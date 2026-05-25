namespace TmTimeTracker.Platform;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    public bool IsPrimary { get; }

    public SingleInstanceGuard(string name = "Global\\TmTimeTracker.SingleInstance.v1")
    {
        _mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        IsPrimary = createdNew;
    }

    public void Dispose()
    {
        if (IsPrimary)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _mutex.Dispose();
    }
}
