using TmTimeTracker.Jira;

namespace TmTimeTracker.UI;

internal static class WorklogRequestFactory
{
    public static WorklogRequest Build(int minutes, string description, DateTimeOffset localNow)
    {
        var content = new WorklogContent("paragraph",
            new[] { new WorklogTextNode("text", description) });
        var comment = new WorklogComment("doc", 1, new[] { content });
        var offset = localNow.Offset;
        var sign = offset.Ticks >= 0 ? "+" : "-";
        var started = $"{localNow:yyyy-MM-ddTHH:mm:ss.fff}{sign}{Math.Abs(offset.Hours):D2}{Math.Abs(offset.Minutes):D2}";
        return new WorklogRequest(TimeSpentSeconds: minutes * 60, StartedIso: started, Comment: comment);
    }
}
