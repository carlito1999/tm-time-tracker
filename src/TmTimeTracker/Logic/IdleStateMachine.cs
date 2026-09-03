namespace TmTimeTracker.Logic;

public sealed class IdleStateMachine
{
    private readonly TimeSpan _threshold;

    public IdleStateMachine(TimeSpan threshold)
    {
        if (threshold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        _threshold = threshold;
    }

    public UserActivityState Current { get; private set; } = UserActivityState.Active;

    public event Action<UserActivityState>? OnTransition;

    public void Observe(long idleSeconds, bool isLocked)
    {
        var next = isLocked || idleSeconds >= _threshold.TotalSeconds
            ? UserActivityState.Idle
            : UserActivityState.Active;

        if (next == Current) return;
        Current = next;
        OnTransition?.Invoke(next);
    }
}
