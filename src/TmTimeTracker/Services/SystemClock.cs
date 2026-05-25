namespace TmTimeTracker.Services;

public interface IClock
{
    DateTime UtcNow { get; }
    DateTimeOffset LocalNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
    public DateTimeOffset LocalNow => DateTimeOffset.Now;
}
