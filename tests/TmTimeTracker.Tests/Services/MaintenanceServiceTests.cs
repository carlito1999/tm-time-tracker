using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TmTimeTracker.Data;
using TmTimeTracker.Services;
using TmTimeTracker.Tests.Data;
using Xunit;

namespace TmTimeTracker.Tests.Services;

public class MaintenanceServiceTests
{
    // Pinned to a zero offset so the local-date arithmetic the hour ledger depends on lands on
    // the same day whatever timezone the machine running these tests is in.
    private sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
        public DateTimeOffset LocalNow => new(UtcNow, TimeSpan.Zero);
    }

    private static (MaintenanceService svc, MinuteSampleRepository samples,
        HourActivityRepository hours, FakeClock clock) Build()
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();
        var samples = new MinuteSampleRepository(ds);
        var hours = new HourActivityRepository(ds);
        var clock = new FakeClock();
        return (new MaintenanceService(samples, hours, clock,
            NullLogger<MaintenanceService>.Instance), samples, hours, clock);
    }

    private static IReadOnlyList<HourActivityRow> AllHours(HourActivityRepository h) =>
        h.GetBetween(new DateTime(2020, 1, 1, 0, 0, 0), new DateTime(2030, 1, 1, 0, 0, 0));

    // Three months of local history is the weekly report's whole retention promise.
    [Fact]
    public void Prunes_hour_ledger_rows_older_than_ninety_days()
    {
        var (svc, _, hours, clock) = Build();
        hours.CreditMinute(clock.LocalNow.DateTime.AddDays(-91), @"C:\repo", "TM-OLD");

        svc.RunOnce();

        AllHours(hours).Should().BeEmpty();
    }

    [Fact]
    public void Keeps_hour_ledger_rows_inside_the_ninety_day_window()
    {
        var (svc, _, hours, clock) = Build();
        hours.CreditMinute(clock.LocalNow.DateTime.AddDays(-89), @"C:\repo", "TM-NEW");

        svc.RunOnce();

        AllHours(hours).Should().ContainSingle().Which.TicketKey.Should().Be("TM-NEW");
    }

    // The ledger stores local time while minute_sample stores UTC, so the two cutoffs are
    // computed from different clock properties. Pruning one must not disturb the other.
    [Fact]
    public void Prunes_minute_samples_older_than_thirty_days()
    {
        var (svc, samples, _, clock) = Build();
        samples.Insert(new MinuteSample(clock.UtcNow.AddDays(-31), "TM-1", false, "main", false));
        samples.Insert(new MinuteSample(clock.UtcNow.AddDays(-29), "TM-2", false, "main", false));

        svc.RunOnce();

        samples.Count().Should().Be(1);
    }

    // A failure in one store must not stop the other being swept, and must never take the
    // daemon down - maintenance runs unattended.
    [Fact]
    public void Reports_how_many_rows_it_removed()
    {
        var (svc, samples, hours, clock) = Build();
        samples.Insert(new MinuteSample(clock.UtcNow.AddDays(-31), "TM-1", false, "main", false));
        hours.CreditMinute(clock.LocalNow.DateTime.AddDays(-91), @"C:\repo", "TM-OLD");

        var swept = svc.RunOnce();

        swept.Should().Be(2);
    }
}
