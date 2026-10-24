namespace FullyQualified.Web;

public record ToolRequest(string Tool = "", string? Host = null, int? Port = null, string? ServerName = null,
    string? Mode = null, string? Domain = null, string? Selector = null, string? Text = null,
    string? FileBase64 = null, string? RecordType = null, string? SenderIp = null,
    string? EnvelopeFrom = null, string? Helo = null, string? Policy = null,
    string? SubdomainPolicy = null, string? AlignmentSpf = null, string? AlignmentDkim = null,
    string? ReportUri = null, int? Percentage = null);

/// <summary>Resource keys in Title, Label, Explanation and values prefixed Tool_ are localized at the HTTP boundary.</summary>
public record ToolRow(string Label, string Value, string Status = "info", string? Explanation = null);
public record ToolSection(string Title, List<ToolRow> Rows);
public record ToolResult(List<ToolSection> Sections, bool Complete = true);
public sealed class ToolException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
