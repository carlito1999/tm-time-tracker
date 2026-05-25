using System.Text;
using System.Text.RegularExpressions;

namespace TmTimeTracker.Logic;

public static class RememberEntryParser
{
    private static readonly Regex HeaderPattern = new(
        @"^##\s+(?<time>\d{1,2}:\d{2})\s*\|\s*(?<tag>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<RememberEntry> Parse(string? content, string sourceFile)
    {
        if (string.IsNullOrEmpty(content)) return Array.Empty<RememberEntry>();

        var lines = content.Split('\n');
        var result = new List<RememberEntry>();
        Match? activeHeader = null;
        var bodyBuilder = new StringBuilder();

        void Flush()
        {
            if (activeHeader is null) return;
            var tag = activeHeader.Groups["tag"].Value;
            var ticket = TicketKeyExtractor.Extract(tag);
            if (ticket is not null)
            {
                var body = bodyBuilder.ToString().TrimEnd('\n', '\r');
                result.Add(new RememberEntry(
                    TimeOfDay: activeHeader.Groups["time"].Value,
                    ContextTag: tag,
                    TicketKey: ticket,
                    Body: body,
                    SourceFile: sourceFile));
            }
            bodyBuilder.Clear();
            activeHeader = null;
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            var headerMatch = HeaderPattern.Match(line);
            if (headerMatch.Success)
            {
                Flush();
                activeHeader = headerMatch;
            }
            else if (activeHeader is not null)
            {
                if (bodyBuilder.Length > 0) bodyBuilder.Append('\n');
                bodyBuilder.Append(line);
            }
        }
        Flush();

        return result;
    }
}
