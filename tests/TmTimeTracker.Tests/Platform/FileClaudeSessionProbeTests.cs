using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TmTimeTracker.Platform;
using Xunit;

namespace TmTimeTracker.Tests.Platform;

public class FileClaudeSessionProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "tmtt-sessions-" + Guid.NewGuid().ToString("N"));

    public FileClaudeSessionProbeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Serialised rather than hand-written so the fixtures carry real Windows path escaping, and
    /// with the surrounding noise Claude Code actually writes - the probe has to ignore all of it.
    /// </summary>
    private void WriteSession(int pid, string? cwd, string? status, long? procStart)
    {
        var doc = new Dictionary<string, object?>
        {
            ["pid"] = pid,
            ["sessionId"] = Guid.NewGuid().ToString(),
            ["cwd"] = cwd,
            ["startedAt"] = 1788431679473,
            ["version"] = "2.1.259",
            ["kind"] = "interactive",
            ["entrypoint"] = "cli",
            ["messagingSocketPath"] = @"\.\pipe\LOCAL\cc-msg-e33ecde8",
        };
        if (status is not null) doc["status"] = status;
        if (procStart is not null) doc["procStart"] = procStart.Value.ToString();
        File.WriteAllText(Path.Combine(_root, pid + ".json"), JsonSerializer.Serialize(doc));
    }

    private IReadOnlyList<TmTimeTracker.Logic.ClaudeSession> Read() =>
        new FileClaudeSessionProbe(_root, NullLogger<FileClaudeSessionProbe>.Instance).Snapshot();

    [Fact]
    public void Reads_the_fields_that_decide_activity()
    {
        WriteSession(13328, @"C:\projects\sheeponline-new", "busy", 134329052784643818);

        var s = Read().Should().ContainSingle().Subject;
        s.Pid.Should().Be(13328);
        s.Cwd.Should().Be(@"C:\projects\sheeponline-new");
        s.Status.Should().Be("busy");
        s.ProcStartFileTime.Should().Be(134329052784643818);
    }

    [Fact]
    public void A_missing_status_field_reads_as_null_rather_than_busy()
    {
        // Older builds omit it entirely. Guessing here would bill repos that nothing is running in.
        WriteSession(12360, @"C:\projects\dynatag", status: null, procStart: 134328871614934010);

        Read().Should().ContainSingle().Which.Status.Should().BeNull();
    }

    [Fact]
    public void A_missing_procStart_reads_as_null()
    {
        WriteSession(999, @"C:\projects\dynatag", "busy", procStart: null);

        Read().Should().ContainSingle().Which.ProcStartFileTime.Should().BeNull();
    }

    [Fact]
    public void One_unreadable_file_does_not_lose_the_others()
    {
        WriteSession(1, @"C:\projects\a", "busy", 1);
        File.WriteAllText(Path.Combine(_root, "2.json"), "{ this is not json");
        File.WriteAllText(Path.Combine(_root, "3.json"), "");
        WriteSession(4, @"C:\projects\b", "idle", 4);

        Read().Select(s => s.Pid).Should().BeEquivalentTo(new[] { 1, 4 });
    }

    [Fact]
    public void Non_json_files_are_skipped()
    {
        WriteSession(1, @"C:\projects\a", "busy", 1);
        File.WriteAllText(Path.Combine(_root, "1.abc.key"), "not a session");

        Read().Should().ContainSingle();
    }

    [Fact]
    public void A_missing_sessions_directory_is_not_an_error()
    {
        new FileClaudeSessionProbe(Path.Combine(_root, "nope"),
                NullLogger<FileClaudeSessionProbe>.Instance)
            .Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void A_session_with_no_pid_is_skipped()
    {
        File.WriteAllText(Path.Combine(_root, "x.json"), """{"cwd":"C:/projects/a","status":"busy"}""");

        Read().Should().BeEmpty();
    }
}
