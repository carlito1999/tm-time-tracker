using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TmTimeTracker.Data;
using TmTimeTracker.Platform;
using TmTimeTracker.Services;
using TmTimeTracker.Tests.Data;
using Xunit;

namespace TmTimeTracker.Tests.Services;

public class ActiveRepoResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "tmtt-resolver-" + Guid.NewGuid().ToString("N"));

    public ActiveRepoResolverTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class FakeForeground : IForegroundWindowProbe
    {
        public ForegroundWindow? Value { get; set; }
        public ForegroundWindow? Probe() => Value;
    }

    private sealed class FakeBranchProbe : IGitBranchProbe
    {
        public Dictionary<string, string?> ByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? GetCurrentBranch(string repoPath) =>
            ByPath.TryGetValue(repoPath, out var b) ? b : null;
    }

    private sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc);
        public DateTimeOffset LocalNow => new(UtcNow, TimeSpan.Zero);
    }

    private string MakeRepo(string name, DateTime headMtimeUtc)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        var headFile = Path.Combine(path, ".git", "HEAD");
        File.WriteAllText(headFile, "ref: refs/heads/main\n");
        File.SetLastWriteTimeUtc(headFile, headMtimeUtc);
        return path;
    }

    private (ActiveRepoResolver r, TrackedRepoRepository repos, FakeForeground fg, FakeBranchProbe git, FakeClock clk)
        Build(IEnumerable<string> repoPaths)
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();
        var repos = new TrackedRepoRepository(ds);
        foreach (var p in repoPaths) repos.Add(p);

        var fg = new FakeForeground();
        var git = new FakeBranchProbe();
        var clk = new FakeClock();
        var r = new ActiveRepoResolver(repos, fg, git, clk, NullLogger<ActiveRepoResolver>.Instance);
        return (r, repos, fg, git, clk);
    }

    [Fact]
    public void Returns_null_resolution_when_no_repos_tracked()
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();
        var r = new ActiveRepoResolver(new TrackedRepoRepository(ds),
            new FakeForeground(), new FakeBranchProbe(), new FakeClock(),
            NullLogger<ActiveRepoResolver>.Instance);
        r.Resolve().Should().BeNull();
    }

    [Fact]
    public void Window_title_match_wins_over_polling()
    {
        var clock = new FakeClock();
        var fresh = clock.UtcNow.AddMinutes(-1);
        var stale = clock.UtcNow.AddHours(-1);

        var a = MakeRepo("project-a", fresh);
        var b = MakeRepo("project-b", stale);

        var (r, _, fg, git, _) = Build(new[] { a, b });
        fg.Value = new ForegroundWindow("Code", "Program.cs - project-b - Visual Studio Code");
        git.ByPath[b] = "TM-29-foo";

        var res = r.Resolve();
        res.Should().NotBeNull();
        res!.RepoPath.Should().Be(b);
        res.TicketKey.Should().Be("TM-29");
    }

    [Fact]
    public void Polling_used_when_no_IDE_match()
    {
        var clock = new FakeClock();
        var a = MakeRepo("a", clock.UtcNow.AddMinutes(-2));
        var b = MakeRepo("b", clock.UtcNow.AddMinutes(-1));

        var (r, _, fg, git, _) = Build(new[] { a, b });
        fg.Value = new ForegroundWindow("chrome", "Some browser tab - Google Chrome");
        git.ByPath[b] = "feature/TM-30";

        var res = r.Resolve();
        res!.RepoPath.Should().Be(b);
        res.TicketKey.Should().Be("TM-30");
    }

    [Fact]
    public void Sticky_when_no_signal_returns_previous_resolution()
    {
        var clock = new FakeClock();
        var a = MakeRepo("a", clock.UtcNow.AddHours(-1));
        var (r, _, fg, git, _) = Build(new[] { a });
        fg.Value = new ForegroundWindow("Code", "Program.cs - a - Visual Studio Code");
        git.ByPath[a] = "TM-29";
        var first = r.Resolve();
        first!.RepoPath.Should().Be(a);

        fg.Value = null;
        var second = r.Resolve();
        second.Should().BeEquivalentTo(first);
    }

    [Fact]
    public void Window_title_match_by_basename_is_case_insensitive()
    {
        var clock = new FakeClock();
        var repo = MakeRepo("Project-X", clock.UtcNow);
        var (r, _, fg, git, _) = Build(new[] { repo });
        fg.Value = new ForegroundWindow("Code", "foo.cs - project-x - Visual Studio Code");
        git.ByPath[repo] = "main";

        r.Resolve()!.RepoPath.Should().Be(repo);
    }

    [Fact]
    public void Missing_repo_path_on_disk_does_not_throw()
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();
        var repos = new TrackedRepoRepository(ds);
        repos.Add(@"c:\nonexistent\ghost");

        var r = new ActiveRepoResolver(repos, new FakeForeground(), new FakeBranchProbe(),
            new FakeClock(), NullLogger<ActiveRepoResolver>.Instance);

        r.Resolve().Should().BeNull();
    }

    [Fact]
    public void Resolution_with_no_ticket_returns_null_ticket()
    {
        var clock = new FakeClock();
        var a = MakeRepo("a", clock.UtcNow);
        var (r, _, fg, git, _) = Build(new[] { a });
        fg.Value = new ForegroundWindow("Code", "foo.cs - a - Visual Studio Code");
        git.ByPath[a] = "main";

        var res = r.Resolve();
        res!.Branch.Should().Be("main");
        res.TicketKey.Should().BeNull();
    }
}
