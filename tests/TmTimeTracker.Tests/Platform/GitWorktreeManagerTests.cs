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
        File.WriteAllText(Path.Combine(_origin, "README.md"), "on master");
        Git(_origin, "add -A");
        Git(_origin, "commit -m first");

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

    private static string Git(string cwd, string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
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
        var path = await New().PrepareAsync(_clone, CancellationToken.None);

        Directory.Exists(path).Should().BeTrue();
        File.Exists(Path.Combine(path, "README.md")).Should().BeTrue();
    }

    [Fact]
    public async Task Places_the_worktree_outside_the_tracked_repository()
    {
        var path = await New().PrepareAsync(_clone, CancellationToken.None);

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

        var path = await New().PrepareAsync(_clone, CancellationToken.None);

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

        var path = await New().PrepareAsync(_clone, CancellationToken.None);

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

        await New().PrepareAsync(_clone, CancellationToken.None);

        File.GetLastWriteTimeUtc(head).Should().Be(before);
    }

    // The sweep runs every five minutes; a leftover worktree from the previous run must not
    // make the next one fail.
    [Fact]
    public async Task Preparing_twice_succeeds()
    {
        var manager = New();
        var first = await manager.PrepareAsync(_clone, CancellationToken.None);

        var second = await manager.PrepareAsync(_clone, CancellationToken.None);

        second.Should().Be(first);
        File.Exists(Path.Combine(second, "README.md")).Should().BeTrue();
    }

    [Fact]
    public async Task Removes_the_worktree()
    {
        var manager = New();
        var path = await manager.PrepareAsync(_clone, CancellationToken.None);

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

        var prepare = async () => await New().PrepareAsync(noRemote, CancellationToken.None);

        await prepare.Should().ThrowAsync<GitWorktreeException>();
    }
}
