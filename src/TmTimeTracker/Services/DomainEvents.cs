using TmTimeTracker.Logic;

namespace TmTimeTracker.Services;

public abstract record DomainEvent(DateTime AtUtc);

public sealed record ActivityChanged(UserActivityState State, DateTime AtUtc) : DomainEvent(AtUtc);
// A dashboard-log event only. Attribution is pulled per repo at the aggregator's tick, so nothing
// bills time off the back of this; RepoPath is here to tell two concurrently-active repos apart.
public sealed record BranchChanged(string? RepoPath, string? Branch, string? TicketKey, DateTime AtUtc) : DomainEvent(AtUtc);
public sealed record RememberEntriesObserved(IReadOnlyList<RememberEntry> Entries, string EntryDate, DateTime AtUtc) : DomainEvent(AtUtc);
// IssueId is Jira's numeric id, carried because the development-information endpoint keys on it
// rather than on the issue key. Trailing and optional so existing consumers are unaffected.
public sealed record JiraStatusTransition(
    string TicketKey, string? Summary, string FromStatus, string ToStatus,
    int MinutesActive, DateTime AtUtc, string? IssueId = null) : DomainEvent(AtUtc);
public sealed record WorklogSubmitted(string TicketKey, string WorklogId, int Minutes, DateTime AtUtc) : DomainEvent(AtUtc);
public sealed record AuthenticationRequired(string Reason, DateTime AtUtc) : DomainEvent(AtUtc);
