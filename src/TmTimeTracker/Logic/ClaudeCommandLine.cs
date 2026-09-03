using System.Globalization;

namespace TmTimeTracker.Logic;

/// <summary>
/// Builds the argument list for one estimation run.
///
/// Separated from the process launcher and covered by tests because the safety flags are the
/// only thing standing between an unattended five-minute loop and a model with write access to
/// the user's repositories. A flag quietly dropped here would not fail anything visible.
/// </summary>
public static class ClaudeCommandLine
{
    /// <summary>
    /// Constrains the answer to the five fields the estimate needs. The CLI validates against
    /// this, and <see cref="EstimateResult"/> validates again - the guarantee should not live
    /// only outside our own code.
    /// </summary>
    public const string Schema = """
        {"type":"object","properties":{"implementation_minutes":{"type":"integer","minimum":1},"testing_minutes":{"type":"integer","minimum":1},"review_minutes":{"type":"integer","minimum":1},"confidence":{"type":"string","enum":["low","medium","high"]},"rationale":{"type":"string","minLength":1}},"required":["implementation_minutes","testing_minutes","review_minutes","confidence","rationale"],"additionalProperties":false}
        """;

    public static IReadOnlyList<string> Build(string prompt, string? model, decimal maxBudgetUsd)
    {
        var args = new List<string>
        {
            "-p", prompt,
            "--output-format", "json",
            "--json-schema", Schema,

            // Removes Bash, PowerShell and the REPL: the run cannot execute anything.
            "--restricted",

            // Second barrier - refuses writes even if a tool were to slip past --restricted.
            "--permission-mode", "plan",

            // No subagents. A live run spent 28k tokens and 24 tool calls inside a single
            // spawned Task before the budget stopped it - fan-out is the dominant cost here, and
            // one focused session is enough to size a ticket.
            "--disallowedTools", "Task",

            // Without this the child inherits every MCP server the user has connected. A live
            // probe loaded Gmail, Slack, Jira and Drive: 26k tokens of tool definitions on every
            // run, and an estimator able to send mail and edit Jira issues. --restricted does not
            // touch MCP tools; only this does.
            "--strict-mcp-config",

            // Formatted invariantly: a locale with a comma decimal separator would otherwise
            // hand the CLI a different number, or one it rejects.
            "--max-budget-usd", maxBudgetUsd.ToString(CultureInfo.InvariantCulture)
        };

        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add("--model");
            args.Add(model);
        }

        return args;
    }

    /// <summary>
    /// A minimal real session used by the Settings "Test" button. It has to actually reach the
    /// model: the failure this exists to catch is an expired login, and only a real call sees
    /// that. Kept on the cheapest model with a small budget, and under the same safety flags as
    /// a real run.
    /// </summary>
    public static IReadOnlyList<string> BuildCheck() => new[]
    {
        "-p", "Reply with the single word OK.",
        "--output-format", "json",
        "--restricted",
        "--permission-mode", "plan",
        "--strict-mcp-config",
        "--max-budget-usd", "0.25",
        "--model", "haiku"
    };
}
