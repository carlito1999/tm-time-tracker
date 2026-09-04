using System.Diagnostics;
using FluentAssertions;
using TmTimeTracker.Platform;
using Xunit;

namespace TmTimeTracker.Tests.Platform;

/// <summary>
/// Exercised against real clones rather than a mocked process: the whole risk in this class is
/// whether the git invocation is right, which a fake would assume rather than prove.
/// </summary>
public class GitRemoteProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "tmtt-remote-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string NewRepo(string? remote)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        Git(path, "init");
        if (remote is not null) Git(path, $"remote add origin {remote}");
        return path;
    }

    private static void Git(string path, string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        p.WaitForExit(10_000);
    }

    [Fact]
    public void Reads_the_origin_remote()
    {
        var path = NewRepo("git@bitbucket.org:thecubeee/sheeponline-new.git");

        new GitRemoteProbe().GetRemoteUrl(path)
            .Should().Be("git@bitbucket.org:thecubeee/sheeponline-new.git");
    }

    [Fact]
    public void Returns_null_when_the_repo_has_no_origin()
    {
        new GitRemoteProbe().GetRemoteUrl(NewRepo(remote: null)).Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_a_path_that_is_not_a_repo()
    {
        var path = Path.Combine(_root, "plain");
        Directory.CreateDirectory(path);

        new GitRemoteProbe().GetRemoteUrl(path).Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_a_path_that_does_not_exist()
    {
        new GitRemoteProbe().GetRemoteUrl(Path.Combine(_root, "missing")).Should().BeNull();
    }
}
