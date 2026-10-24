using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using DnsClient;
using DnsClient.Protocol;

namespace FullyQualified.Web;

/// <summary>Read-only, per-request DNS and mail-policy diagnostics. No target/result retention.</summary>
public class MailTools(ToolNetwork network)
{
    public async Task<ToolResult> RunAsync(ToolRequest request, CancellationToken ct)
    {
        if (request.Tool == "dns") return await DnsAsync(request,ct);
        if (request.Tool == "dmarc-builder") return BuildDmarc(request);
        var domain = ToolNetwork.NormalizeHost(request.Domain) ?? throw new ToolException("invalid_host");
        if (IPAddress.TryParse(domain,out _)) throw new ToolException("invalid_host");
        return request.Tool switch
        {
            "dkim" => await DkimAsync(domain,request.Selector,ct),
            "dmarc" => await DmarcAsync(domain,ct),
            "mta-sts" => await MtaAsync(domain,ct),
            _ => throw new ToolException("invalid_tool")
        };
    }

    internal static async Task<List<string>> TxtAsync(ToolNetwork network,string name,CancellationToken ct)
    {
        var response = await network.QueryAsync(name,QueryType.TXT,ct);
        CheckResponse(response);
        return response.Answers.TxtRecords().Select(x=>string.Concat(x.Text)).ToList();
    }
    internal static void CheckResponse(IDnsQueryResponse response)
    {
        if (response.Header.ResultTruncated) throw new ToolException("dns_incomplete");
        if (response.Header.ResponseCode is not (DnsHeaderResponseCode.NoError or DnsHeaderResponseCode.NotExistentDomain))
            throw new ToolException("dns_failed");
    }
    internal static Dictionary<string,string>? Tags(string value)
    {
        var result = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in value.Split(';',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries))
        {
            var pair=part.Split('=',2,StringSplitOptions.TrimEntries);
            if(pair.Length!=2 || pair[0].Length==0 || !result.TryAdd(pair[0],pair[1])) return null;
        }
        return result;
    }
    static ToolResult Single(string title,List<ToolRow> rows,bool complete=true)=>new([new(title,rows)],complete);
    static ToolRow Invalid(string label="Tool_Record")=>new(label,"Tool_InvalidRecord","warning");

    async Task<ToolResult> DkimAsync(string domain,string? selector,CancellationToken ct)
    {
        var selectors=Dkim.ParseSelectors(selector);
        if(selectors is not {Count:1}) throw new ToolException("invalid_selector");
        var records=await TxtAsync(network,$"{selectors[0]}._domainkey.{domain}",ct);
        var candidates=records.Where(x=>x.Contains("p=",StringComparison.OrdinalIgnoreCase)||x.StartsWith("v=DKIM1",StringComparison.OrdinalIgnoreCase)).ToList();
        var rows=new List<ToolRow>{new("Tool_DkimVerification","Tool_DkimDiscoveryOnly","info","Tool_DkimDiscoveryOnly")};
        foreach(var record in records) rows.Add(new("Tool_Record",record));
        if(candidates.Count==0) { rows.Add(new("Tool_DkimState","Tool_MissingRecord","warning")); return Single("Tool_Dkim",rows); }
        var tags=candidates.Count==1?Tags(candidates[0]):null;
        if(tags is null || !tags.ContainsKey("p") || (tags.TryGetValue("v",out var version)&&version!="DKIM1")
            || (tags.TryGetValue("k",out var kind)&&kind is not ("rsa" or "ed25519")))
        { rows.Add(Invalid("Tool_DkimState")); return Single("Tool_Dkim",rows); }
        if(string.IsNullOrWhiteSpace(tags["p"])) {rows.Add(new("Tool_DkimState","Tool_DkimRevoked","warning"));return Single("Tool_Dkim",rows);}
        try
        {
            var bytes=Convert.FromBase64String(string.Concat(tags["p"].Where(c=>!char.IsWhiteSpace(c))));
            var keyType=tags.GetValueOrDefault("k","rsa");
            int bits;
            if(keyType=="ed25519") {if(bytes.Length!=32) throw new FormatException();bits=256;}
            else {using var rsa=RSA.Create();rsa.ImportSubjectPublicKeyInfo(bytes,out var consumed);if(consumed!=bytes.Length) throw new FormatException();bits=rsa.KeySize;}
            rows.Add(new("Tool_DkimKeyType",keyType));
            rows.Add(new("Tool_DkimKeyBits",bits.ToString(System.Globalization.CultureInfo.InvariantCulture),keyType=="rsa"&&bits<2048?"warning":"info"));
            rows.Add(new("Tool_DkimState","Tool_ValidRecord"));
        }
        catch(Exception ex) when(ex is FormatException or CryptographicException or ArgumentException) { rows.Add(Invalid("Tool_DkimState")); }
        return Single("Tool_Dkim",rows);
    }

    async Task<ToolResult> DmarcAsync(string domain,CancellationToken ct)
    {
        var source=domain;
        var records=(await TxtAsync(network,"_dmarc."+domain,ct)).Where(IsDmarc).ToList();
        if(records.Count==0 && PublicSuffix.OrganizationalDomain(domain) is {} org && org!=domain)
        { source=org;records=(await TxtAsync(network,"_dmarc."+source,ct)).Where(IsDmarc).ToList(); }
        var rows=new List<ToolRow>{new("Tool_DmarcSource",source)};
        foreach(var record in records) rows.Add(new("Tool_Record",record));
        if(records.Count==0) { rows.Add(new("Tool_Record","Tool_MissingRecord","warning"));return Single("Tool_Dmarc",rows); }
        var tags=records.Count==1?Tags(records[0]):null;
        if(tags is null||!ValidDmarc(tags,records[0])) {rows.Add(Invalid());return Single("Tool_Dmarc",rows);}
        rows.Add(new("Tool_DmarcEffectivePolicy",source==domain?tags["p"]:tags.GetValueOrDefault("sp",tags["p"])));
        rows.Add(new("Tool_DmarcSpfAlignment",tags.GetValueOrDefault("aspf","r")=="s"?"Tool_AlignmentStrict":"Tool_AlignmentRelaxed"));
        rows.Add(new("Tool_DmarcDkimAlignment",tags.GetValueOrDefault("adkim","r")=="s"?"Tool_AlignmentStrict":"Tool_AlignmentRelaxed"));
        var complete=true;
        foreach(var key in new[]{"rua","ruf"})
        foreach(var destination in tags.GetValueOrDefault(key,"").Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries))
        {
            rows.Add(new("Tool_ReportDestination",destination));
            var uriText=destination.Split('!',2)[0];
            if(!Uri.TryCreate(uriText,UriKind.Absolute,out var uri)||uri.Scheme!="mailto"||!uri.OriginalString.Contains('@')) {rows.Add(Invalid("Tool_ReportDestination"));continue;}
            var reportDomain=ToolNetwork.NormalizeHost(uri.OriginalString[(uri.OriginalString.LastIndexOf('@')+1)..]);
            if(reportDomain is null) {rows.Add(Invalid("Tool_ReportDestination"));continue;}
            var sourceOrg=PublicSuffix.OrganizationalDomain(source);
            if(reportDomain==source || sourceOrg is not null&&sourceOrg==PublicSuffix.OrganizationalDomain(reportDomain)) continue;
            try
            {
                var authorization=await TxtAsync(network,$"{source}._report._dmarc.{reportDomain}",ct);
                var valid=authorization.Any(x=>IsDmarc(x)&&Tags(x) is {} auth&&auth.GetValueOrDefault("v")=="DMARC1");
                rows.Add(new("Tool_ReportAuthorization",valid?"Tool_ReportAuthorized":"Tool_ReportUnauthorized",valid?"info":"warning"));
            }
            catch(Exception ex) when(ex is ToolException or OperationCanceledException) {complete=false;rows.Add(new("Tool_ReportAuthorization","Tool_DnsError","warning"));}
        }
        return Single("Tool_Dmarc",rows,complete);
    }
    static bool IsDmarc(string record)=>record.StartsWith("v=DMARC1",StringComparison.OrdinalIgnoreCase)
        && (record.Length==8||record[8] is ';' or ' ' or '\t');
    static bool ValidDmarc(Dictionary<string,string> tags,string record)=>record.StartsWith("v=DMARC1;",StringComparison.Ordinal)
        &&tags.GetValueOrDefault("v")=="DMARC1"&&tags.GetValueOrDefault("p") is "none" or "quarantine" or "reject"
        &&(!tags.TryGetValue("sp",out var sp)||sp is "none" or "quarantine" or "reject")
        &&(!tags.TryGetValue("aspf",out var aspf)||aspf is "r" or "s")&&(!tags.TryGetValue("adkim",out var adkim)||adkim is "r" or "s")
        &&(!tags.TryGetValue("pct",out var pct)||int.TryParse(pct,out var n)&&n is >=0 and <=100);
    static ToolResult BuildDmarc(ToolRequest request)
    {
        if(request.Policy is not ("none" or "quarantine" or "reject") || request.SubdomainPolicy is not (null or "" or "none" or "quarantine" or "reject")
            ||request.AlignmentSpf is not(null or "" or "r" or "s")||request.AlignmentDkim is not(null or "" or "r" or "s")||request.Percentage is <0 or >100)
            throw new ToolException("invalid_policy");
        var record=$"v=DMARC1; p={request.Policy}; aspf={(string.IsNullOrEmpty(request.AlignmentSpf)?"r":request.AlignmentSpf)}; adkim={(string.IsNullOrEmpty(request.AlignmentDkim)?"r":request.AlignmentDkim)}; pct={request.Percentage??100}";
        if(!string.IsNullOrEmpty(request.SubdomainPolicy)) record+="; sp="+request.SubdomainPolicy;
        if(!string.IsNullOrWhiteSpace(request.ReportUri))
        {
            foreach(var part in request.ReportUri.Split(',',StringSplitOptions.TrimEntries))
                if(part.Contains(';')||part.Any(char.IsControl)||!ValidReportMailbox(part)) throw new ToolException("invalid_policy");
            record+="; rua="+request.ReportUri;
        }
        return Single("Tool_DmarcBuilder",[new("Tool_Record",record),new("Tool_DmarcBuilderExplanation","Tool_DmarcBuilderTextOnly"),
            new("Tool_DmarcEffectivePolicy",request.Policy,"info","Tool_DmarcPolicyExplanation"),
            new("Tool_DmarcSpfAlignment",request.AlignmentSpf=="s"?"Tool_AlignmentStrict":"Tool_AlignmentRelaxed","info","Tool_DmarcAlignmentExplanation"),
            new("Tool_DmarcDkimAlignment",request.AlignmentDkim=="s"?"Tool_AlignmentStrict":"Tool_AlignmentRelaxed","info","Tool_DmarcAlignmentExplanation"),
            new("Tool_DmarcPercentage",(request.Percentage??100).ToString(),"info","Tool_DmarcPercentageExplanation")]);
    }

    static bool ValidReportMailbox(string text)
    {
        if(!text.StartsWith("mailto:",StringComparison.OrdinalIgnoreCase))return false;
        var address=text[7..];
        if(address.Contains('?')||address.Any(char.IsControl))return false;
        return MailAddress.TryCreate(address,out var mailbox)&&mailbox.Address==address
            &&ToolNetwork.NormalizeHost(mailbox.Host) is not null;
    }

    async Task<ToolResult> MtaAsync(string domain,CancellationToken ct)
    {
        var rows=new List<ToolRow>();var complete=true;
        var records=(await TxtAsync(network,"_mta-sts."+domain,ct)).Where(x=>x.StartsWith("v=STSv1",StringComparison.Ordinal)).ToList();
        foreach(var record in records) rows.Add(new("Tool_MtaDiscovery",record));
        var tags=records.Count==1?Tags(records[0]):null;
        if(tags is null||tags.GetValueOrDefault("v")!="STSv1"||!tags.TryGetValue("id",out var id)||id.Length is <1 or >32||id.Any(c=>!char.IsAsciiLetterOrDigit(c))) rows.Add(Invalid("Tool_MtaDiscovery"));
        try
        {
            var text=await network.FetchPolicyAsync("mta-sts."+domain,ct);
            if(System.Text.Encoding.UTF8.GetByteCount(text)>network.MaxPolicyBytes) throw new ToolException("too_large");
            rows.Add(new("Tool_MtaPolicy",text));
            var values=new Dictionary<string,List<string>>(StringComparer.Ordinal);
            var valid=true;
            foreach(var line in text.Replace("\r\n","\n").Split('\n',StringSplitOptions.RemoveEmptyEntries))
            { var pair=line.Split(':',2,StringSplitOptions.TrimEntries);if(pair.Length!=2){valid=false;continue;} if(!values.TryGetValue(pair[0],out var list))values[pair[0]]=list=[];list.Add(pair[1]); }
            string? One(string key)=>values.TryGetValue(key,out var list)&&list.Count==1?list[0]:null;
            var mode=One("mode");
            var patterns=values.GetValueOrDefault("mx")??[];
            valid &= One("version")=="STSv1"&&mode is "none" or "testing" or "enforce"&&long.TryParse(One("max_age"),out var age)&&age is >=0 and <=31557600;
            valid &= patterns.All(p=>ToolNetwork.NormalizeHost(p.StartsWith("*.")?p[2..]:p) is not null)&& (mode=="none"||patterns.Count>0);
            rows.Add(valid?new("Tool_MtaSyntax","Tool_ValidRecord"):Invalid("Tool_MtaSyntax"));
            if(valid&&mode!="none")
            {
                var mx=await network.QueryAsync(domain,QueryType.MX,ct);CheckResponse(mx);
                foreach(var host in mx.Answers.MxRecords())
                {
                    var name=host.Exchange.Value.TrimEnd('.').ToLowerInvariant();
                    var covered=patterns.Any(p=>p.StartsWith("*.")?name.EndsWith(p[1..],StringComparison.OrdinalIgnoreCase)&&name.Length>p.Length-1&&name.Count(c=>c=='.')==p.Count(c=>c=='.'):name.Equals(p,StringComparison.OrdinalIgnoreCase));
                    rows.Add(new("Tool_MtaMxCoverage",name,covered?"info":"warning",covered?"Tool_MtaMxCovered":"Tool_MtaMxUncovered"));
                }
                if(!mx.Answers.MxRecords().Any()) rows.Add(new("Tool_MtaMxCoverage","Tool_MissingRecord","warning"));
            }
        }
        catch(Exception ex) when(ex is ToolException or HttpRequestException or IOException or OperationCanceledException)
        {complete=false;rows.Add(new("Tool_MtaPolicy","Tool_PolicyUnavailable","warning"));}
        try
        {
            var reporting=(await TxtAsync(network,"_smtp._tls."+domain,ct)).Where(x=>x.StartsWith("v=TLSRPTv1",StringComparison.Ordinal)).ToList();
            if(reporting.Count==0)rows.Add(new("Tool_TlsRpt","Tool_MissingRecord","warning"));
            foreach(var report in reporting) rows.Add(new("Tool_TlsRpt",report));
            var rpt=reporting.Count==1?Tags(reporting[0]):null;
            if(reporting.Count>0)
            {
                var valid=rpt?.GetValueOrDefault("v")=="TLSRPTv1"&&rpt.TryGetValue("rua",out var rua)&&rua.Split(',',StringSplitOptions.TrimEntries).All(x=>Uri.TryCreate(x,UriKind.Absolute,out var u)&&u.Scheme is "https" or "mailto");
                rows.Add(valid?new("Tool_TlsRptSyntax","Tool_ValidRecord"):Invalid("Tool_TlsRptSyntax"));
            }
        }
        catch(Exception ex) when(ex is ToolException or OperationCanceledException){complete=false;rows.Add(new("Tool_TlsRpt","Tool_DnsError","warning"));}
        return Single("Tool_MtaSts",rows,complete);
    }

    async Task<ToolResult> DnsAsync(ToolRequest request,CancellationToken ct)
    {
        var name=DigService.NormalizeName(request.Host)??throw new ToolException("invalid_host");
        var typeText=(request.RecordType??"A").ToUpperInvariant();
        if(!DigService.Types.Contains(typeText)||!Enum.TryParse<QueryType>(typeText,out var type)) throw new ToolException("invalid_type");
        if(type==QueryType.PTR&&IPAddress.TryParse(name,out var ip))
            name=ip.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork?string.Join('.',ip.GetAddressBytes().Reverse())+".in-addr.arpa":string.Join('.',Convert.ToHexString(ip.GetAddressBytes()).ToLowerInvariant().Reverse())+".ip6.arpa";
        var sections=new List<ToolSection>();var complete=true;string? baseline=null;
        async Task Observe(string label,IPAddress? address)
        {
            var rows=new List<ToolRow>{new("Tool_DnsResolver",label)};var watch=Stopwatch.StartNew();
            try
            {
                var response=address is null?await network.QueryAsync(name,type,ct):await network.QueryAtAsync(name,type,address,ct);
                rows.Add(new("Tool_DnsResponse",response.Header.ResponseCode.ToString(),response.HasError?"warning":"info"));
                if(response.Header.ResultTruncated){complete=false;rows.Add(new("Tool_DnsResponse","Tool_DnsIncomplete","warning"));}
                foreach(var record in response.Answers) rows.Add(new("Tool_DnsAnswer",record.ToString()));
                if(response.Answers.Count==0&&!response.HasError)rows.Add(new("Tool_DnsAnswer","Tool_DnsEmpty"));
                var normalized=string.Join('\n',response.Answers.Select(Canonical).Distinct().Order(StringComparer.Ordinal));
                normalized=response.Header.ResponseCode+":"+normalized;
                if(baseline is null)baseline=normalized;
                else rows.Add(new("Tool_DnsComparison",normalized==baseline?"Tool_DnsSame":"Tool_DnsDifferent",normalized==baseline?"info":"warning"));
            }
            catch(ToolException){complete=false;rows.Add(new("Tool_DnsResponse","Tool_DnsError","warning"));}
            rows.Add(new("Tool_DnsMilliseconds",watch.ElapsedMilliseconds.ToString()));sections.Add(new("Tool_Dns",rows));
        }
        try
        {
        await Observe("Tool_DnsConfigured",null);
        foreach(var server in (network.Configuration["Dns:CompareResolvers"]??"1.1.1.1,9.9.9.9,8.8.8.8").Split([',',' '],StringSplitOptions.RemoveEmptyEntries).Distinct().Take(8))
            if(IPAddress.TryParse(server,out var serverIp))await Observe(server,serverIp);
        try
        {
            var zone=name;var nameservers=new List<string>();
            for(var depth=0;depth<16;depth++)
            {
                var response=await network.QueryAsync(zone,QueryType.NS,ct);CheckResponse(response);
                nameservers=response.Answers.Concat(response.Authorities).OfType<NsRecord>().Select(x=>x.NSDName.Value.TrimEnd('.')).Distinct().ToList();
                if(nameservers.Count>0)break;
                var dot=zone.IndexOf('.');if(dot<0)break;zone=zone[(dot+1)..];
            }
            if(nameservers.Count>10){complete=false;sections.Add(new("Tool_Dns",[new("Tool_Limit","Tool_DnsNsLimit","warning")]));}
            foreach(var ns in nameservers.Take(10))
            {
                try
                {
                    var addresses=await network.ResolveAsync(ns,ct);
                    await network.GuardAddressesAsync(addresses,ct);
                    if(addresses.Length>network.MaxAddresses){complete=false;sections.Add(new("Tool_Dns",[new("Tool_Limit","Tool_DnsAddressLimit","warning")]));}
                    foreach(var address in addresses.Take(network.MaxAddresses))await Observe(ns+" ("+address+")",address);
                }
                catch(ToolException){complete=false;sections.Add(new("Tool_Dns",[new("Tool_DnsResolver",ns),new("Tool_DnsResponse","Tool_DnsError","warning")]));}
            }
        }
        catch(ToolException){complete=false;sections.Add(new("Tool_Dns",[new("Tool_DnsResponse","Tool_DnsError","warning")]));}
        }
        catch(OperationCanceledException){complete=false;sections.Add(new("Tool_Dns",[new("Tool_DnsResponse","Tool_RunIncomplete","warning")]));}
        return new(sections,complete);
    }
    // TTL is excluded; only domain-valued RDATA is case-folded (TXT data remains case-sensitive).
    internal static string Canonical(DnsResourceRecord record)
    {
        var parts=record.ToString().Split([' ','\t'],5,StringSplitOptions.RemoveEmptyEntries);
        var data=parts.Length==5?parts[4]:record.ToString();
        if(record is NsRecord or CNameRecord or MxRecord or PtrRecord or SoaRecord or SrvRecord)data=data.ToLowerInvariant();
        return record.DomainName.Value.ToLowerInvariant()+" "+record.RecordType+" "+data;
    }
}
