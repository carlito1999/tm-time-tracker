using Dapper;

namespace TmTimeTracker.Data;

public sealed class DatabaseInitializer
{
    /// <summary>
    /// Columns added to tables that already shipped. CREATE TABLE IF NOT EXISTS silently does
    /// nothing to an existing table, so a schema change that widens one is invisible to every
    /// database created before it - the app then fails on the first write to the new column.
    ///
    /// Adding a column here is enough; SQLite's ADD COLUMN is cheap and this runs on every start.
    /// A new table needs no entry, only a CREATE TABLE IF NOT EXISTS in Schema.sql.
    /// </summary>
    private static readonly (string Table, string Column, string Definition)[] AddedColumns =
    {
        ("pr_announcement", "handed_off_at", "TEXT"),
    };

    private readonly ISqliteConnectionFactory _factory;

    public DatabaseInitializer(ISqliteConnectionFactory factory) => _factory = factory;

    public void EnsureCreated()
    {
        using var conn = _factory.Open();
        conn.Execute(LoadEmbeddedSql());

        foreach (var (table, column, definition) in AddedColumns)
        {
            var present = conn.Query<string>(
                "SELECT name FROM pragma_table_info(@table)", new { table });
            if (present.Contains(column, StringComparer.Ordinal)) continue;

            // Interpolated, which is safe only because every value is a literal in this file.
            conn.Execute($"ALTER TABLE {table} ADD COLUMN {column} {definition}");
        }
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
