using FluentAssertions;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class WeekGridTests
{
    private const string Sheep = @"C:\projects\sheeponline-new";
    private const string Training = @"C:\projects\training-manager";

    private static readonly DateTime Mon = new(2026, 8, 31);
    private static readonly DateTime Tue = new(2026, 9, 1);

    private static HourActivityRow At(DateTime day, int hour, string repo, string ticket, int minutes) =>
        new(day.AddHours(hour), repo, ticket, minutes);

    private static IReadOnlyList<GridRow> Build(
        IEnumerable<HourActivityRow> rows, DateTime from, DateTime to,
        int startHour = 8, int endHour = 10,
        IReadOnlyDictionary<string, string>? summaries = null,
        int minimumMinutes = WeekGrid.DefaultMinimumMinutes) =>
        WeekGrid.Build(rows.ToList(), from, to, startHour, endHour,
            summaries ?? new Dictionary<string, string>(), minimumMinutes);

    [Fact]
    public void Lays_down_one_row_per_hour_of_the_window()
    {
        Build(Array.Empty<HourActivityRow>(), Mon, Mon, startHour: 8, endHour: 12)
            .Should().HaveCount(4);
    }

    // The user asked for this explicitly: an untracked hour keeps its row and loses its content,
    // so the grid holds its shape and a gap is visible rather than silently closed up.
    [Fact]
    public void Leaves_an_hour_with_no_tracked_time_empty()
    {
        var grid = Build(new[] { At(Mon, 8, Sheep, "SN-1", 60) }, Mon, Mon);

        grid[1].TimeSlot.Should().Be("09:00\u201310:00");
        grid[1].Repo.Should().BeEmpty();
        grid[1].Tickets.Should().BeEmpty();
    }

    [Fact]
    public void Formats_the_date_and_time_slot_as_the_sheet_expects()
    {
        var grid = Build(Array.Empty<HourActivityRow>(), Mon, Mon, startHour: 8, endHour: 9);

        grid.Single().Date.Should().Be("31-08-26");
        grid.Single().TimeSlot.Should().Be("08:00\u201309:00");
    }

    // tracked_repo stores a full path and has no display-name column, so the sheet shows the
    // folder the way every other surface in the app does.
    [Fact]
    public void Names_a_repo_by_its_folder()
    {
        var grid = Build(new[] { At(Mon, 8, Sheep, "SN-1", 60) }, Mon, Mon);

        grid[0].Repo.Should().Be("sheeponline-new");
    }

    // A Claude session in one repo while you hand-edit another: the hour belongs to both.
    [Fact]
    public void Joins_two_repos_sharing_an_hour()
    {
        var grid = Build(new[]
        {
            At(Mon, 8, Sheep, "SN-1", 40),
            At(Mon, 8, Training, "TM-1", 20)
        }, Mon, Mon);

        grid[0].Repo.Should().Be("sheeponline-new / training-manager");
    }

    [Fact]
    public void Renders_a_ticket_as_key_and_summary()
    {
        var grid = Build(new[] { At(Mon, 8, Sheep, "SN-1", 60) }, Mon, Mon,
            summaries: new Dictionary<string, string> { ["SN-1"] = "isolated-member studbooks" });

        grid[0].Tickets.Should().Be("SN-1: isolated-member studbooks");
    }

    // Tickets that closed before the summary cache existed have no row, and a bare key is more
    // useful than an empty cell.
    [Fact]
    public void Falls_back_to_the_bare_key_when_no_summary_is_cached()
    {
        var grid = Build(new[] { At(Mon, 8, Sheep, "SN-1", 60) }, Mon, Mon);

        grid[0].Tickets.Should().Be("SN-1");
    }

    [Fact]
    public void Stacks_two_tickets_of_one_hour_in_a_single_cell()
    {
        var grid = Build(new[]
        {
            At(Mon, 8, Sheep, "SN-1", 40),
            At(Mon, 8, Sheep, "SN-2", 20)
        }, Mon, Mon);

        grid[0].Tickets.Should().Be("SN-1\nSN-2");
    }

    // Whatever you spent most of the hour on should read first.
    [Fact]
    public void Orders_repos_and_tickets_by_minutes_spent()
    {
        var grid = Build(new[]
        {
            At(Mon, 8, Sheep, "SN-1", 10),
            At(Mon, 8, Training, "TM-1", 45)
        }, Mon, Mon);

        grid[0].Repo.Should().Be("training-manager / sheeponline-new");
        grid[0].Tickets.Should().Be("TM-1\nSN-1");
    }

    // TimeAggregator credits every active repo concurrently, so a one-minute Claude blip would
    // otherwise smuggle a whole extra repo into the cell.
    [Fact]
    public void Drops_a_repo_that_earned_less_than_the_floor()
    {
        var grid = Build(new[]
        {
            At(Mon, 8, Sheep, "SN-1", 55),
            At(Mon, 8, Training, "TM-1", 2)
        }, Mon, Mon, minimumMinutes: 5);

        grid[0].Repo.Should().Be("sheeponline-new");
        grid[0].Tickets.Should().Be("SN-1");
    }

    /// <summary>
    /// The floor is asymmetric on purpose. An hour split four minutes each across two tickets in
    /// one repo is eight tracked minutes in that repo, so the repo earns its cell even though
    /// neither ticket does. Thresholding both on the ticket would blank the row and look like a bug.
    /// </summary>
    [Fact]
    public void Keeps_a_repo_whose_tickets_are_each_below_the_floor()
    {
        var grid = Build(new[]
        {
            At(Mon, 8, Sheep, "SN-1", 4),
            At(Mon, 8, Sheep, "SN-2", 4)
        }, Mon, Mon, minimumMinutes: 5);

        grid[0].Repo.Should().Be("sheeponline-new");
        grid[0].Tickets.Should().BeEmpty();
    }

    // Minutes on a branch with no ticket are stored under an empty key. They count towards the
    // repo, but there is nothing to print in the ticket column.
    [Fact]
    public void Counts_ticketless_minutes_towards_the_repo_but_prints_no_ticket()
    {
        var grid = Build(new[] { At(Mon, 8, Sheep, "", 30) }, Mon, Mon);

        grid[0].Repo.Should().Be("sheeponline-new");
        grid[0].Tickets.Should().BeEmpty();
    }

    [Fact]
    public void Separates_days_with_a_blank_row()
    {
        var grid = Build(Array.Empty<HourActivityRow>(), Mon, Tue, startHour: 8, endHour: 9);

        grid.Should().HaveCount(3);
        grid[0].Date.Should().Be("31-08-26");
        grid[1].Date.Should().BeEmpty();
        grid[1].TimeSlot.Should().BeEmpty();
        grid[2].Date.Should().Be("01-09-26");
    }

    [Fact]
    public void Does_not_trail_a_blank_row_after_the_last_day()
    {
        var grid = Build(Array.Empty<HourActivityRow>(), Mon, Mon, startHour: 8, endHour: 9);

        grid.Should().ContainSingle();
    }

    // The dialog lets the user pick both ends, and nothing stops them crossing over.
    [Fact]
    public void Returns_nothing_when_the_range_is_inverted()
    {
        Build(Array.Empty<HourActivityRow>(), Tue, Mon).Should().BeEmpty();
    }

    [Fact]
    public void Returns_nothing_when_the_day_window_is_inverted()
    {
        Build(Array.Empty<HourActivityRow>(), Mon, Mon, startHour: 17, endHour: 8)
            .Should().BeEmpty();
    }

    // A time picker hands back a whole DateTime; only its date should matter.
    [Fact]
    public void Ignores_the_time_of_day_on_the_range_bounds()
    {
        var grid = Build(Array.Empty<HourActivityRow>(),
            Mon.AddHours(15), Mon.AddHours(2), startHour: 8, endHour: 9);

        grid.Should().ContainSingle().Which.Date.Should().Be("31-08-26");
    }

    /// <summary>
    /// The floor is the user's to set, because the default hides real work on a young ledger:
    /// three tracked minutes against the five-minute default renders a completely blank row,
    /// which reads as "nothing was recorded" rather than "below your threshold".
    /// </summary>
    [Fact]
    public void Honours_a_floor_the_caller_lowered()
    {
        var rows = new[] { At(Mon, 8, Sheep, "SN-342", 3) };

        Build(rows, Mon, Mon, minimumMinutes: 5)[0].Repo.Should().BeEmpty();
        Build(rows, Mon, Mon, minimumMinutes: 1)[0].Repo.Should().Be("sheeponline-new");
        Build(rows, Mon, Mon, minimumMinutes: 1)[0].Tickets.Should().Be("SN-342");
    }

    // Zero means show everything, including a single stray minute.
    [Fact]
    public void Shows_every_minute_when_the_floor_is_zero()
    {
        var grid = Build(new[] { At(Mon, 8, Sheep, "SN-1", 1) }, Mon, Mon, minimumMinutes: 0);

        grid[0].Repo.Should().Be("sheeponline-new");
    }

    // Raising the floor still filters, and still filters the repo and ticket separately.
    [Fact]
    public void Honours_a_floor_the_caller_raised()
    {
        var grid = Build(new[]
        {
            At(Mon, 8, Sheep, "SN-1", 20),
            At(Mon, 8, Training, "TM-1", 15)
        }, Mon, Mon, minimumMinutes: 18);

        grid[0].Repo.Should().Be("sheeponline-new");
        grid[0].Tickets.Should().Be("SN-1");
    }

    // Nothing tracked is hidden unless the user asks for it: pruning a full sheet is easier than
    // noticing work missing from a sparse one.
    [Fact]
    public void Prints_everything_tracked_by_default()
    {
        WeekGrid.DefaultMinimumMinutes.Should().Be(0);

        Build(new[] { At(Mon, 8, Sheep, "SN-1", 1) }, Mon, Mon)[0]
            .Repo.Should().Be("sheeponline-new");
    }
}
