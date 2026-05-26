using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TmTimeTracker.Platform;
using Xunit;

namespace TmTimeTracker.Tests.Platform;

public class FileClaudeCodeActivityProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "tmtt-claude-probe-" + Guid.NewGuid().ToString("N"));

    public FileClaudeCodeActivityProbeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private FileClaudeCodeActivityProbe New() =>
        new(_root, NullLogger<FileClaudeCodeActivityProbe>.Instance);

    private void WriteSession(string projectSlug, string sessionId, DateTime mtimeUtc)
    {
        var dir = Path.Combine(_root, projectSlug);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{sessionId}.jsonl");
        File.WriteAllText(file, "{}\n");
        File.SetLastWriteTimeUtc(file, mtimeUtc);
    }

    [Fact]
    public void Returns_empty_when_projects_root_does_not_exist()
    {
        var probe = new FileClaudeCodeActivityProbe(
            Path.Combine(_root, "nonexistent"),
            NullLogger<FileClaudeCodeActivityProbe>.Instance);
        probe.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void Returns_empty_when_no_projects()
    {
        New().Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void Returns_latest_mtime_for_each_project()
    {
        var t1 = new DateTime(2026, 5, 25, 10, 0, 0, DateTimeKind.Utc);
        var t2 = t1.AddMinutes(30);
        WriteSession("c--projects-a", "sess-1", t1);
        WriteSession("c--projects-a", "sess-2", t2);
        WriteSession("c--projects-b", "sess-1", t1);

        var snap = New().Snapshot();
        snap.Should().HaveCount(2);
        snap["c--projects-a"].Should().BeCloseTo(t2, TimeSpan.FromSeconds(1));
        snap["c--projects-b"].Should().BeCloseTo(t1, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Lookup_is_case_insensitive()
    {
        var t = new DateTime(2026, 5, 25, 10, 0, 0, DateTimeKind.Utc);
        WriteSession("C--Projects-X", "s", t);

        var snap = New().Snapshot();
        snap.ContainsKey("c--projects-x").Should().BeTrue();
        snap.ContainsKey("C--PROJECTS-X").Should().BeTrue();
    }

    [Fact]
    public void Project_folder_without_jsonl_is_skipped()
    {
        Directory.CreateDirectory(Path.Combine(_root, "c--projects-empty"));
        New().Snapshot().Should().BeEmpty();
    }
}
