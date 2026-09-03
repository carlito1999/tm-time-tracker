using System.Diagnostics;
using FluentAssertions;
using TmTimeTracker.Logic;
using TmTimeTracker.Platform;
using Xunit;

namespace TmTimeTracker.Tests.Platform;

/// <summary>
/// Runs against this test process, which is a real live process with a real creation time - the
/// only honest way to check that a FILETIME out of a session file lines up with what Windows
/// reports.
/// </summary>
public class Win32ProcessLivenessTests
{
    private static readonly Win32ProcessLiveness Liveness = new();

    private static (int pid, long procStart) Self()
    {
        using var self = Process.GetCurrentProcess();
        return (self.Id, self.StartTime.ToUniversalTime().ToFileTimeUtc());
    }

    private static ClaudeSession Session(int pid, long? procStart) =>
        new(pid, @"C:\projects\whatever", "busy", procStart);

    [Fact]
    public void A_live_process_with_a_matching_start_time_is_running()
    {
        var (pid, procStart) = Self();

        Liveness.IsRunning(Session(pid, procStart)).Should().BeTrue();
    }

    [Fact]
    public void A_live_pid_with_the_wrong_start_time_is_not_running()
    {
        // The pid-reuse case: the session's process died and Windows handed the number to
        // something else. Without this, that repo would bill until the machine rebooted.
        var (pid, procStart) = Self();

        Liveness.IsRunning(Session(pid, procStart - TimeSpan.FromHours(1).Ticks))
            .Should().BeFalse();
    }

    [Fact]
    public void A_start_time_within_tolerance_still_counts()
    {
        var (pid, procStart) = Self();

        Liveness.IsRunning(Session(pid, procStart + TimeSpan.FromSeconds(1).Ticks))
            .Should().BeTrue();
    }

    [Fact]
    public void A_session_with_no_recorded_start_time_is_not_running()
    {
        var (pid, _) = Self();

        Liveness.IsRunning(Session(pid, procStart: null)).Should().BeFalse();
    }

    [Fact]
    public void A_nonsense_start_time_is_not_running()
    {
        var (pid, _) = Self();

        Liveness.IsRunning(Session(pid, long.MinValue)).Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_pid_that_cannot_exist_is_not_running(int pid)
    {
        Liveness.IsRunning(Session(pid, Self().procStart)).Should().BeFalse();
    }

    [Fact]
    public void A_process_that_has_exited_is_not_running()
    {
        using var doomed = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
            { CreateNoWindow = true, UseShellExecute = false })!;
        var procStart = doomed.StartTime.ToUniversalTime().ToFileTimeUtc();
        doomed.WaitForExit();

        Liveness.IsRunning(Session(doomed.Id, procStart)).Should().BeFalse();
    }
}
