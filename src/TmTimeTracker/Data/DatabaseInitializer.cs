using Dapper;

namespace TmTimeTracker.Data;

public sealed class DatabaseInitializer
{
    private readonly ISqliteConnectionFactory _factory;

    public DatabaseInitializer(ISqliteConnectionFactory factory) => _factory = factory;

    public void EnsureCreated()
    {
        using var conn = _factory.Open();
        var sql = LoadEmbeddedSql();
        conn.Execute(sql);
    }

    private static string LoadEmbeddedSql()
    {
        var asm = typeof(DatabaseInitializer).Assembly;
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("Schema.sql", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
