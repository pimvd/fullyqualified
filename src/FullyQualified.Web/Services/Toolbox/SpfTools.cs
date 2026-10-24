using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using DnsClient;
using DnsClient.Protocol;

namespace FullyQualified.Web;

/// <summary>Bounded SPF graph analysis and a separate optional concrete sender evaluation.</summary>
public sealed class SpfTools(ToolNetwork network)
{
    sealed record Term(string Name,string? Argument,char Qualifier,int V4=32,int V6=128);
    sealed record Policy(List<Term> Terms,string? Redirect);
    sealed class State(List<ToolRow> rows)
    {
        public List<ToolRow> Rows {get;}=rows;
        public int Lookups;public int Voids;public int Work;public bool Complete=true;
        public HashSet<string> Path {get;}=new(StringComparer.OrdinalIgnoreCase);
        public void Problem(string key){Rows.Add(new("Tool_SpfFinding",key,"warning"));Complete=false;}
    }

    public async Task<ToolResult> RunAsync(ToolRequest request,CancellationToken ct)
    {
        var domain=ToolNetwork.NormalizeHost(request.Domain)??throw new ToolException("invalid_host");
        if(IPAddress.TryParse(domain,out _))throw new ToolException("invalid_host");
        var staticRows=new List<ToolRow>{new("Tool_SpfScope","Tool_SpfStaticOnly","info","Tool_SpfStaticOnly")};
        var graph=new State(staticRows);
        try { await Expand(domain,graph,ct); }
        catch(OperationCanceledException) { graph.Problem("Tool_RunIncomplete"); }
        staticRows.Add(new("Tool_SpfStaticLookups",graph.Lookups.ToString(),graph.Lookups>10?"warning":"info","Tool_SpfStaticBudget"));
        if(graph.Lookups>10)graph.Problem("Tool_SpfLookupLimit");
        var sections=new List<ToolSection>{new("Tool_SpfStatic",staticRows)};
        var complete=graph.Complete;
        if(!string.IsNullOrWhiteSpace(request.SenderIp))
        {
            if(!IPAddress.TryParse(request.SenderIp,out var sender))throw new ToolException("invalid_ip");
            if(sender.IsIPv4MappedToIPv6)sender=sender.MapToIPv4();
            var evaluationDomain=domain;
            if(!string.IsNullOrWhiteSpace(request.EnvelopeFrom)&&request.EnvelopeFrom!="<>")
            {
                var senderText=request.EnvelopeFrom.Trim('<','>');
                var at=senderText.LastIndexOf('@');
                if(at<1||ToolNetwork.NormalizeHost(senderText[(at+1)..]) is not {} identity)throw new ToolException("invalid_sender");
                evaluationDomain=identity;
            }
            else if(request.EnvelopeFrom=="<>") evaluationDomain=ToolNetwork.NormalizeHost(request.Helo)??throw new ToolException("invalid_sender");
            var rows=new List<ToolRow>{new("Tool_SpfIdentity",evaluationDomain),new("Tool_SpfSenderIp",sender.ToString())};
            var state=new State(rows);
            string outcome;
            try { outcome=await Evaluate(evaluationDomain,sender,state,ct); }
            catch(OperationCanceledException) { state.Problem("Tool_RunIncomplete");outcome="unknown"; }
            rows.Add(new("Tool_SpfEvaluation",outcome,outcome=="pass"?"info":"warning","Tool_SpfEvaluationExplanation"));
            rows.Add(new("Tool_SpfActualLookups",state.Lookups.ToString()));rows.Add(new("Tool_SpfVoidLookups",state.Voids.ToString()));
            sections.Add(new("Tool_SpfConcrete",rows));complete&=state.Complete;
        }
        return new(sections,complete);
    }

    async Task<Policy?> Read(string domain,State state,CancellationToken ct)
    {
        if(++state.Work>128){state.Problem("Tool_SpfWorkLimit");return null;}
        try
        {
            var records=(await MailTools.TxtAsync(network,domain,ct)).Where(x=>x.Equals("v=spf1",StringComparison.OrdinalIgnoreCase)||x.StartsWith("v=spf1 ",StringComparison.OrdinalIgnoreCase)||x.StartsWith("v=spf1\t",StringComparison.OrdinalIgnoreCase)).ToList();
            foreach(var record in records)state.Rows.Add(new("Tool_SpfRecord",domain+": "+record));
            if(records.Count!=1){state.Problem(records.Count==0?"Tool_SpfMissing":"Tool_SpfMultiple");return null;}
            var policy=Parse(records[0]);
            if(policy is null)state.Problem("Tool_SpfSyntax");
            return policy;
        }
        catch(ToolException){state.Problem("Tool_DnsError");return null;}
    }

