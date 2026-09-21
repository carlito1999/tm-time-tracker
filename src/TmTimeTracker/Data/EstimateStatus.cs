namespace TmTimeTracker.Data;

/// <summary>
/// Lifecycle of one ticket's estimate. Terminal states are the sweep's stop condition: without
/// them a finished ticket would be re-estimated every five minutes, and a permanently broken
/// one would spawn a Claude session forever.
/// </summary>
public static class EstimateStatus
{
    public const string Pending = "pending";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string SkippedExisting = "skipped_existing";

    public static bool IsTerminal(string status) =>
        status is Done or Failed or SkippedExisting;
}
