using FluentAssertions;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using TmTimeTracker.Services;
using TmTimeTracker.Tests.Data;
using Xunit;

namespace TmTimeTracker.Tests.Services;

public class WeeklyReportExporterTests : IDisposable
{
    private const string Sheep = @"C:\projects\sheeponline-new";

    // 2026-09-21 is a Monday; 2026-09-23 the Wednesday of that week.
    private static readonly DateTime Monday = new(2026, 9, 21);
    private static readonly DateTime Wednesday = new(2026, 9, 23);

    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"tmtt-export-{Guid.NewGuid():N}");

    private sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 9, 23, 14, 0, 0, DateTimeKind.Utc);
        public DateTimeOffset LocalNow => new(UtcNow, TimeSpan.Zero);
    }

    private static (WeeklyReportExporter exporter, HourActivityRepository hours,
        TicketSummaryRepository summaries) Build()
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();
        var hours = new HourActivityRepository(ds);
        var summaries = new TicketSummaryRepository(ds);
        return (new WeeklyReportExporter(hours, summaries, new FakeClock()), hours, summaries);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Builds_a_grid_of_blank_hours_when_nothing_was_tracked()
    {
        var (exporter, _, _) = Build();

        var grid = exporter.BuildGrid(Monday, Monday, 8, 10);

        grid.Should().HaveCount(2);
        grid.Should().OnlyContain(r => r.Repo == "" && r.Tickets == "");
    }

    // The whole chain: ledger row plus cached summary becomes one rendered cell.
    [Fact]
    public void Renders_a_tracked_hour_with_its_repo_and_ticket_name()
    {
        var (exporter, hours, summaries) = Build();
        for (var i = 0; i < 40; i++) hours.CreditMinute(Monday.AddHours(8), Sheep, "SN-291-350");
        summaries.Upsert("SN-291-350", "isolated-member studbooks");

        var grid = exporter.BuildGrid(Monday, Monday, 8, 9);

        var row = grid.Should().ContainSingle().Subject;
        row.Date.Should().Be("21-09-26");
        row.Repo.Should().Be("sheeponline-new");
        row.Tickets.Should().Be("SN-291-350: isolated-member studbooks");
    }

    // Hours outside the chosen days must not leak in, or a Monday-to-Wednesday report would
    // quietly include last Friday.
    [Fact]
    public void Leaves_out_hours_outside_the_chosen_days()
    {
        var (exporter, hours, _) = Build();
        for (var i = 0; i < 30; i++) hours.CreditMinute(Monday.AddDays(-1).AddHours(8), Sheep, "SN-OLD");
        for (var i = 0; i < 30; i++) hours.CreditMinute(Monday.AddHours(8), Sheep, "SN-NOW");

        var grid = exporter.BuildGrid(Monday, Monday, 8, 9);

        grid.Should().ContainSingle().Which.Tickets.Should().Be("SN-NOW");
    }

    // The last day of the range is inclusive - the user picks dates, not a half-open interval.
    [Fact]
    public void Includes_the_last_day_of_the_range()
    {
        var (exporter, hours, _) = Build();
        for (var i = 0; i < 30; i++) hours.CreditMinute(Wednesday.AddHours(8), Sheep, "SN-WED");

        var grid = exporter.BuildGrid(Monday, Wednesday, 8, 9);

        grid.Last().Tickets.Should().Be("SN-WED");
    }

    // Friday afternoon: the dialog should already be showing this week without any fiddling.
    [Fact]
    public void Defaults_the_range_to_monday_through_today()
    {
        var (exporter, _, _) = Build();

        var (from, to) = exporter.CurrentWeek();

        from.Should().Be(Monday);
        to.Should().Be(Wednesday);
    }

    /// <summary>
    /// The week boundary is where local-date arithmetic hides its bugs, and Sunday is the case a
    /// naive "DayOfWeek - 1" gets wrong: DayOfWeek counts from Sunday, so Sunday would go forward
    /// a day instead of back six.
    /// </summary>
    [Theory]
    [InlineData(2026, 9, 21, 2026, 9, 21)]   // Monday itself
    [InlineData(2026, 9, 23, 2026, 9, 21)]   // Wednesday
    [InlineData(2026, 9, 25, 2026, 9, 21)]   // Friday
    [InlineData(2026, 9, 27, 2026, 9, 21)]   // Sunday - still the same week
    [InlineData(2026, 9, 28, 2026, 9, 28)]   // the next Monday starts a new week
    public void Starts_the_week_on_monday_whatever_day_it_is(
        int year, int month, int day, int expectedYear, int expectedMonth, int expectedDay)
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();
        var clock = new FakeClock { UtcNow = new DateTime(year, month, day, 14, 0, 0, DateTimeKind.Utc) };
        var exporter = new WeeklyReportExporter(
            new HourActivityRepository(ds), new TicketSummaryRepository(ds), clock);

        var (from, to) = exporter.CurrentWeek();

        from.Should().Be(new DateTime(expectedYear, expectedMonth, expectedDay));
        to.Should().Be(new DateTime(year, month, day));
    }

    [Fact]
    public void Suggests_a_filename_carrying_the_week_start()
    {
        WeeklyReportExporter.SuggestFileName(Monday).Should().Be("TmTimeTracker-week-2026-09-21.xlsx");
    }

    [Fact]
    public void Writes_a_workbook_that_reads_back()
    {
        var (exporter, hours, summaries) = Build();
        for (var i = 0; i < 40; i++) hours.CreditMinute(Monday.AddHours(8), Sheep, "SN-291-350");
        summaries.Upsert("SN-291-350", "isolated-member studbooks");

        var path = exporter.Export(Monday, Monday, 8, 9, _tempDir, "report.xlsx");

        File.Exists(path).Should().BeTrue();
        var text = XlsxText.Extract(File.ReadAllBytes(path));
        text.Should().Contain("sheeponline-new").And.Contain("isolated-member studbooks");
    }

    // The reports folder will not exist the first time anyone presses the button.
    [Fact]
    public void Creates_the_folder_when_it_is_missing()
    {
        var (exporter, _, _) = Build();

        exporter.Export(Monday, Monday, 8, 9, _tempDir, "report.xlsx");

        Directory.Exists(_tempDir).Should().BeTrue();
    }

    // The filename box is free text and the extension is easy to leave off.
    [Fact]
    public void Adds_the_extension_when_the_filename_has_none()
    {
        var (exporter, _, _) = Build();

        var path = exporter.Export(Monday, Monday, 8, 9, _tempDir, "my week");

        Path.GetFileName(path).Should().Be("my week.xlsx");
    }

    [Fact]
    public void Falls_back_to_a_suggested_name_when_the_filename_is_blank()
    {
        var (exporter, _, _) = Build();

        var path = exporter.Export(Monday, Monday, 8, 9, _tempDir, "   ");

        Path.GetFileName(path).Should().Be("TmTimeTracker-week-2026-09-21.xlsx");
    }
}