    static Policy? Parse(string record)
    {
        var words=record.Split([' ','\t'],StringSplitOptions.RemoveEmptyEntries);
        var terms=new List<Term>();var modifiers=new HashSet<string>(StringComparer.Ordinal);string? redirect=null;
        foreach(var word in words.Skip(1))
        {
            if(word.Contains('='))
            {
                var pair=word.Split('=',2);var key=pair[0].ToLowerInvariant();
                if(!Regex.IsMatch(key,@"^[a-zA-Z][a-zA-Z0-9_.-]*$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100))||!modifiers.Add(key)||pair[1].Length==0)return null;
                if(key=="redirect")redirect=pair[1];
                continue;
            }
            var qualifier=word[0] is '+' or '-' or '~' or '?'?word[0]:'+';
            var body=word[0] is '+' or '-' or '~' or '?'?word[1..]:word;
            var match=Regex.Match(body,@"^(all|include|a|mx|ptr|ip4|ip6|exists)(?::([^/]+))?(?:/([0-9]+))?(?://([0-9]+))?$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
            if(!match.Success)return null;
            var name=match.Groups[1].Value.ToLowerInvariant();var arg=match.Groups[2].Success?match.Groups[2].Value:null;
            var v4=32;var v6=128;
            if(match.Groups[3].Success&&!int.TryParse(match.Groups[3].Value,out v4)||match.Groups[4].Success&&!int.TryParse(match.Groups[4].Value,out v6))return null;
            if(name=="ip4"&&match.Groups[4].Success)return null;
            if(name=="ip6")
            { v6=match.Groups[3].Success?v4:128;v4=32;if(match.Groups[4].Success)return null; }
            if(v4 is <0 or >32||v6 is <0 or >128)return null;
            if(name is "include" or "exists" or "ip4" or "ip6" &&arg is null)return null;
            if(name=="all"&&(arg is not null||match.Groups[3].Success||match.Groups[4].Success))return null;
            if(name is "include" or "exists" or "ptr" &&(match.Groups[3].Success||match.Groups[4].Success))return null;
            if(name is "ip4" or "ip6" &&(!IPAddress.TryParse(arg,out var parsed)||parsed.AddressFamily!=(name=="ip4"?AddressFamily.InterNetwork:AddressFamily.InterNetworkV6)))return null;
            terms.Add(new(name,arg,qualifier,v4,v6));
        }
        return new(terms,redirect);
    }

    async Task Expand(string domain,State state,CancellationToken ct)
    {
        if(state.Path.Count>=20){state.Problem("Tool_SpfWorkLimit");return;}
        if(!state.Path.Add(domain)){state.Problem("Tool_SpfCycle");return;}
        try
        {
            var policy=await Read(domain,state,ct);if(policy is null)return;
            var terminal=false;
            foreach(var term in policy.Terms)
            {
                ct.ThrowIfCancellationRequested();
                if(term.Name=="all")
                { terminal=true;if(term.Qualifier=='+')state.Rows.Add(new("Tool_SpfFinding","Tool_SpfPermissive","warning"));break; }
                if(term.Name is "a" or "mx" or "include" or "exists" or "ptr")state.Lookups++;
                if(state.Lookups>128){state.Problem("Tool_SpfWorkLimit");return;}
                if(term.Name=="ptr")state.Rows.Add(new("Tool_SpfFinding","Tool_SpfPtrDiscouraged","warning"));
                if(term.Name=="include")
                { if(PlainDomain(term.Argument) is {} target)await Expand(target,state,ct);else state.Problem("Tool_SpfMacroUnknown"); }
            }
            if(!terminal&&policy.Redirect is not null)
            {state.Lookups++;if(PlainDomain(policy.Redirect) is {} target)await Expand(target,state,ct);else state.Problem("Tool_SpfMacroUnknown");terminal=true;}
            if(!terminal)state.Rows.Add(new("Tool_SpfFinding","Tool_SpfNoTerminal","warning"));
        }
        finally{state.Path.Remove(domain);}
    }
    static string? PlainDomain(string? value)=>value is null||value.Contains('%')?null:ToolNetwork.NormalizeHost(value);
    static bool Budget(State state)
    {if(++state.Lookups<=10)return true;state.Problem("Tool_SpfLookupLimit");return false;}
    async Task<IDnsQueryResponse?> Query(string domain,QueryType type,State state,CancellationToken ct)
    {
        try
        {
            var response=await network.QueryAsync(domain,type,ct);MailTools.CheckResponse(response);
            if(response.Header.ResponseCode==DnsHeaderResponseCode.NotExistentDomain||response.Answers.Count==0)
                if(++state.Voids>2){state.Problem("Tool_SpfVoidLimit");return null;}
            return response;
        }
        catch(ToolException){state.Problem("Tool_DnsError");return null;}
    }
    async Task<string> Evaluate(string domain,IPAddress sender,State state,CancellationToken ct)
    {
        if(state.Path.Count>=20){state.Problem("Tool_SpfWorkLimit");return "permerror";}
        if(!state.Path.Add(domain)){state.Problem("Tool_SpfCycle");return "permerror";}
        try
        {
            var policy=await Read(domain,state,ct);if(policy is null)return "unknown";
            foreach(var term in policy.Terms)
            {
                ct.ThrowIfCancellationRequested();
                var matches=false;
                if(term.Name is "a" or "mx" or "include" or "exists" or "ptr")if(!Budget(state))return "permerror";
                if(term.Name=="all")matches=true;
                else if(term.Name is "ip4" or "ip6")matches=Prefix(sender,IPAddress.Parse(term.Argument!),sender.AddressFamily==AddressFamily.InterNetwork?term.V4:term.V6);
                else
                {
                    var target=term.Argument is null?domain:PlainDomain(term.Argument);
                    if(target is null){state.Problem("Tool_SpfMacroUnknown");return "unknown";}
                    switch(term.Name)
                    {
                        case "include":
                            var included=await Evaluate(target,sender,state,ct);
                            if(included is "permerror" or "temperror" or "unknown")return included;
                            matches=included=="pass";break;
                        case "ptr":state.Problem("Tool_SpfPtrUnknown");return "unknown";
                        case "exists":
                            var exists=await Query(target,QueryType.A,state,ct);if(exists is null)return "unknown";
                            matches=exists.Answers.ARecords().Any();break;
                        case "a":
                            var a=await Query(target,sender.AddressFamily==AddressFamily.InterNetwork?QueryType.A:QueryType.AAAA,state,ct);if(a is null)return "unknown";
                            matches=Addresses(a).Any(x=>Prefix(sender,x,sender.AddressFamily==AddressFamily.InterNetwork?term.V4:term.V6));break;
                        case "mx":
                            var mx=await Query(target,QueryType.MX,state,ct);if(mx is null)return "unknown";
                            var hosts=mx.Answers.MxRecords().ToList();
                            if(hosts.Count>10){state.Problem("Tool_SpfMxLimit");return "permerror";}
                            foreach(var host in hosts)
                            {
                                if(host.Exchange.Value==".")continue;
                                var ips=await Query(host.Exchange.Value,sender.AddressFamily==AddressFamily.InterNetwork?QueryType.A:QueryType.AAAA,state,ct);if(ips is null)return "unknown";
                                var addresses=Addresses(ips).ToList();
                                if(addresses.Count>10){state.Problem("Tool_SpfMxLimit");return "permerror";}
                                if(addresses.Any(x=>Prefix(sender,x,sender.AddressFamily==AddressFamily.InterNetwork?term.V4:term.V6))){matches=true;break;}
                            }
                            break;
                    }
                }
                if(matches)return term.Qualifier switch{'-'=>"fail",'~'=>"softfail",'?'=>"neutral",_=>"pass"};
            }
            if(policy.Redirect is not null)
            {
                if(!Budget(state))return "permerror";
                var target=PlainDomain(policy.Redirect);if(target is null){state.Problem("Tool_SpfMacroUnknown");return "unknown";}
                return await Evaluate(target,sender,state,ct);
            }
            return "neutral";
        }
        finally{state.Path.Remove(domain);}
    }
    static IEnumerable<IPAddress> Addresses(IDnsQueryResponse response)=>response.Answers.ARecords().Select(x=>x.Address).Concat(response.Answers.AaaaRecords().Select(x=>x.Address));
    static bool Prefix(IPAddress address,IPAddress networkAddress,int bits)
    {
        if(address.AddressFamily!=networkAddress.AddressFamily)return false;
        var a=address.GetAddressBytes();var b=networkAddress.GetAddressBytes();
        for(var i=0;i<a.Length;i++){var count=Math.Clamp(bits-i*8,0,8);var mask=255<<(8-count)&255;if((a[i]&mask)!=(b[i]&mask))return false;}return true;
    }
}
