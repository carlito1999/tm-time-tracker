using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class ClaudeCommandLineTests
{
    private static IReadOnlyList<string> Build(string? model = "opus", decimal budget = 2.00m) =>
        ClaudeCommandLine.Build("estimate this", model, budget);

    private static string Joined(string? model = "opus", decimal budget = 2.00m) =>
        string.Join(" ", Build(model, budget));

    [Fact]
    public void Runs_headless_with_the_prompt()
    {
        var args = Build();

        args.Should().Contain("-p");
        args.Should().Contain("estimate this");
    }

    [Fact]
    public void Asks_for_json_output()
    {
        Build().Should().ContainInConsecutiveOrder("--output-format", "json");
    }

    [Fact]
    public void Passes_the_estimate_schema()
    {
        var args = Build();
        var schema = args[args.ToList().IndexOf("--json-schema") + 1];

        schema.Should().Contain("implementation_minutes")
              .And.Contain("testing_minutes")
              .And.Contain("review_minutes")
              .And.Contain("confidence")
              .And.Contain("rationale");
    }

    // --restricted removes Bash, PowerShell and the REPL, so the run cannot execute anything.
    [Fact]
    public void Runs_restricted()
    {
        Build().Should().Contain("--restricted");
    }

    // Second barrier: plan mode refuses writes even if a tool slipped through.
    [Fact]
    public void Runs_in_plan_mode()
    {
        Build().Should().ContainInConsecutiveOrder("--permission-mode", "plan");
    }

    /// <summary>
    /// A spawned session otherwise inherits every MCP server the user has connected. In a live
    /// probe that meant Gmail, Slack, Jira and Drive tools were loaded - 26k tokens of
    /// definitions on every run, and an estimator holding the ability to send mail and edit
    /// Jira. --restricted does not remove those; only this does.
    /// </summary>
    [Fact]
    public void Refuses_to_inherit_the_users_mcp_servers()
    {
        Build().Should().Contain("--strict-mcp-config");
    }

    /// <summary>
    /// Subagent fan-out was the dominant cost in the first live run: one spawned Task spent
    /// 28k tokens over 24 tool calls before the budget stopped the session. Sizing a ticket
    /// needs one focused session, not a fleet.
    /// </summary>
    [Fact]
    public void Does_not_let_the_run_spawn_subagents()
    {
        Build().Should().ContainInConsecutiveOrder("--disallowedTools", "Task");
    }

    [Fact]
    public void Caps_the_spend_of_a_single_run()
    {
        Joined(budget: 2.50m).Should().Contain("--max-budget-usd").And.Contain("2.5");
    }

    [Fact]
    public void Selects_the_configured_model()
    {
        Build(model: "sonnet").Should().ContainInConsecutiveOrder("--model", "sonnet");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Leaves_the_model_to_the_cli_default_when_unset(string? model)
    {
        Build(model: model).Should().NotContain("--model");
    }

    /// <summary>
    /// The estimator runs unattended against the user's real repositories. Nothing that grants
    /// write or bypasses the permission prompt may ever appear here, so it is asserted rather
    /// than left to review.
    /// </summary>
    [Theory]
    [InlineData("--allow-dangerously-skip-permissions")]
    [InlineData("bypassPermissions")]
    [InlineData("acceptEdits")]
    [InlineData("--add-dir")]
    public void Never_grants_write_or_bypasses_permissions(string forbidden)
    {
        Joined().Should().NotContain(forbidden);
    }

    // The budget is formatted for the CLI, not for the user's locale: a comma decimal
    // separator would be read as a different number or rejected outright.
    [Fact]
    public void Formats_the_budget_invariantly()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            Joined(budget: 2.50m).Should().Contain("2.5").And.NotContain("2,5");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
