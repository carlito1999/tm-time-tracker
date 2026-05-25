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
