using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TmTimeTracker.Platform;
using Xunit;

namespace TmTimeTracker.Tests.Platform;

/// <summary>
/// Exercises real git. The worktree is what keeps the estimator from corrupting the app's own
/// time tracking, so these assertions are about actual git behaviour rather than a fake's.
/// </summary>
public class GitWorktreeManagerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "tmtt-wt-" + Guid.NewGuid().ToString("N"));

    private readonly string _origin;
    private readonly string _clone;
    private readonly string _estimates;

    public GitWorktreeManagerTests()
    {
        _origin = Path.Combine(_root, "origin");
        _clone = Path.Combine(_root, "training-manager");
        _estimates = Path.Combine(_root, "estimates");
        Directory.CreateDirectory(_origin);

        Git(_origin, "init --initial-branch=master");
        Git(_origin, "config user.email test@example.com");
        Git(_origin, "config user.name Test");

        // Enough files to read as a codebase rather than a stub branch: PrepareAsync refuses a
        // checkout with almost nothing in it, and a one-file fixture would be exactly that.
        File.WriteAllText(Path.Combine(_origin, "README.md"), "on master");
        Directory.CreateDirectory(Path.Combine(_origin, "src"));
        foreach (var name in new[] { "a.cs", "b.cs", "c.cs", "d.cs", "e.cs" })
            File.WriteAllText(Path.Combine(_origin, "src", name), $"// {name}");
        Git(_origin, "add -A");
        // Dated rather than "now" so tests about which branch is newest can place their own
        // branches on either side of it.
        Git(_origin, "commit -m first", committerDate: "2026-01-01T10:00:00");

        Git(_root, $"clone --quiet \"{_origin}\" \"{_clone}\"");
        Git(_clone, "config user.email test@example.com");
        Git(_clone, "config user.name Test");
    }

    public void Dispose()
    {
        try { ForceDelete(_root); } catch { /* best effort on Windows file locks */ }
    }

    private static void ForceDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    private static string Git(string cwd, string args, string? committerDate = null)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // for-each-ref sorts by committer date to a one-second resolution, so commits made in
        // the same second would tie and the "newest branch" assertion would be meaningless.
        if (committerDate is not null)
        {
            psi.Environment["GIT_COMMITTER_DATE"] = committerDate;
            psi.Environment["GIT_AUTHOR_DATE"] = committerDate;
        }
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {args} failed: {stderr}");
        return stdout.Trim();
    }

    private GitWorktreeManager New() =>
        new(_estimates, NullLogger<GitWorktreeManager>.Instance);

    [Fact]
    public async Task Creates_a_worktree_holding_the_repository_content()
    {
        var path = await New().PrepareAsync(_clone, null, CancellationToken.None);

        Directory.Exists(path).Should().BeTrue();
        File.Exists(Path.Combine(path, "README.md")).Should().BeTrue();
    }

    [Fact]
    public async Task Places_the_worktree_outside_the_tracked_repository()
    {
        var path = await New().PrepareAsync(_clone, null, CancellationToken.None);

        path.Should().StartWith(_estimates);
        path.Should().NotStartWith(_clone);
    }

    // The estimate must reflect latest main, not whatever branch the user left checked out.
    [Fact]
    public async Task Checks_out_the_remote_default_branch_not_the_local_one()
    {
        Git(_clone, "checkout -q -b feature/TM-1");
        File.WriteAllText(Path.Combine(_clone, "scratch.txt"), "local work");
        Git(_clone, "add -A");
        Git(_clone, "commit -m local-only");

        var path = await New().PrepareAsync(_clone, null, CancellationToken.None);

        File.Exists(Path.Combine(path, "scratch.txt")).Should().BeFalse();
        File.ReadAllText(Path.Combine(path, "README.md")).Should().Be("on master");
    }

    // Latest main means latest on the remote, so a commit pushed since the last fetch must
    // appear without anyone running fetch by hand.
    [Fact]
    public async Task Picks_up_commits_that_landed_on_the_remote_since_the_last_fetch()
    {
        File.WriteAllText(Path.Combine(_origin, "README.md"), "updated on master");
        Git(_origin, "add -A");
        Git(_origin, "commit -m second");

        var path = await New().PrepareAsync(_clone, null, CancellationToken.None);

        File.ReadAllText(Path.Combine(path, "README.md")).Should().Be("updated on master");
    }

    /// <summary>
    /// The invariant the whole feature rests on. ActiveRepoResolver.TryReadHeadMtime treats a
    /// fresh .git/HEAD mtime as the user working in that repo, so if preparing a worktree
    /// touched it the estimator would silently credit minutes to whatever ticket branch the
    /// repo was on. Pinned here so a future change to this class cannot quietly break it.
    /// </summary>
    [Fact]
    public async Task Does_not_disturb_the_head_file_the_activity_probe_watches()
    {
        var head = Path.Combine(_clone, ".git", "HEAD");
        var before = File.GetLastWriteTimeUtc(head);

        await New().PrepareAsync(_clone, null, CancellationToken.None);

        File.GetLastWriteTimeUtc(head).Should().Be(before);
    }

    // The sweep runs every five minutes; a leftover worktree from the previous run must not
    // make the next one fail.
    [Fact]
    public async Task Preparing_twice_succeeds()
    {
        var manager = New();
        var first = await manager.PrepareAsync(_clone, null, CancellationToken.None);

        var second = await manager.PrepareAsync(_clone, null, CancellationToken.None);

        second.Should().Be(first);
        File.Exists(Path.Combine(second, "README.md")).Should().BeTrue();
    }

    [Fact]
    public async Task Removes_the_worktree()
    {
        var manager = New();
        var path = await manager.PrepareAsync(_clone, null, CancellationToken.None);

        manager.Remove(_clone);

        Directory.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task Removing_a_worktree_that_was_never_created_is_harmless()
    {
        var remove = () => New().Remove(_clone);

        remove.Should().NotThrow();
        await Task.CompletedTask;
    }

    /// <summary>
    /// A live repository had origin/HEAD pointing at a stub branch holding a single .gitignore
    /// while 2,000 files sat on the active branches. Estimating against it produced a confident
    /// and completely meaningless number - nothing downstream can distinguish an empty checkout
    /// from an easy ticket, so it has to be refused here.
    /// </summary>
    [Fact]
    public async Task Refuses_a_default_branch_that_holds_no_code()
    {
        var stub = Path.Combine(_root, "stub-origin");
        var stubClone = Path.Combine(_root, "stub-clone");
        Directory.CreateDirectory(stub);
        Git(stub, "init --initial-branch=mainTraining");
        Git(stub, "config user.email test@example.com");
        Git(stub, "config user.name Test");
        File.WriteAllText(Path.Combine(stub, ".gitignore"), "vendor/");
        Git(stub, "add -A");
        Git(stub, "commit -m \"Initial commit\"");
        Git(_root, $"clone --quiet \"{stub}\" \"{stubClone}\"");

        var prepare = async () => await New().PrepareAsync(stubClone, null, CancellationToken.None);

        (await prepare.Should().ThrowAsync<GitWorktreeException>())
            .WithMessage("*no code to estimate*");
    }

    /// <summary>
    /// The live shape that broke this: origin/HEAD points at a stub "mainTraining" branch while
    /// real work lands on rolling dated dev branches. No configuration should be needed - the
    /// newest trunk-shaped branch wins, and the stub loses on date even though "main*" matches it.
    /// </summary>
    [Fact]
    public async Task Prefers_the_newest_trunk_branch_over_a_stale_default()
    {
        Git(_origin, "checkout -q -b mainTraining");
        Git(_origin, "rm -q -r --cached .");
        foreach (var f in Directory.GetFiles(Path.Combine(_origin, "src"))) File.Delete(f);
        File.Delete(Path.Combine(_origin, "README.md"));
        File.WriteAllText(Path.Combine(_origin, ".gitignore"), "vendor/");
        Git(_origin, "add -A");
        Git(_origin, "commit -m \"Initial commit\"", committerDate: "2025-10-15T10:00:00");

        Git(_origin, "checkout -q master");
        Git(_origin, "checkout -q -b dev-01-09-2026");
        File.WriteAllText(Path.Combine(_origin, "which.txt"), "live work");
        Git(_origin, "add -A");
        Git(_origin, "commit -m live", committerDate: "2026-09-01T10:00:00");

        Git(_clone, "fetch -q origin");
        Git(_clone, "remote set-head origin mainTraining");

        var path = await New().PrepareAsync(_clone, null, CancellationToken.None);

        File.Exists(Path.Combine(path, "which.txt")).Should().BeTrue();
        File.Exists(Path.Combine(path, "README.md")).Should().BeTrue();
    }

    /// <summary>
    /// A repo whose trunk is not its default branch. One tracked repo points origin/HEAD at a
    /// stub while real work lands on rolling dated branches, so the override names the branch.
    /// </summary>
    [Fact]
    public async Task Uses_an_exact_branch_override()
    {
        Git(_origin, "checkout -q -b release");
        File.WriteAllText(Path.Combine(_origin, "release-only.txt"), "shipped");
        Git(_origin, "add -A");
        Git(_origin, "commit -m release-work");
        Git(_clone, "fetch -q origin");

        var path = await New().PrepareAsync(_clone, "release", CancellationToken.None);

        File.Exists(Path.Combine(path, "release-only.txt")).Should().BeTrue();
    }

    /// <summary>
    /// The real motivating case: a repo cuts dev-01-09-2026, dev-31-08-2026 and so on, so no
    /// fixed name stays correct. The pattern resolves to whichever matching branch was committed
    /// to most recently, and keeps working when the next one is cut.
    /// </summary>
    [Fact]
    public async Task Resolves_a_wildcard_override_to_the_newest_matching_branch()
    {
        Git(_origin, "checkout -q -b dev-01-01-2026");
        File.WriteAllText(Path.Combine(_origin, "which.txt"), "older");
        Git(_origin, "add -A");
        Git(_origin, "commit -m older-dev", committerDate: "2026-01-01T10:00:00");

        Git(_origin, "checkout -q -b dev-02-09-2026");
        File.WriteAllText(Path.Combine(_origin, "which.txt"), "newer");
        Git(_origin, "add -A");
        Git(_origin, "commit -m newer-dev", committerDate: "2026-09-02T10:00:00");

        Git(_clone, "fetch -q origin");

        var path = await New().PrepareAsync(_clone, "dev-*", CancellationToken.None);

        File.ReadAllText(Path.Combine(path, "which.txt")).Should().Be("newer");
    }

    [Fact]
    public async Task Reports_an_override_that_matches_no_branch()
    {
        var prepare = async () =>
            await New().PrepareAsync(_clone, "nosuchbranch-*", CancellationToken.None);

        (await prepare.Should().ThrowAsync<GitWorktreeException>())
            .WithMessage("*nosuchbranch-*");
    }

    // A repo with no remote cannot be brought to latest main, and the sweep needs to hear about
    // it rather than silently estimate against stale local state.
    [Fact]
    public async Task Reports_a_repository_with_no_remote()
    {
        var noRemote = Path.Combine(_root, "no-remote");
        Directory.CreateDirectory(noRemote);
        Git(noRemote, "init --initial-branch=master");
        Git(noRemote, "config user.email test@example.com");
        Git(noRemote, "config user.name Test");
        File.WriteAllText(Path.Combine(noRemote, "a.txt"), "x");
        Git(noRemote, "add -A");
        Git(noRemote, "commit -m only");

        var prepare = async () => await New().PrepareAsync(noRemote, null, CancellationToken.None);

        await prepare.Should().ThrowAsync<GitWorktreeException>();
    }
}
