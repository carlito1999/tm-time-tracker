namespace TmTimeTracker.Data;

public sealed record TicketCycle(
    long Id,
    string TicketKey,
    DateTime CycleStarted,
    int MinutesActive,
    string? LastSeenStatus,
    DateTime? LastPolled,
    DateTime? SubmittedAt,
    string? WorklogId,
    int? SubmittedMinutes);
