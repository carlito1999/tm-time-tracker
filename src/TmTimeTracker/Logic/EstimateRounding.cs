namespace TmTimeTracker.Logic;

/// <summary>
/// Puts an estimate on the quarter-hour grid people actually read it against.
///
/// The three phases are summed as-is and would otherwise reach Jira raw: SN-305's 40+25+15
/// arrived as 80m and rendered "1h 20m", which reads as false precision for a figure the prompt
/// explicitly asks to be a median guess.
///
/// Ceiling rather than nearest, decided with the user: an estimate that shrinks on rounding is
/// worse than one that grows. Applied to the total rather than per phase - rounding each phase
/// first would add up to 42 minutes to a small ticket.
/// </summary>
public static class EstimateRounding
{
    public const int QuarterHourMinutes = 15;

    public static int CeilingToQuarterHour(int minutes)
    {
        // Rounding nothing up to a quarter hour would invent work that was never estimated. The
        // sanity gate makes this unreachable, but the guard costs nothing.
        if (minutes <= 0) return 0;

        return (minutes + QuarterHourMinutes - 1) / QuarterHourMinutes * QuarterHourMinutes;
    }
}
