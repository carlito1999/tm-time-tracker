using Dapper;
using FluentAssertions;
using TmTimeTracker.Data;
using Xunit;

namespace TmTimeTracker.Tests.Data;

public class DatabaseInitializerTests
{
    [Fact]
    public void Creates_all_expected_tables()
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();

        using var conn = ds.Open();
        var tables = conn.Query<string>(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name").ToList();

        tables.Should().Contain(new[]
        {
            "config", "minute_sample", "oauth_state",
            "remember_entry", "ticket_time"
        });
    }

    // CREATE TABLE IF NOT EXISTS cannot widen a table that already exists, so a database created
    // before a column was introduced keeps the old shape forever without this.
    [Fact]
    public void Adds_a_column_that_is_missing_from_an_older_database()
    {
        var ds = SharedSqlite.NewInMemory();
        using (var conn = ds.Open())
        {
            conn.Execute(@"CREATE TABLE pr_announcement (
                               ticket_key   TEXT PRIMARY KEY,
                               issue_id     TEXT,
                               summary      TEXT,
                               from_status  TEXT NOT NULL,
                               to_status    TEXT NOT NULL,
                               minutes      INTEGER NOT NULL DEFAULT 0,
                               occurred_at  TEXT NOT NULL,
                               queued_at    TEXT NOT NULL,
                               attempts     INTEGER NOT NULL DEFAULT 0,
                               announced_at TEXT,
                               pr_url       TEXT,
                               warned_at    TEXT)");
        }

        new DatabaseInitializer(ds).EnsureCreated();

        using var check = ds.Open();
        check.Query<string>("SELECT name FROM pragma_table_info('pr_announcement')")
             .Should().Contain("handed_off_at");
    }

    [Fact]
    public void Is_idempotent()
    {
        var ds = SharedSqlite.NewInMemory();
        var init = new DatabaseInitializer(ds);
        init.EnsureCreated();
        Action again = () => init.EnsureCreated();
        again.Should().NotThrow();
    }
}
