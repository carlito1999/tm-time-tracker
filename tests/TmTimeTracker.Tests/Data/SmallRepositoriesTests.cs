using FluentAssertions;
using TmTimeTracker.Data;
using Xunit;

namespace TmTimeTracker.Tests.Data;

public class SmallRepositoriesTests
{
    private static ISqliteConnectionFactory Setup()
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();
        return ds;
    }

    [Fact]
    public void Config_round_trips_with_defaults_when_unset()
    {
        var repo = new ConfigRepository(Setup());
        repo.SetIfMissing(new AppConfig(
            IdleThresholdSeconds: 600,
            JiraPollIntervalSeconds: 90,
            RepoPath: @"c:\projects\training-manager",
            RememberPath: @"c:\projects\training-manager\.remember",
            InProgressStatusName: "In Progress",
            TransitionToStatusName: "Review"));

        var loaded = repo.Get();
        loaded.RepoPath.Should().Be(@"c:\projects\training-manager");
        loaded.IdleThresholdSeconds.Should().Be(600);
    }

    [Fact]
    public void Config_SetIfMissing_does_not_overwrite()
    {
        var repo = new ConfigRepository(Setup());
        repo.SetIfMissing(new AppConfig(600, 90, "a", "b", "ip", "rev"));
        repo.SetIfMissing(new AppConfig(999, 999, "x", "y", "z", "w"));
        repo.Get().RepoPath.Should().Be("a");
    }

    [Fact]
    public void OAuthState_round_trip()
    {
        var repo = new OAuthStateRepository(Setup());
        var dummyAccess = new byte[] { 1, 2, 3 };
        var dummyRefresh = new byte[] { 4, 5 };
        var expires = DateTime.UtcNow.AddHours(1);
        repo.Save(new OAuthState("cloud-xyz", dummyAccess, dummyRefresh, expires, "read:jira-work"));
        var loaded = repo.Load();
        loaded!.CloudId.Should().Be("cloud-xyz");
        loaded.AccessTokenDpapi.Should().BeEquivalentTo(dummyAccess);
        loaded.RefreshTokenDpapi.Should().BeEquivalentTo(dummyRefresh);
        loaded.AccessExpiresAt.Should().BeCloseTo(expires, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void OAuthState_load_returns_null_when_absent()
    {
        var repo = new OAuthStateRepository(Setup());
        repo.Load().Should().BeNull();
    }

    [Fact]
    public void MinuteSample_insert_and_prune()
    {
        var repo = new MinuteSampleRepository(Setup());
        var old = DateTime.UtcNow.AddDays(-40);
        var fresh = DateTime.UtcNow;
        repo.Insert(new MinuteSample(old,    "TM-29", IsIdle: false, "main", ClaudeRunning: true));
        repo.Insert(new MinuteSample(fresh,  "TM-29", IsIdle: false, "main", ClaudeRunning: true));

        var deleted = repo.PruneOlderThan(DateTime.UtcNow.AddDays(-30));
        deleted.Should().Be(1);
        repo.Count().Should().Be(1);
    }
}
