using TmTimeTracker.Data;

namespace TmTimeTracker.Tests.Data;

internal static class SharedSqlite
{
    public static ISqliteConnectionFactory NewInMemory()
    {
        var name = $"db-{Guid.NewGuid():N}";
        return new SqliteConnectionFactory(
            $"Data Source=file:{name}?mode=memory&cache=shared");
    }
}
