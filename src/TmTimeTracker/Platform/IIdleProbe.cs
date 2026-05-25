namespace TmTimeTracker.Platform;

public interface IIdleProbe
{
    long SecondsSinceLastInput();
    bool IsSessionLocked();
}
