using System.Text.RegularExpressions;

namespace FullyQualified.Web;

public static class HeaderTools
{
    public static ToolResult Analyze(string text)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(text) > 64 * 1024) throw new ToolException("too_large");
        var fields = new List<(string Name, string Value)>();
        var malformed = false;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0) break;
            if (line[0] is ' ' or '\t')
            {
                if (fields.Count == 0) { malformed = true; continue; }
                var previous = fields[^1];
                fields[^1] = (previous.Name, previous.Value + " " + line.Trim());
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon < 1 || line[..colon].Any(c => c <= 32 || c >= 127)) { malformed = true; continue; }
            fields.Add((line[..colon].ToLowerInvariant(), line[(colon+1)..].Trim()));
        }
        var rows = new List<ToolRow> { new("Tool_HeaderTrust", "Tool_HeaderUnverified", "warning", "Tool_HeaderUnverified") };
        if (malformed) rows.Add(new("Tool_HeaderSyntax", "Tool_InvalidRecord", "warning"));
        var fromFields = fields.Where(x=>x.Name=="from").ToList();
        var from = fromFields.Count == 1 ? AddressDomain(fromFields[0].Value) : null;
        foreach (var (name, value) in fields)
        {
            var label = name switch { "received"=>"Tool_HeaderReceived", "from"=>"Tool_HeaderFrom", "return-path"=>"Tool_HeaderReturnPath", "dkim-signature"=>"Tool_HeaderDkim", "authentication-results"=>"Tool_HeaderAuthentication", "received-spf"=>"Tool_HeaderSpf", _=>null };
            if (label is null) continue;
            rows.Add(new(label, value, "info", "Tool_HeaderUnverified"));
            if (name == "authentication-results")
            {
                foreach(var clause in value.Split(';').Skip(1))
                {
                    var assertion=Regex.Match(clause,@"^\s*(spf|dkim|dmarc)\s*=\s*([a-zA-Z]+)(?:\s|$)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
                    if(!assertion.Success)continue;
                    var method=assertion.Groups[1].Value.ToLowerInvariant();
                    rows.Add(new(method=="spf"?"Tool_HeaderReportedSpf":method=="dkim"?"Tool_HeaderReportedDkim":"Tool_HeaderReportedDmarc",assertion.Groups[2].Value.ToLowerInvariant(),"info","Tool_HeaderUnverified"));
                    if(from is null||method=="dmarc")continue;
                    var property=method=="spf"?@"smtp\.mailfrom":@"header\.d";
                    var identity=Regex.Match(clause,@"(?:^|\s)"+property+@"\s*=\s*([a-zA-Z0-9@._+-]+)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
                    if(!identity.Success)continue;
                    var raw=identity.Groups[1].Value;
                    var other=raw.Contains('@')?AddressDomain(raw):ToolNetwork.NormalizeHost(raw);
                    if(other is not null)rows.Add(new(method=="spf"?"Tool_HeaderSpfAlignment":"Tool_HeaderDkimAlignment",Alignment(from,other),"info","Tool_HeaderAlignmentOnly"));
                }
            }
            if (name == "return-path" && from is not null && AddressDomain(value) is { } envelope)
                rows.Add(new("Tool_HeaderSpfAlignment", Alignment(from,envelope), "info", "Tool_HeaderAlignmentOnly"));
            if (name == "dkim-signature")
            {
                var tags = value.Split(';',StringSplitOptions.TrimEntries).Select(x=>x.Split('=',2)).Where(x=>x.Length==2).ToList();
                foreach (var tag in tags.Where(x=>x[0] is "d" or "s")) rows.Add(new(tag[0]=="d"?"Tool_HeaderSigningDomain":"Tool_HeaderSelector",tag[1]));
                var identities = tags.Where(x=>x[0]=="d").ToList();
                if (identities.Count==1 && from is not null && ToolNetwork.NormalizeHost(identities[0][1]) is {} signing)
                    rows.Add(new("Tool_HeaderDkimAlignment",Alignment(from,signing),"info","Tool_HeaderAlignmentOnly"));
            }
        }
        if (from is null) rows.Add(new("Tool_HeaderFrom", "Tool_HeaderIdentityUnknown", "warning"));
        return new([new("Tool_Headers",rows)], !malformed);
    }
    static string? AddressDomain(string value)
    {
        var matches = Regex.Matches(value, @"@([a-zA-Z0-9.-]+)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return matches.Count == 1 ? ToolNetwork.NormalizeHost(matches[0].Groups[1].Value) : null;
    }
    static string Alignment(string from,string other) => from.Equals(other,StringComparison.OrdinalIgnoreCase)
        ? "Tool_AlignmentStrict" : PublicSuffix.OrganizationalDomain(from) is {} org && org==PublicSuffix.OrganizationalDomain(other)
        ? "Tool_AlignmentRelaxed" : "Tool_AlignmentNone";
}
