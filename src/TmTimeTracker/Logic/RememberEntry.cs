namespace TmTimeTracker.Logic;

public sealed record RememberEntry(
    string TimeOfDay,
    string ContextTag,
    string? TicketKey,
    string Body,
    string SourceFile);
