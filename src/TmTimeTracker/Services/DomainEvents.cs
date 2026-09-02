using TmTimeTracker.Logic;

namespace TmTimeTracker.Services;

public abstract record DomainEvent(DateTime AtUtc);

public sealed record ActivityChanged(UserActivityState State, DateTime AtUtc) : DomainEvent(AtUtc);
public sealed record BranchChanged(string? Branch, string? TicketKey, DateTime AtUtc) : DomainEvent(AtUtc);
public sealed record RememberEntriesObserved(IReadOnlyList<RememberEntry> Entries, string EntryDate, DateTime AtUtc) : DomainEvent(AtUtc);
public sealed record JiraStatusTransition(
    string TicketKey, string? Summary, string FromStatus, string ToStatus,
    int MinutesActive, DateTime AtUtc) : DomainEvent(AtUtc);
public sealed record WorklogSubmitted(string TicketKey, string WorklogId, int Minutes, DateTime AtUtc) : DomainEvent(AtUtc);
public sealed record AuthenticationRequired(string Reason, DateTime AtUtc) : DomainEvent(AtUtc);
