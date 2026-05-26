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

    public void Observe(long idleSeconds, bool isLocked, bool claudeActive = false)
    {
        var next = isLocked
            ? UserActivityState.Idle
            : (claudeActive || idleSeconds < _threshold.TotalSeconds)
                ? UserActivityState.Active
                : UserActivityState.Idle;

        if (next == Current) return;
        Current = next;
        OnTransition?.Invoke(next);
    }
}
