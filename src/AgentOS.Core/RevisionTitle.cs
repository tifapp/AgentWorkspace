namespace AgentOS.Core;

public static class RevisionTitle
{
    public const string Marker = "Revision title: ";

    public static string? FromReport(string? report)
    {
        if (string.IsNullOrEmpty(report)) return null;
        var newline = report.IndexOf('\n');
        var firstLine = (newline < 0 ? report : report[..newline]).TrimEnd('\r').Trim();
        if (!firstLine.StartsWith(Marker, StringComparison.OrdinalIgnoreCase)) return null;
        var title = firstLine[Marker.Length..].Trim();
        return title.Length is > 0 and <= 110 ? title : null;
    }
}
