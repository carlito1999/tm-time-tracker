# Slack Review Notifications Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a tracked ticket transitions into the configured review status, post a templated message to a per-project Slack channel, authored by the user.

**Architecture:** A new `ReviewSlackNotifier` background service subscribes to the existing `IEventBus` and reacts to `JiraStatusTransition` — an event `JiraPollService` already publishes edge-triggered. It resolves the Jira project key from the ticket key, looks up that project's Slack channel and message template, renders the template through a pure function, and posts via `chat.postMessage` using a user token. All decision logic lives in pure `Logic/` classes; all I/O lives behind thin clients and Dapper repositories.

**Tech Stack:** C# / .NET 8 (`net8.0-windows`), WinForms, Dapper + Microsoft.Data.Sqlite, Microsoft.Extensions.Hosting, Serilog. Tests: xUnit 2.5.3, FluentAssertions 6.12, Moq 4.20, WireMock.Net 1.5.46.

**Spec:** [docs/superpowers/specs/2026-09-02-slack-review-notifications-design.md](../specs/2026-09-02-slack-review-notifications-design.md)

## Global Constraints

- Target framework `net8.0-windows`; `Nullable` and `ImplicitUsings` are `enable` in both projects.
- **`Data/Schema.sql` contains only `CREATE TABLE IF NOT EXISTS`.** `DatabaseInitializer` has no `ALTER TABLE` path, so never add a column to an existing table — new state goes in a new table.
- **No test performs network or real-filesystem I/O.** HTTP is faked with WireMock.Net; SQLite uses `SharedSqlite.NewInMemory()`.
- Test style: xUnit `[Fact]`/`[Theory]` with FluentAssertions (`x.Should().Be(y)`), namespace `TmTimeTracker.Tests.<Layer>`.
- **Never send the Slack `as_user` parameter** — it is legacy and returns `as_user_not_supported`. Authorship comes from the user token.
- **Always check the `ok` boolean** on every Slack response before using it.
- Slack user-token scopes are exactly: `chat:write`, `channels:read`, `groups:read`.
- Project-key regex must stay consistent with the existing `TicketKeyExtractor`: `[A-Z][A-Z0-9_]+` — at least two characters, so `A-1` is not a key.
- Every commit message ends with:
  ```
  Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01W5AMnBqnhqr4LVRF2GUDmZ
  ```
- Run tests with `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj`.

## File Structure

**Create — production:**

| File | Responsibility |
| ---- | -------------- |
| `src/TmTimeTracker/Logic/JiraProjectKey.cs` | `"SN-296"` → `"SN"`. Pure |
| `src/TmTimeTracker/Logic/MessageTemplateRenderer.cs` | `{VAR}` substitution. Pure |
| `src/TmTimeTracker/Logic/SlackVariables.cs` | Variable catalog + dictionary builder. Pure |
| `src/TmTimeTracker/Logic/ChannelSuggester.cs` | Project name → best-guess channel. Pure |
| `src/TmTimeTracker/Data/SlackCredentialRepository.cs` | One-row DPAPI token store |
| `src/TmTimeTracker/Data/SlackChannelRepository.cs` | Per-project channel + template |
| `src/TmTimeTracker/Data/JiraSiteRepository.cs` | One-row site-URL cache keyed by cloud id |
| `src/TmTimeTracker/Slack/SlackDtos.cs` | Slack response records |
| `src/TmTimeTracker/Slack/ISlackTokenSource.cs` | Token indirection for testing |
| `src/TmTimeTracker/Slack/SlackApiClient.cs` | `auth.test`, `conversations.list`, `chat.postMessage` |
| `src/TmTimeTracker/Services/JiraSiteResolver.cs` | Lazy, self-healing site-URL resolution |
| `src/TmTimeTracker/Services/ReviewSlackNotifier.cs` | Bus subscriber; orchestrates the pipeline |
| `src/TmTimeTracker/UI/SettingsPages/SlackPage.cs` | Token, per-project grid, variables panel, preview |
| `docs/slack-app-manifest.yml` | Pasteable Slack app manifest — the user's whole setup surface |

**Modify:**

| File | Change |
| ---- | ------ |
| `src/TmTimeTracker/Data/Schema.sql` | +3 tables |
| `src/TmTimeTracker/Jira/JiraDtos.cs` | `IssueFields.Summary`; `JiraProject` record |
| `src/TmTimeTracker/Jira/JiraApiClient.cs` | Fetch `summary`; add `ListProjectsAsync` |
| `src/TmTimeTracker/Services/DomainEvents.cs` | `JiraStatusTransition` += `Summary`, `MinutesActive` |
| `src/TmTimeTracker/Services/JiraPollService.cs` | Populate the two new event fields |
| `src/TmTimeTracker/HostingExtensions.cs` | Register Slack services |
| `src/TmTimeTracker/UI/SettingsWindow.cs` | Add the "Slack" tab |

**Create — tests:** one file per production unit, mirroring the layout above under `tests/TmTimeTracker.Tests/`.

---

### Task 1: JiraProjectKey

**Files:**
- Create: `src/TmTimeTracker/Logic/JiraProjectKey.cs`
- Test: `tests/TmTimeTracker.Tests/Logic/JiraProjectKeyTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `static string? JiraProjectKey.From(string? ticketKey)` — returns the project key, or `null` when the input is not a well-formed issue key.

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class JiraProjectKeyTests
{
    [Theory]
    [InlineData("SN-296", "SN")]
    [InlineData("TM-51", "TM")]
    [InlineData("ABC_D-12", "ABC_D")]
    [InlineData("CORE_API-42", "CORE_API")]
    [InlineData("V2-9", "V2")]
    public void Extracts_project_key(string ticket, string expected)
    {
        JiraProjectKey.From(ticket).Should().Be(expected);
    }

    [Theory]
    [InlineData("nokey")]
    [InlineData("SN-")]
    [InlineData("-296")]
    [InlineData("sn-296")]
    [InlineData("A-1")]
    [InlineData("SN-296-372")]
    [InlineData("")]
    [InlineData(null)]
    public void Returns_null_for_malformed_input(string? ticket)
    {
        JiraProjectKey.From(ticket).Should().BeNull();
    }

    [Fact]
    public void Trims_surrounding_whitespace()
    {
        JiraProjectKey.From("  SN-296  ").Should().Be("SN");
    }
}
```

Note `"SN-296-372"` returns null: this function takes a *ticket key*, not a branch name. Branch names are handled by `TicketKeyExtractor`, which runs first.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj --filter JiraProjectKeyTests`
Expected: build failure — `JiraProjectKey` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Text.RegularExpressions;

namespace TmTimeTracker.Logic;

public static class JiraProjectKey
{
    private static readonly Regex Pattern = new(@"^(?<project>[A-Z][A-Z0-9_]+)-\d+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string? From(string? ticketKey)
    {
        if (string.IsNullOrWhiteSpace(ticketKey)) return null;
        var match = Pattern.Match(ticketKey.Trim());
        return match.Success ? match.Groups["project"].Value : null;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj --filter JiraProjectKeyTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/TmTimeTracker/Logic/JiraProjectKey.cs tests/TmTimeTracker.Tests/Logic/JiraProjectKeyTests.cs
git commit -m "feat(logic): JiraProjectKey derives project key from a ticket key"
```

---

### Task 2: MessageTemplateRenderer

**Files:**
- Create: `src/TmTimeTracker/Logic/MessageTemplateRenderer.cs`
- Test: `tests/TmTimeTracker.Tests/Logic/MessageTemplateRendererTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `static string MessageTemplateRenderer.Render(string? template, IReadOnlyDictionary<string, string> variables)`.

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class MessageTemplateRendererTests
{
    private static Dictionary<string, string> Vars() => new(StringComparer.Ordinal)
    {
        ["TICKET"] = "SN-296",
        ["SUMMARY"] = "Fix Tolgee warning",
        ["TO"] = "Review",
    };

    [Fact]
    public void Substitutes_known_placeholders()
    {
        MessageTemplateRenderer.Render("{TICKET} -> {TO}", Vars())
            .Should().Be("SN-296 -> Review");
    }

    [Fact]
    public void Leaves_unknown_placeholder_literal()
    {
        MessageTemplateRenderer.Render("{TICKET} {URL}", Vars())
            .Should().Be("SN-296 {URL}");
    }

    [Fact]
    public void Does_not_rescan_substituted_text()
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SUMMARY"] = "Rename {TICKET} everywhere",
            ["TICKET"] = "SN-296",
        };
        MessageTemplateRenderer.Render("{SUMMARY}", vars)
            .Should().Be("Rename {TICKET} everywhere");
    }

    [Fact]
    public void Template_without_placeholders_is_unchanged()
    {
        MessageTemplateRenderer.Render("plain text", Vars()).Should().Be("plain text");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_or_empty_template_renders_empty(string? template)
    {
        MessageTemplateRenderer.Render(template, Vars()).Should().Be("");
    }

    [Fact]
    public void Lowercase_placeholders_are_not_substituted()
    {
        MessageTemplateRenderer.Render("{ticket}", Vars()).Should().Be("{ticket}");
    }
}
```

`Does_not_rescan_substituted_text` is the security-relevant one: a Jira summary that happens to contain braces must never be re-interpreted as a placeholder.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj --filter MessageTemplateRendererTests`
Expected: build failure — `MessageTemplateRenderer` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Text.RegularExpressions;

namespace TmTimeTracker.Logic;

public static class MessageTemplateRenderer
{
    private static readonly Regex Placeholder = new(@"\{(?<name>[A-Z][A-Z0-9_]*)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Render(string? template, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;

        return Placeholder.Replace(template, match =>
            variables.TryGetValue(match.Groups["name"].Value, out var value)
                ? value
                : match.Value);
    }
}
```

`Regex.Replace` with a `MatchEvaluator` walks the *input* once and never re-examines what the evaluator returns, which is what gives us the no-rescan guarantee for free.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj --filter MessageTemplateRendererTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/TmTimeTracker/Logic/MessageTemplateRenderer.cs tests/TmTimeTracker.Tests/Logic/MessageTemplateRendererTests.cs
git commit -m "feat(logic): MessageTemplateRenderer substitutes {VAR} placeholders"
```

---

### Task 3: SlackVariables

**Files:**
- Create: `src/TmTimeTracker/Logic/SlackVariables.cs`
- Test: `tests/TmTimeTracker.Tests/Logic/SlackVariablesTests.cs`

**Interfaces:**
- Consumes: `JiraProjectKey.From` (Task 1).
- Produces:
  - `static IReadOnlyList<SlackVariable> SlackVariables.Catalog` where `SlackVariable` is `record SlackVariable(string Name, string Description, string Sample)`.
  - `static IReadOnlyDictionary<string,string> SlackVariables.Build(string ticketKey, string? summary, string fromStatus, string toStatus, int minutesActive, DateTime atUtc, string? siteUrl)`.
  - `static IReadOnlyDictionary<string,string> SlackVariables.Sample()` — sample values for the Settings preview.
  - `static string SlackVariables.FormatHours(int minutes)`.
  - `const string SlackVariables.DefaultTemplate`.

**Critical rule (spec §6.2):** a variable whose value is unavailable is **omitted from the dictionary**, never mapped to `""`. Omission is what makes an unresolved `{URL}` render literally instead of leaving a silent blank line.

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class SlackVariablesTests
{
    private static IReadOnlyDictionary<string, string> Build(
        string? summary = "Fix Tolgee warning", string? siteUrl = "https://tcubeee.atlassian.net") =>
        SlackVariables.Build("SN-296", summary, "In Progress", "Review", 137,
            new DateTime(2026, 9, 2, 11, 31, 0, DateTimeKind.Utc), siteUrl);

    [Fact]
    public void Populates_every_variable_when_all_data_is_present()
    {
        var v = Build();
        v["TICKET"].Should().Be("SN-296");
        v["PROJECT"].Should().Be("SN");
        v["SUMMARY"].Should().Be("Fix Tolgee warning");
        v["URL"].Should().Be("https://tcubeee.atlassian.net/browse/SN-296");
        v["FROM"].Should().Be("In Progress");
        v["TO"].Should().Be("Review");
        v["MINUTES"].Should().Be("137");
        v["HOURS"].Should().Be("2h 17m");
    }

    [Fact]
    public void Omits_url_when_site_url_is_unknown()
    {
        Build(siteUrl: null).ContainsKey("URL").Should().BeFalse();
    }

    [Fact]
    public void Omits_summary_when_blank()
    {
        Build(summary: "  ").ContainsKey("SUMMARY").Should().BeFalse();
    }

    [Fact]
    public void Strips_trailing_slash_from_site_url()
    {
        Build(siteUrl: "https://tcubeee.atlassian.net/")["URL"]
            .Should().Be("https://tcubeee.atlassian.net/browse/SN-296");
    }

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(45, "45m")]
    [InlineData(60, "1h")]
    [InlineData(137, "2h 17m")]
    public void Formats_hours(int minutes, string expected)
    {
        SlackVariables.FormatHours(minutes).Should().Be(expected);
    }

    [Fact]
    public void Catalog_matches_the_keys_build_can_emit()
    {
        var produced = Build().Keys.ToHashSet();
        var advertised = SlackVariables.Catalog.Select(c => c.Name).ToHashSet();
        advertised.Should().BeEquivalentTo(produced);
    }

    [Fact]
    public void Default_template_only_references_catalogued_variables()
    {
        var rendered = MessageTemplateRenderer.Render(
            SlackVariables.DefaultTemplate, SlackVariables.Sample());
        rendered.Should().NotContain("{");
    }
}
```

`Catalog_matches_the_keys_build_can_emit` is the drift guard from spec §5: the UI's variables panel renders from `Catalog`, so if someone adds a variable to `Build` without cataloguing it (or vice versa) this test fails.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj --filter SlackVariablesTests`
Expected: build failure — `SlackVariables` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Globalization;

namespace TmTimeTracker.Logic;

public sealed record SlackVariable(string Name, string Description, string Sample);

public static class SlackVariables
{
    public const string DefaultTemplate =
        "Done ✅ {TICKET} — {SUMMARY}\n{FROM} -> {TO} · {HOURS}\n{URL}";

    public static IReadOnlyList<SlackVariable> Catalog { get; } = new[]
    {
        new SlackVariable("TICKET",  "Issue key",             "SN-296"),
        new SlackVariable("PROJECT", "Project key",           "SN"),
        new SlackVariable("SUMMARY", "Issue summary",         "Fix Tolgee warning"),
        new SlackVariable("URL",     "Link to the issue",     "https://example.atlassian.net/browse/SN-296"),
        new SlackVariable("FROM",    "Previous status",       "In Progress"),
        new SlackVariable("TO",      "New status",            "Review"),
        new SlackVariable("MINUTES", "Tracked minutes",       "137"),
        new SlackVariable("HOURS",   "Tracked time",          "2h 17m"),
        new SlackVariable("DATE",    "Local date and time",   "2026-09-02 14:31"),
    };

    public static IReadOnlyDictionary<string, string> Build(
        string ticketKey, string? summary, string fromStatus, string toStatus,
        int minutesActive, DateTime atUtc, string? siteUrl)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TICKET"]  = ticketKey,
            ["FROM"]    = fromStatus,
            ["TO"]      = toStatus,
            ["MINUTES"] = minutesActive.ToString(CultureInfo.InvariantCulture),
            ["HOURS"]   = FormatHours(minutesActive),
            ["DATE"]    = atUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        };

        var project = JiraProjectKey.From(ticketKey);
        if (project is not null) vars["PROJECT"] = project;

        if (!string.IsNullOrWhiteSpace(summary)) vars["SUMMARY"] = summary;

        if (!string.IsNullOrWhiteSpace(siteUrl))
            vars["URL"] = $"{siteUrl.TrimEnd('/')}/browse/{ticketKey}";

        return vars;
    }

    public static IReadOnlyDictionary<string, string> Sample() =>
        Catalog.ToDictionary(c => c.Name, c => c.Sample, StringComparer.Ordinal);

    public static string FormatHours(int minutes)
    {
        if (minutes < 60) return $"{minutes}m";
        var hours = minutes / 60;
        var rest = minutes % 60;
        return rest == 0 ? $"{hours}h" : $"{hours}h {rest}m";
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj --filter SlackVariablesTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/TmTimeTracker/Logic/SlackVariables.cs tests/TmTimeTracker.Tests/Logic/SlackVariablesTests.cs
git commit -m "feat(logic): SlackVariables catalog and builder, omitting unavailable values"
```

---

### Task 4: ChannelSuggester

**Files:**
- Create: `src/TmTimeTracker/Logic/ChannelSuggester.cs`
- Test: `tests/TmTimeTracker.Tests/Logic/ChannelSuggesterTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `static string? ChannelSuggester.Suggest(string? projectName, IEnumerable<string> channelNames)` — returns the single plausible channel name, or `null` when there is no match or more than one.

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class ChannelSuggesterTests
{
    private static readonly string[] Channels =
        { "general", "sheeponline", "training-manager", "thecube-site", "ai-content-studio" };

    [Theory]
    [InlineData("SheepOnline New", "sheeponline")]
    [InlineData("Training Manager", "training-manager")]
    [InlineData("The Cube Site", "thecube-site")]
    public void Suggests_the_matching_channel(string projectName, string expected)
    {
        ChannelSuggester.Suggest(projectName, Channels).Should().Be(expected);
    }

    [Fact]
    public void Returns_null_when_nothing_matches()
    {
        ChannelSuggester.Suggest("Dynatag Codes", Channels).Should().BeNull();
    }

    [Fact]
    public void Returns_null_rather_than_guessing_between_two_candidates()
    {
        var ambiguous = new[] { "sheeponline", "sheeponline-new" };
        ChannelSuggester.Suggest("SheepOnline New", ambiguous).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Returns_null_for_blank_project_name(string? projectName)
    {
        ChannelSuggester.Suggest(projectName, Channels).Should().BeNull();
    }

    [Fact]
    public void Empty_channel_list_yields_null()
    {
        ChannelSuggester.Suggest("SheepOnline New", Array.Empty<string>()).Should().BeNull();
    }
}
```

Returning `null` on ambiguity is deliberate: a wrong pre-selection that the user clicks past is worse than no pre-selection, because it posts to the wrong channel silently.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj --filter ChannelSuggesterTests`
Expected: build failure — `ChannelSuggester` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace TmTimeTracker.Logic;

public static class ChannelSuggester
{
    public static string? Suggest(string? projectName, IEnumerable<string> channelNames)
    {
        var needle = Normalize(projectName);
        if (needle.Length == 0) return null;

        var matches = channelNames
            .Where(name =>
            {
                var candidate = Normalize(name);
                return candidate.Length > 0
                    && (needle.StartsWith(candidate, StringComparison.Ordinal)
                     || candidate.StartsWith(needle, StringComparison.Ordinal));
            })
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    private static string Normalize(string? value) =>
        new((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj --filter ChannelSuggesterTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/TmTimeTracker/Logic/ChannelSuggester.cs tests/TmTimeTracker.Tests/Logic/ChannelSuggesterTests.cs
git commit -m "feat(logic): ChannelSuggester pre-matches Jira projects to Slack channels"
```

---

### Task 5: Schema and Slack repositories

**Files:**
- Modify: `src/TmTimeTracker/Data/Schema.sql` (append)
- Create: `src/TmTimeTracker/Data/SlackCredentialRepository.cs`
- Create: `src/TmTimeTracker/Data/SlackChannelRepository.cs`
- Test: `tests/TmTimeTracker.Tests/Data/SlackRepositoriesTests.cs`

**Interfaces:**
- Consumes: `ISqliteConnectionFactory`, `ITokenProtector` (both existing).
- Produces:
  - `record SlackChannelMapping(string ProjectKey, string ChannelId, string ChannelName, string MessageTemplate)`
  - `SlackChannelRepository`: `void Upsert(SlackChannelMapping m)`, `SlackChannelMapping? Find(string projectKey)`, `IReadOnlyList<SlackChannelMapping> GetAll()`, `void Remove(string projectKey)`
  - `SlackCredentialRepository`: `void Save(string token)`, `string? Get()`, `void Clear()`

- [ ] **Step 1: Append the schema**

Append to `src/TmTimeTracker/Data/Schema.sql`:

```sql
CREATE TABLE IF NOT EXISTS slack_credential (
    id          INTEGER PRIMARY KEY CHECK(id = 1),
    token_dpapi BLOB NOT NULL
);

CREATE TABLE IF NOT EXISTS slack_channel (
    project_key      TEXT PRIMARY KEY,
    channel_id       TEXT NOT NULL,
    channel_name     TEXT NOT NULL,
    message_template TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS jira_site (
    id       INTEGER PRIMARY KEY CHECK(id = 1),
    cloud_id TEXT NOT NULL,
    site_url TEXT NOT NULL
);
```

`jira_site` is created here so all schema lands in one commit; its repository arrives in Task 9.

- [ ] **Step 2: Write the failing test**

```csharp
using FluentAssertions;
using Moq;
using TmTimeTracker.Data;
using TmTimeTracker.Platform;
using Xunit;

namespace TmTimeTracker.Tests.Data;

public class SlackRepositoriesTests
{
    private static ISqliteConnectionFactory NewDb()
    {
        var factory = SharedSqlite.NewInMemory();
        new DatabaseInitializer(factory).EnsureCreated();
        return factory;
    }

    private static SlackChannelMapping Mapping(string project = "SN") =>
        new(project, "C0809CK6C14", "sheeponline", "Done {TICKET}");

    [Fact]
    public void Channel_upsert_then_find_round_trips()
    {
        var repo = new SlackChannelRepository(NewDb());
        repo.Upsert(Mapping());

        var found = repo.Find("SN");
        found.Should().NotBeNull();
        found!.ChannelId.Should().Be("C0809CK6C14");
        found.ChannelName.Should().Be("sheeponline");
        found.MessageTemplate.Should().Be("Done {TICKET}");
    }

    [Fact]
    public void Channel_upsert_replaces_an_existing_project_row()
    {
        var repo = new SlackChannelRepository(NewDb());
        repo.Upsert(Mapping());
        repo.Upsert(new SlackChannelMapping("SN", "C999", "elsewhere", "New {TICKET}"));

        repo.GetAll().Should().HaveCount(1);
        repo.Find("SN")!.ChannelName.Should().Be("elsewhere");
    }

    [Fact]
    public void Channel_find_returns_null_for_unknown_project()
    {
        new SlackChannelRepository(NewDb()).Find("NOPE").Should().BeNull();
    }

    [Fact]
    public void Channel_remove_deletes_the_row()
    {
        var repo = new SlackChannelRepository(NewDb());
        repo.Upsert(Mapping());
        repo.Remove("SN");
        repo.Find("SN").Should().BeNull();
    }

    [Fact]
    public void Credential_save_get_clear_round_trips_through_the_protector()
    {
        var protector = new Mock<ITokenProtector>();
        protector.Setup(p => p.Protect("xoxp-secret")).Returns(new byte[] { 1, 2, 3 });
        protector.Setup(p => p.Unprotect(It.Is<byte[]>(b => b.Length == 3))).Returns("xoxp-secret");

        var repo = new SlackCredentialRepository(NewDb(), protector.Object);
        repo.Get().Should().BeNull();

        repo.Save("xoxp-secret");
        repo.Get().Should().Be("xoxp-secret");

        repo.Clear();
        repo.Get().Should().BeNull();
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj --filter SlackRepositoriesTests`
Expected: build failure — repositories do not exist.

- [ ] **Step 4: Implement SlackChannelRepository**

```csharp
using Dapper;

namespace TmTimeTracker.Data;

public sealed record SlackChannelMapping(
    string ProjectKey, string ChannelId, string ChannelName, string MessageTemplate);

public sealed class SlackChannelRepository
{
    private readonly ISqliteConnectionFactory _factory;
    public SlackChannelRepository(ISqliteConnectionFactory factory) => _factory = factory;

    public void Upsert(SlackChannelMapping mapping)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO slack_channel (project_key, channel_id, channel_name, message_template)
              VALUES (@pk, @cid, @cname, @tpl)
              ON CONFLICT(project_key) DO UPDATE SET
                channel_id=excluded.channel_id,
                channel_name=excluded.channel_name,
                message_template=excluded.message_template",
            new
            {
                pk = mapping.ProjectKey,
                cid = mapping.ChannelId,
                cname = mapping.ChannelName,
                tpl = mapping.MessageTemplate
            });
    }

    public SlackChannelMapping? Find(string projectKey)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            "SELECT * FROM slack_channel WHERE project_key = @pk", new { pk = projectKey });
        return row is null ? null : Map(row);
    }

    public IReadOnlyList<SlackChannelMapping> GetAll()
    {
        using var conn = _factory.Open();
        return conn.Query<Row>("SELECT * FROM slack_channel ORDER BY project_key")
                   .Select(Map).ToList();
    }

    public void Remove(string projectKey)
    {
        using var conn = _factory.Open();
        conn.Execute("DELETE FROM slack_channel WHERE project_key = @pk", new { pk = projectKey });
    }

    private static SlackChannelMapping Map(Row r) =>
        new(r.project_key, r.channel_id, r.channel_name, r.message_template);

    private sealed class Row
    {
        public string project_key { get; set; } = "";
        public string channel_id { get; set; } = "";
        public string channel_name { get; set; } = "";
        public string message_template { get; set; } = "";
    }
}
```

- [ ] **Step 5: Implement SlackCredentialRepository**

```csharp
using Dapper;
using TmTimeTracker.Platform;

namespace TmTimeTracker.Data;

public sealed class SlackCredentialRepository
{
    private readonly ISqliteConnectionFactory _factory;
    private readonly ITokenProtector _protector;

    public SlackCredentialRepository(ISqliteConnectionFactory factory, ITokenProtector protector)
    {
        _factory = factory;
        _protector = protector;
    }

    public void Save(string token)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO slack_credential (id, token_dpapi) VALUES (1, @t)
              ON CONFLICT(id) DO UPDATE SET token_dpapi = excluded.token_dpapi",
            new { t = _protector.Protect(token) });
    }

    public string? Get()
    {
        using var conn = _factory.Open();
        var blob = conn.QueryFirstOrDefault<byte[]>(
            "SELECT token_dpapi FROM slack_credential WHERE id = 1");
        return blob is null ? null : _protector.Unprotect(blob);
    }

    public void Clear()
    {
        using var conn = _factory.Open();
        conn.Execute("DELETE FROM slack_credential WHERE id = 1");
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj --filter SlackRepositoriesTests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/TmTimeTracker/Data/ tests/TmTimeTracker.Tests/Data/SlackRepositoriesTests.cs
git commit -m "feat(data): slack_credential, slack_channel and jira_site tables with repositories"
```

---

### Task 6: Jira issue summary and project listing

**Files:**
- Modify: `src/TmTimeTracker/Jira/JiraDtos.cs`
- Modify: `src/TmTimeTracker/Jira/JiraApiClient.cs`
- Test: `tests/TmTimeTracker.Tests/Jira/JiraApiClientTests.cs` (append)

**Interfaces:**
- Consumes: existing `IAccessTokenSource`.
- Produces:
  - `IssueFields.Summary` — `string?`
  - `record JiraProject(string Key, string Name)`
  - `Task<IReadOnlyList<JiraProject>> JiraApiClient.ListProjectsAsync(CancellationToken ct)`

- [ ] **Step 1: Write the failing tests**

Append to `tests/TmTimeTracker.Tests/Jira/JiraApiClientTests.cs`:

```csharp
    [Fact]
    public async Task GetIssue_requests_summary_and_returns_it()
    {
        var src = new Mock<IAccessTokenSource>();
        src.Setup(s => s.GetAccessTokenAsync(It.IsAny<CancellationToken>()))
           .ReturnsAsync(("at-1", "cloud-1"));

        _server.Given(Request.Create().WithPath("/ex/jira/cloud-1/rest/api/3/issue/SN-296").UsingGet()
                        .WithParam("fields", "status,summary"))
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   key = "SN-296",
                   fields = new
                   {
                       summary = "Fix Tolgee warning",
                       status = new { name = "Review",
                           statusCategory = new { key = "indeterminate", name = "In Progress" } }
                   }
               }));

        var issue = await New(src.Object).GetIssueAsync("SN-296", CancellationToken.None);
        issue.Fields.Summary.Should().Be("Fix Tolgee warning");
    }

    [Fact]
    public async Task ListProjects_returns_key_and_name_pairs()
    {
        var src = new Mock<IAccessTokenSource>();
        src.Setup(s => s.GetAccessTokenAsync(It.IsAny<CancellationToken>()))
           .ReturnsAsync(("at-1", "cloud-1"));

        _server.Given(Request.Create().WithPath("/ex/jira/cloud-1/rest/api/3/project/search").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   values = new[]
                   {
                       new { key = "SN", name = "SheepOnline New" },
                       new { key = "TM", name = "Training Manager" }
                   }
               }));

        var projects = await New(src.Object).ListProjectsAsync(CancellationToken.None);
        projects.Should().HaveCount(2);
        projects[0].Key.Should().Be("SN");
        projects[0].Name.Should().Be("SheepOnline New");
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj --filter JiraApiClientTests`
Expected: build failure — `Summary` and `ListProjectsAsync` do not exist.

- [ ] **Step 3: Update the DTOs**

In `src/TmTimeTracker/Jira/JiraDtos.cs`, replace `IssueFields` and add `JiraProject` plus its page wrapper:

```csharp
public sealed record IssueFields(
    [property: JsonPropertyName("status")] IssueStatus Status,
    [property: JsonPropertyName("summary")] string? Summary = null);

public sealed record JiraProject(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name);

public sealed record JiraProjectPage(
    [property: JsonPropertyName("values")] JiraProject[] Values);
```

`Summary` is optional with a default so existing `IssueFields` construction sites and tests keep compiling.

- [ ] **Step 4: Update JiraApiClient**

In `GetIssueAsync`, change the URL builder to request both fields:

```csharp
            cloudId => $"{_apiBase}/{cloudId}/rest/api/3/issue/{key}?fields=status,summary",
```

Then add, next to `GetIssueAsync`:

```csharp
    public async Task<IReadOnlyList<JiraProject>> ListProjectsAsync(CancellationToken ct)
    {
        var (resp, _) = await SendAsync(HttpMethod.Get,
            cloudId => $"{_apiBase}/{cloudId}/rest/api/3/project/search?maxResults=50",
            content: null, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var page = await resp.Content.ReadFromJsonAsync<JiraProjectPage>(cancellationToken: ct)
                                     .ConfigureAwait(false);
        return page?.Values ?? Array.Empty<JiraProject>();
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj --filter JiraApiClientTests`
Expected: PASS — including the pre-existing tests in that file.

- [ ] **Step 6: Commit**

```bash
git add src/TmTimeTracker/Jira/ tests/TmTimeTracker.Tests/Jira/JiraApiClientTests.cs
git commit -m "feat(jira): fetch issue summary and list projects"
```

---

### Task 7: Carry summary and tracked minutes on the transition event

**Files:**
- Modify: `src/TmTimeTracker/Services/DomainEvents.cs:10`
- Modify: `src/TmTimeTracker/Services/JiraPollService.cs:60-66`
- Test: `tests/TmTimeTracker.Tests/Services/JiraPollServiceTests.cs` (create)

**Interfaces:**
- Consumes: `IssueFields.Summary` (Task 6).
- Produces: `record JiraStatusTransition(string TicketKey, string? Summary, string FromStatus, string ToStatus, int MinutesActive, DateTime AtUtc)`.

`JiraPollService` is the only place that constructs this record — verified by grep across `src/` and `tests/`. `DashboardWindow`, `TrayIconHost` and `Theme` only pattern-match on it, so they need no change.

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;
using TmTimeTracker.Services;
using Xunit;

namespace TmTimeTracker.Tests.Services;

public class JiraPollServiceTests
{
    private sealed class CapturingBus : IEventBus
    {
        public List<DomainEvent> Published { get; } = new();
        public ValueTask PublishAsync(DomainEvent evt, CancellationToken ct = default)
        {
            Published.Add(evt);
            return ValueTask.CompletedTask;
        }
        public async IAsyncEnumerable<DomainEvent> Subscribe(CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow { get; } = new(2026, 9, 2, 11, 31, 0, DateTimeKind.Utc);
    }

    [Fact]
    public async Task Transition_event_carries_summary_and_tracked_minutes()
    {
        var factory = SharedSqlite.NewInMemory();
        new DatabaseInitializer(factory).EnsureCreated();

        var tickets = new TicketTimeRepository(factory);
        var cycle = tickets.OpenOrCreateCycle("SN-296",
            new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc));
        for (var i = 0; i < 3; i++) tickets.IncrementMinute(cycle.Id);
        tickets.UpdateStatusSnapshot(cycle.Id, "In Progress",
            new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc));

        var api = new Mock<IJiraIssueSource>();
        api.Setup(a => a.GetIssueAsync("SN-296", It.IsAny<CancellationToken>()))
           .ReturnsAsync(new Issue("SN-296",
               new IssueFields(new IssueStatus("Review", new StatusCategory("indeterminate", "In Progress")),
                               "Fix Tolgee warning")));

        var bus = new CapturingBus();
        var svc = new JiraPollService(tickets, api.Object, new ConfigRepository(factory),
            bus, new FixedClock(), NullLogger<JiraPollService>.Instance, new PollServiceGate());

        await svc.PollOnce("Review", CancellationToken.None);

        var evt = bus.Published.OfType<JiraStatusTransition>().Single();
        evt.TicketKey.Should().Be("SN-296");
        evt.Summary.Should().Be("Fix Tolgee warning");
        evt.FromStatus.Should().Be("In Progress");
        evt.ToStatus.Should().Be("Review");
        evt.MinutesActive.Should().Be(3);
    }
}
```

- [ ] **Step 1b: Introduce the `IJiraIssueSource` seam**

`JiraApiClient` is a `sealed class` with no interface and `JiraPollService` depends on it concretely, so the test above cannot be written without a seam. Create `src/TmTimeTracker/Jira/IJiraIssueSource.cs`:

```csharp
namespace TmTimeTracker.Jira;

public interface IJiraIssueSource
{
    Task<Issue> GetIssueAsync(string key, CancellationToken ct);
}
```

Declare it on the client — the method signature already matches exactly:

```csharp
public sealed class JiraApiClient : IJiraIssueSource
```

Change `JiraPollService`'s field and constructor parameter from `JiraApiClient _api` to `IJiraIssueSource _api`, and register the interface against the existing singleton in `HostingExtensions.AddJiraServices`:

```csharp
            services.AddSingleton<IJiraIssueSource>(sp => sp.GetRequiredService<JiraApiClient>());
```

`WorklogRequestFactory` and the UI keep using the concrete `JiraApiClient` for `PostWorklogAsync`; only the poll service narrows to the interface.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj --filter JiraPollServiceTests`
Expected: build failure — `JiraStatusTransition` has no `Summary`.

- [ ] **Step 3: Widen the event**

In `src/TmTimeTracker/Services/DomainEvents.cs`, replace line 10:

```csharp
public sealed record JiraStatusTransition(
    string TicketKey, string? Summary, string FromStatus, string ToStatus,
    int MinutesActive, DateTime AtUtc) : DomainEvent(AtUtc);
```

- [ ] **Step 4: Populate the new fields**

In `src/TmTimeTracker/Services/JiraPollService.cs`, replace the publish call inside `PollOnce`:

```csharp
                if (issue.Fields.Status.Name == transitionTo && previous != transitionTo)
                {
                    await _bus.PublishAsync(
                        new JiraStatusTransition(
                            cycle.TicketKey,
                            issue.Fields.Summary,
                            previous,
                            issue.Fields.Status.Name,
                            cycle.MinutesActive,
                            nowUtc),
                        ct).ConfigureAwait(false);
                }
```

- [ ] **Step 5: Run the full suite**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj`
Expected: PASS — confirms no other construction site broke.

- [ ] **Step 6: Commit**

```bash
git add src/TmTimeTracker/Services/ tests/TmTimeTracker.Tests/Services/JiraPollServiceTests.cs
git commit -m "feat(services): carry issue summary and tracked minutes on JiraStatusTransition"
```

---

### Task 8: SlackApiClient

**Files:**
- Create: `src/TmTimeTracker/Slack/SlackDtos.cs`
- Create: `src/TmTimeTracker/Slack/ISlackTokenSource.cs`
- Create: `src/TmTimeTracker/Slack/SlackApiClient.cs`
- Test: `tests/TmTimeTracker.Tests/Slack/SlackApiClientTests.cs`

**Interfaces:**
- Consumes: `HttpClient`, `ILogger<SlackApiClient>`, `ISlackTokenSource`.
- Produces:
  - `interface ISlackTokenSource { string? GetToken(); }`
  - `record SlackIdentity(string UserId, string UserName, string TeamName)`
  - `record SlackConversation(string Id, string Name)`
  - `class SlackApiException : Exception { public string SlackError { get; } }`
  - `SlackApiClient`: `Task<SlackIdentity> AuthTestAsync(CancellationToken)`, `Task<IReadOnlyList<SlackConversation>> ListConversationsAsync(CancellationToken)`, `Task PostMessageAsync(string channelId, string text, CancellationToken)`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TmTimeTracker.Slack;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace TmTimeTracker.Tests.Slack;

public class SlackApiClientTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Dispose();

    private sealed class FixedToken : ISlackTokenSource
    {
        private readonly string? _token;
        public FixedToken(string? token) => _token = token;
        public string? GetToken() => _token;
    }

    private SlackApiClient New(string? token = "xoxp-1") =>
        new(new HttpClient(), new FixedToken(token),
            NullLogger<SlackApiClient>.Instance, apiBaseOverride: _server.Url);

    [Fact]
    public async Task AuthTest_returns_identity()
    {
        _server.Given(Request.Create().WithPath("/auth.test").UsingPost()
                        .WithHeader("Authorization", "Bearer xoxp-1"))
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   ok = true, user_id = "U1", user = "lefteris", team = "thecube"
               }));

        var id = await New().AuthTestAsync(CancellationToken.None);
        id.UserName.Should().Be("lefteris");
        id.TeamName.Should().Be("thecube");
    }

    [Fact]
    public async Task Not_ok_response_throws_with_the_slack_error_code()
    {
        _server.Given(Request.Create().WithPath("/auth.test").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                        .WithBodyAsJson(new { ok = false, error = "invalid_auth" }));

        var act = () => New().AuthTestAsync(CancellationToken.None);
        (await act.Should().ThrowAsync<SlackApiException>()).Which.SlackError.Should().Be("invalid_auth");
    }

    [Fact]
    public async Task Missing_token_throws_before_any_request()
    {
        var act = () => New(token: null).AuthTestAsync(CancellationToken.None);
        await act.Should().ThrowAsync<SlackApiException>();
        _server.LogEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task PostMessage_sends_channel_and_text_and_never_sends_as_user()
    {
        _server.Given(Request.Create().WithPath("/chat.postMessage").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                        .WithBodyAsJson(new { ok = true, ts = "1.2" }));

        await New().PostMessageAsync("C1", "hello", CancellationToken.None);

        var body = _server.LogEntries.Single().RequestMessage.Body ?? "";
        body.Should().Contain("\"channel\":\"C1\"");
        body.Should().Contain("\"text\":\"hello\"");
        body.Should().NotContain("as_user");
    }

    [Fact]
    public async Task ListConversations_follows_the_cursor()
    {
        _server.Given(Request.Create().WithPath("/conversations.list").UsingPost()
                        .WithBody(b => b != null && !b.Contains("cursor=")))
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   ok = true,
                   channels = new[] { new { id = "C1", name = "sheeponline" } },
                   response_metadata = new { next_cursor = "CUR2" }
               }));
        _server.Given(Request.Create().WithPath("/conversations.list").UsingPost()
                        .WithBody(b => b != null && b.Contains("cursor=CUR2")))
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   ok = true,
                   channels = new[] { new { id = "C2", name = "training-manager" } },
                   response_metadata = new { next_cursor = "" }
               }));

        var channels = await New().ListConversationsAsync(CancellationToken.None);
        channels.Select(c => c.Name).Should().BeEquivalentTo("sheeponline", "training-manager");
    }

    [Fact]
    public async Task Rate_limit_is_retried_once_after_honouring_retry_after()
    {
        var calls = 0;
        _server.Given(Request.Create().WithPath("/chat.postMessage").UsingPost())
               .RespondWith(Response.Create().WithCallback(_ =>
               {
                   calls++;
                   return calls == 1
                       ? new WireMock.ResponseMessage
                         {
                             StatusCode = (int)HttpStatusCode.TooManyRequests,
                             Headers = new() { ["Retry-After"] = new WireMock.Types.WireMockList<string>("0") }
                         }
                       : new WireMock.ResponseMessage
                         {
                             StatusCode = 200,
                             BodyData = new WireMock.Util.BodyData
                             {
                                 DetectedBodyType = WireMock.Types.BodyType.String,
                                 BodyAsString = "{\"ok\":true}"
                             }
                         };
               }));

        await New().PostMessageAsync("C1", "hi", CancellationToken.None);
        calls.Should().Be(2);
    }
}
```

If the WireMock callback API differs in 1.5.46, simplify the rate-limit test to two sequential `Given` scenarios using `WithGuid`/scenario state — the assertion that matters is *two* calls and no exception.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj --filter SlackApiClientTests`
Expected: build failure — the Slack namespace does not exist.

- [ ] **Step 3: Write the DTOs and token source**

`src/TmTimeTracker/Slack/ISlackTokenSource.cs`:

```csharp
namespace TmTimeTracker.Slack;

public interface ISlackTokenSource
{
    string? GetToken();
}
```

`src/TmTimeTracker/Slack/SlackDtos.cs`:

```csharp
using System.Text.Json.Serialization;

namespace TmTimeTracker.Slack;

public sealed record SlackIdentity(string UserId, string UserName, string TeamName);

public sealed record SlackConversation(string Id, string Name);

public sealed class SlackApiException : Exception
{
    public string SlackError { get; }
    public SlackApiException(string slackError)
        : base($"Slack API returned an error: {slackError}") => SlackError = slackError;
}

internal sealed record AuthTestResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("user_id")] string? UserId,
    [property: JsonPropertyName("user")] string? User,
    [property: JsonPropertyName("team")] string? Team);

internal sealed record ConversationDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);

internal sealed record ResponseMetadata(
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

internal sealed record ConversationsListResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("channels")] ConversationDto[]? Channels,
    [property: JsonPropertyName("response_metadata")] ResponseMetadata? Metadata);

internal sealed record PostMessageResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string? Error);
```

- [ ] **Step 4: Write SlackApiClient**

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace TmTimeTracker.Slack;

public sealed class SlackApiClient
{
    private const string DefaultApiBase = "https://slack.com/api";

    private readonly HttpClient _http;
    private readonly ISlackTokenSource _tokens;
    private readonly ILogger<SlackApiClient> _log;
    private readonly string _apiBase;

    public SlackApiClient(HttpClient http, ISlackTokenSource tokens,
        ILogger<SlackApiClient> log, string? apiBaseOverride = null)
    {
        _http = http; _tokens = tokens; _log = log;
        _apiBase = (apiBaseOverride ?? DefaultApiBase).TrimEnd('/');
    }

    public async Task<SlackIdentity> AuthTestAsync(CancellationToken ct)
    {
        var body = await SendAsync<AuthTestResponse>("auth.test", content: null, ct)
            .ConfigureAwait(false);
        return new SlackIdentity(body.UserId ?? "", body.User ?? "", body.Team ?? "");
    }

    public async Task<IReadOnlyList<SlackConversation>> ListConversationsAsync(CancellationToken ct)
    {
        var all = new List<SlackConversation>();
        string? cursor = null;

        do
        {
            var form = new Dictionary<string, string>
            {
                ["types"] = "public_channel,private_channel",
                ["exclude_archived"] = "true",
                ["limit"] = "200"
            };
            if (!string.IsNullOrEmpty(cursor)) form["cursor"] = cursor;

            var page = await SendAsync<ConversationsListResponse>(
                "conversations.list", new FormUrlEncodedContent(form), ct).ConfigureAwait(false);

            foreach (var c in page.Channels ?? Array.Empty<ConversationDto>())
                all.Add(new SlackConversation(c.Id, c.Name));

            cursor = page.Metadata?.NextCursor;
        }
        while (!string.IsNullOrEmpty(cursor));

        return all;
    }

    public async Task PostMessageAsync(string channelId, string text, CancellationToken ct)
    {
        // Note: as_user is deliberately never sent - it is legacy and returns
        // as_user_not_supported. Authorship comes from the user token itself.
        var payload = JsonContent.Create(new { channel = channelId, text });
        await SendAsync<PostMessageResponse>("chat.postMessage", payload, ct).ConfigureAwait(false);
    }

    private async Task<T> SendAsync<T>(string method, HttpContent? content, CancellationToken ct)
        where T : class
    {
        var token = _tokens.GetToken();
        if (string.IsNullOrWhiteSpace(token))
            throw new SlackApiException("not_configured");

        var response = await SendOnceAsync(method, content, token, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
            _log.LogWarning("Slack rate-limited {Method}; retrying once after {Seconds}s",
                method, wait.TotalSeconds);
            response.Dispose();
            await Task.Delay(wait, ct).ConfigureAwait(false);
            response = await SendOnceAsync(method, content, token, ct).ConfigureAwait(false);
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
                                             .ConfigureAwait(false)
                       ?? throw new SlackApiException("empty_response");

            var ok = (bool?)typeof(T).GetProperty("Ok")?.GetValue(body) ?? false;
            if (!ok)
            {
                var error = (string?)typeof(T).GetProperty("Error")?.GetValue(body) ?? "unknown_error";
                throw new SlackApiException(error);
            }
            return body;
        }
    }

    private Task<HttpResponseMessage> SendOnceAsync(
        string method, HttpContent? content, string token, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiBase}/{method}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (content is not null) request.Content = content;
        return _http.SendAsync(request, ct);
    }
}
```

A retried request cannot reuse a consumed `HttpContent`. If the rate-limit test fails with `ObjectDisposedException`, change `SendAsync` to take a `Func<HttpContent?>` factory and build fresh content per attempt.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj --filter SlackApiClientTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/TmTimeTracker/Slack/ tests/TmTimeTracker.Tests/Slack/
git commit -m "feat(slack): API client for auth.test, conversations.list and chat.postMessage"
```

---

### Task 9: Site-URL resolution

**Files:**
- Create: `src/TmTimeTracker/Data/JiraSiteRepository.cs`
- Create: `src/TmTimeTracker/Services/JiraSiteResolver.cs`
- Test: `tests/TmTimeTracker.Tests/Services/JiraSiteResolverTests.cs`

**Interfaces:**
- Consumes: `OAuthCoordinator.ListAccessibleAsync` (existing, public), `OAuthStateRepository`.
- Produces:
  - `record JiraSite(string CloudId, string SiteUrl)`
  - `JiraSiteRepository`: `void Save(JiraSite site)`, `JiraSite? Get()`
  - `interface IJiraSiteResolver { Task<string?> GetSiteUrlAsync(CancellationToken ct); }`
  - `class JiraSiteResolver : IJiraSiteResolver`

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;
using TmTimeTracker.Services;
using Xunit;

namespace TmTimeTracker.Tests.Services;

public class JiraSiteResolverTests
{
    private static (JiraSiteRepository Repo, ISqliteConnectionFactory Factory) NewRepo()
    {
        var factory = SharedSqlite.NewInMemory();
        new DatabaseInitializer(factory).EnsureCreated();
        return (new JiraSiteRepository(factory), factory);
    }

    private static Mock<IAccessibleSiteSource> Sites(params AtlassianResource[] resources)
    {
        var mock = new Mock<IAccessibleSiteSource>();
        mock.Setup(s => s.ListAccessibleAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(resources);
        return mock;
    }

    [Fact]
    public async Task Fetches_and_caches_the_url_for_the_active_cloud_id()
    {
        var (repo, _) = NewRepo();
        var sites = Sites(new AtlassianResource("cloud-1", "tcubeee",
            "https://tcubeee.atlassian.net", Array.Empty<string>()));

        var resolver = new JiraSiteResolver(repo, sites.Object, () => "cloud-1",
            NullLogger<JiraSiteResolver>.Instance);

        (await resolver.GetSiteUrlAsync(CancellationToken.None))
            .Should().Be("https://tcubeee.atlassian.net");
        repo.Get()!.SiteUrl.Should().Be("https://tcubeee.atlassian.net");
    }

    [Fact]
    public async Task Second_call_uses_the_cache_and_does_not_refetch()
    {
        var (repo, _) = NewRepo();
        var sites = Sites(new AtlassianResource("cloud-1", "t",
            "https://tcubeee.atlassian.net", Array.Empty<string>()));
        var resolver = new JiraSiteResolver(repo, sites.Object, () => "cloud-1",
            NullLogger<JiraSiteResolver>.Instance);

        await resolver.GetSiteUrlAsync(CancellationToken.None);
        await resolver.GetSiteUrlAsync(CancellationToken.None);

        sites.Verify(s => s.ListAccessibleAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_changed_cloud_id_invalidates_the_cache()
    {
        var (repo, _) = NewRepo();
        repo.Save(new JiraSite("cloud-OLD", "https://old.atlassian.net"));

        var sites = Sites(new AtlassianResource("cloud-NEW", "n",
            "https://new.atlassian.net", Array.Empty<string>()));
        var resolver = new JiraSiteResolver(repo, sites.Object, () => "cloud-NEW",
            NullLogger<JiraSiteResolver>.Instance);

        (await resolver.GetSiteUrlAsync(CancellationToken.None))
            .Should().Be("https://new.atlassian.net");
    }

    [Fact]
    public async Task Returns_null_when_the_active_cloud_id_is_not_among_accessible_sites()
    {
        var (repo, _) = NewRepo();
        var sites = Sites(new AtlassianResource("other", "o",
            "https://other.atlassian.net", Array.Empty<string>()));
        var resolver = new JiraSiteResolver(repo, sites.Object, () => "cloud-1",
            NullLogger<JiraSiteResolver>.Instance);

        (await resolver.GetSiteUrlAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_and_does_not_throw_when_the_lookup_fails()
    {
        var (repo, _) = NewRepo();
        var sites = new Mock<IAccessibleSiteSource>();
        sites.Setup(s => s.ListAccessibleAsync(It.IsAny<CancellationToken>()))
             .ThrowsAsync(new HttpRequestException("offline"));
        var resolver = new JiraSiteResolver(repo, sites.Object, () => "cloud-1",
            NullLogger<JiraSiteResolver>.Instance);

        (await resolver.GetSiteUrlAsync(CancellationToken.None)).Should().BeNull();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj --filter JiraSiteResolverTests`
Expected: build failure — the resolver does not exist.

- [ ] **Step 3: Write JiraSiteRepository**

```csharp
using Dapper;

namespace TmTimeTracker.Data;

public sealed record JiraSite(string CloudId, string SiteUrl);

public sealed class JiraSiteRepository
{
    private readonly ISqliteConnectionFactory _factory;
    public JiraSiteRepository(ISqliteConnectionFactory factory) => _factory = factory;

    public void Save(JiraSite site)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO jira_site (id, cloud_id, site_url) VALUES (1, @c, @u)
              ON CONFLICT(id) DO UPDATE SET cloud_id=excluded.cloud_id, site_url=excluded.site_url",
            new { c = site.CloudId, u = site.SiteUrl });
    }

    public JiraSite? Get()
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>("SELECT cloud_id, site_url FROM jira_site WHERE id = 1");
        return row is null ? null : new JiraSite(row.cloud_id, row.site_url);
    }

    private sealed class Row
    {
        public string cloud_id { get; set; } = "";
        public string site_url { get; set; } = "";
    }
}
```

- [ ] **Step 4: Write the resolver and its seam**

Add to `src/TmTimeTracker/Jira/IOAuthAppConfigSource.cs` (or a new file in `Jira/`):

```csharp
namespace TmTimeTracker.Jira;

public interface IAccessibleSiteSource
{
    Task<IReadOnlyList<AtlassianResource>> ListAccessibleAsync(CancellationToken ct);
}
```

Make `OAuthCoordinator` implement it — the method already exists with that exact signature, so this is a declaration change only:

```csharp
public sealed class OAuthCoordinator : IAccessTokenSource, IAccessibleSiteSource
```

`src/TmTimeTracker/Services/JiraSiteResolver.cs`:

```csharp
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;

namespace TmTimeTracker.Services;

public interface IJiraSiteResolver
{
    Task<string?> GetSiteUrlAsync(CancellationToken ct);
}

public sealed class JiraSiteResolver : IJiraSiteResolver
{
    private readonly JiraSiteRepository _repo;
    private readonly IAccessibleSiteSource _sites;
    private readonly Func<string?> _activeCloudId;
    private readonly ILogger<JiraSiteResolver> _log;

    public JiraSiteResolver(JiraSiteRepository repo, IAccessibleSiteSource sites,
        Func<string?> activeCloudId, ILogger<JiraSiteResolver> log)
    {
        _repo = repo; _sites = sites; _activeCloudId = activeCloudId; _log = log;
    }

    public async Task<string?> GetSiteUrlAsync(CancellationToken ct)
    {
        var cloudId = _activeCloudId();
        if (string.IsNullOrEmpty(cloudId)) return null;

        var cached = _repo.Get();
        if (cached is not null && cached.CloudId == cloudId) return cached.SiteUrl;

        try
        {
            var resources = await _sites.ListAccessibleAsync(ct).ConfigureAwait(false);
            var match = resources.FirstOrDefault(r => r.Id == cloudId);
            if (match is null)
            {
                _log.LogWarning("Active cloudId {CloudId} is not among accessible sites", cloudId);
                return null;
            }
            _repo.Save(new JiraSite(match.Id, match.Url));
            return match.Url;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not resolve the Jira site URL; {{URL}} will render literally");
            return null;
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj --filter JiraSiteResolverTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/TmTimeTracker/Data/JiraSiteRepository.cs src/TmTimeTracker/Services/JiraSiteResolver.cs src/TmTimeTracker/Jira/ tests/TmTimeTracker.Tests/Services/JiraSiteResolverTests.cs
git commit -m "feat(services): lazy self-healing Jira site-URL resolution"
```

---

### Task 10: ReviewSlackNotifier and DI wiring

**Files:**
- Create: `src/TmTimeTracker/Services/ReviewSlackNotifier.cs`
- Modify: `src/TmTimeTracker/HostingExtensions.cs`
- Test: `tests/TmTimeTracker.Tests/Services/ReviewSlackNotifierTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-9.
- Produces:
  - `interface ISlackPoster { Task PostMessageAsync(string channelId, string text, CancellationToken ct); }` — implemented by `SlackApiClient` (declaration change only; the method already matches).
  - `ReviewSlackNotifier : BackgroundService` with `internal Task HandleAsync(JiraStatusTransition evt, CancellationToken ct)` for direct testing.

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using TmTimeTracker.Services;
using TmTimeTracker.Slack;
using Xunit;

namespace TmTimeTracker.Tests.Services;

public class ReviewSlackNotifierTests
{
    private static JiraStatusTransition Event(string ticket = "SN-296") =>
        new(ticket, "Fix Tolgee warning", "In Progress", "Review", 137,
            new DateTime(2026, 9, 2, 11, 31, 0, DateTimeKind.Utc));

    private static (ReviewSlackNotifier Notifier, Mock<ISlackPoster> Poster, SlackChannelRepository Channels)
        Build(string? siteUrl = "https://tcubeee.atlassian.net", bool configured = true)
    {
        var factory = SharedSqlite.NewInMemory();
        new DatabaseInitializer(factory).EnsureCreated();

        var channels = new SlackChannelRepository(factory);
        if (configured)
            channels.Upsert(new SlackChannelMapping("SN", "C1", "sheeponline",
                "Done {TICKET} {HOURS} {URL}"));

        var poster = new Mock<ISlackPoster>();
        var resolver = new Mock<IJiraSiteResolver>();
        resolver.Setup(r => r.GetSiteUrlAsync(It.IsAny<CancellationToken>())).ReturnsAsync(siteUrl);

        var notifier = new ReviewSlackNotifier(
            new Mock<IEventBus>().Object, channels, poster.Object, resolver.Object,
            NullLogger<ReviewSlackNotifier>.Instance);

        return (notifier, poster, channels);
    }

    [Fact]
    public async Task Posts_the_rendered_template_to_the_mapped_channel()
    {
        var (notifier, poster, _) = Build();
        await notifier.HandleAsync(Event(), CancellationToken.None);

        poster.Verify(p => p.PostMessageAsync("C1",
            "Done SN-296 2h 17m https://tcubeee.atlassian.net/browse/SN-296",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Unresolved_site_url_leaves_the_placeholder_literal()
    {
        var (notifier, poster, _) = Build(siteUrl: null);
        await notifier.HandleAsync(Event(), CancellationToken.None);

        poster.Verify(p => p.PostMessageAsync("C1",
            "Done SN-296 2h 17m {URL}", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Silent_when_the_project_has_no_channel()
    {
        var (notifier, poster, _) = Build(configured: false);
        await notifier.HandleAsync(Event(), CancellationToken.None);

        poster.Verify(p => p.PostMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Silent_when_the_ticket_key_is_malformed()
    {
        var (notifier, poster, _) = Build();
        await notifier.HandleAsync(Event("garbage"), CancellationToken.None);

        poster.Verify(p => p.PostMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_failing_slack_call_does_not_propagate()
    {
        var (notifier, poster, _) = Build();
        poster.Setup(p => p.PostMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
              .ThrowsAsync(new SlackApiException("channel_not_found"));

        var act = () => notifier.HandleAsync(Event(), CancellationToken.None);
        await act.Should().NotThrowAsync();
    }
}
```

`A_failing_slack_call_does_not_propagate` encodes spec §7: notifications are a side-channel and must never disturb time tracking.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj --filter ReviewSlackNotifierTests`
Expected: build failure — `ReviewSlackNotifier` does not exist.

- [ ] **Step 3: Add the poster seam**

In `src/TmTimeTracker/Slack/ISlackTokenSource.cs` (same file is fine):

```csharp
public interface ISlackPoster
{
    Task PostMessageAsync(string channelId, string text, CancellationToken ct);
}
```

Then declare it on the client — the signature already matches:

```csharp
public sealed class SlackApiClient : ISlackPoster
```

- [ ] **Step 4: Write ReviewSlackNotifier**

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using TmTimeTracker.Slack;

namespace TmTimeTracker.Services;

public sealed class ReviewSlackNotifier : BackgroundService
{
    private readonly IEventBus _bus;
    private readonly SlackChannelRepository _channels;
    private readonly ISlackPoster _slack;
    private readonly IJiraSiteResolver _site;
    private readonly ILogger<ReviewSlackNotifier> _log;

    public ReviewSlackNotifier(IEventBus bus, SlackChannelRepository channels,
        ISlackPoster slack, IJiraSiteResolver site, ILogger<ReviewSlackNotifier> log)
    {
        _bus = bus; _channels = channels; _slack = slack; _site = site; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var evt in _bus.Subscribe(stoppingToken).ConfigureAwait(false))
        {
            if (evt is JiraStatusTransition transition)
                await HandleAsync(transition, stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task HandleAsync(JiraStatusTransition evt, CancellationToken ct)
    {
        try
        {
            var project = JiraProjectKey.From(evt.TicketKey);
            if (project is null)
            {
                _log.LogDebug("No project key in {Ticket}; skipping Slack notification", evt.TicketKey);
                return;
            }

            var mapping = _channels.Find(project);
            if (mapping is null)
            {
                _log.LogDebug("No Slack channel configured for project {Project}; skipping", project);
                return;
            }

            var siteUrl = await _site.GetSiteUrlAsync(ct).ConfigureAwait(false);
            var variables = SlackVariables.Build(evt.TicketKey, evt.Summary,
                evt.FromStatus, evt.ToStatus, evt.MinutesActive, evt.AtUtc, siteUrl);
            var text = MessageTemplateRenderer.Render(mapping.MessageTemplate, variables);

            await _slack.PostMessageAsync(mapping.ChannelId, text, ct).ConfigureAwait(false);
            _log.LogInformation("Posted {Ticket} review notification to #{Channel}",
                evt.TicketKey, mapping.ChannelName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A notification is a side-channel: never let it disturb time tracking.
            _log.LogWarning(ex, "Slack notification for {Ticket} failed", evt.TicketKey);
        }
    }
}
```

- [ ] **Step 5: Wire up DI**

In `src/TmTimeTracker/HostingExtensions.cs`, add to `AddTmTimeTrackerCore`'s `ConfigureServices`:

```csharp
            services.AddSingleton<SlackChannelRepository>();
            services.AddSingleton<JiraSiteRepository>();
```

Add a new builder method and call it from `Program.cs` wherever `AddJiraServices()` is called:

```csharp
    public static IHostBuilder AddSlackServices(this IHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(sp => new SlackCredentialRepository(
                sp.GetRequiredService<ISqliteConnectionFactory>(),
                sp.GetRequiredService<ITokenProtector>()));

            services.AddSingleton<ISlackTokenSource>(sp =>
                new RepositorySlackTokenSource(sp.GetRequiredService<SlackCredentialRepository>()));

            services.AddSingleton(sp => new SlackApiClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("slack-api"),
                sp.GetRequiredService<ISlackTokenSource>(),
                sp.GetRequiredService<ILogger<SlackApiClient>>()));

            services.AddSingleton<ISlackPoster>(sp => sp.GetRequiredService<SlackApiClient>());

            services.AddSingleton<IJiraSiteResolver>(sp => new JiraSiteResolver(
                sp.GetRequiredService<JiraSiteRepository>(),
                sp.GetRequiredService<OAuthCoordinator>(),
                () => sp.GetRequiredService<OAuthStateRepository>().Load()?.CloudId,
                sp.GetRequiredService<ILogger<JiraSiteResolver>>()));

            services.AddHostedService<ReviewSlackNotifier>();
        });
        return builder;
    }
```

And a tiny adapter beside `ISlackTokenSource`:

```csharp
public sealed class RepositorySlackTokenSource : ISlackTokenSource
{
    private readonly SlackCredentialRepository _repo;
    public RepositorySlackTokenSource(SlackCredentialRepository repo) => _repo = repo;
    public string? GetToken() => _repo.Get();
}
```

Place `RepositorySlackTokenSource` in `src/TmTimeTracker/Slack/RepositorySlackTokenSource.cs` so `Slack/` has no dependency direction surprises; it may reference `Data/` exactly as `RepositoryOAuthAppConfigSource` already does.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/TmTimeTracker/ tests/TmTimeTracker.Tests/Services/ReviewSlackNotifierTests.cs
git commit -m "feat(services): ReviewSlackNotifier posts review transitions to Slack"
```

---

### Task 11: Settings → Slack page

**Files:**
- Create: `src/TmTimeTracker/UI/SettingsPages/SlackPage.cs`
- Modify: `src/TmTimeTracker/UI/SettingsWindow.cs`

**Interfaces:**
- Consumes: `SlackCredentialRepository`, `SlackChannelRepository`, `SlackApiClient`, `JiraApiClient.ListProjectsAsync`, `TicketTimeRepository`, `ChannelSuggester`, `SlackVariables`, `MessageTemplateRenderer`.
- Produces: `SlackPage : UserControl` with a `public SlackPage(IServiceProvider sp, ILogger log)` constructor, matching `ConnectionPage`.

No unit tests: this is WinForms composition with no extractable logic left — every decision it makes was already unit-tested in Tasks 1-4. Verify manually per the steps below.

- [ ] **Step 1: Build the page**

Follow `ConnectionPage`'s conventions exactly: `Dock = DockStyle.Fill`, `BackColor = Theme.Background`, `FlatButton` for buttons, `Theme.Mono` / `Theme.Accent` / `Theme.TextSecondary` for text, `using TmTimeTracker.UI.Theming;`.

Layout, top to bottom:

1. **Heading** "Slack connection".
2. **Token row** — a `TextBox` with `UseSystemPasswordChar = true`, a `FlatButton` "Save & test", and a status `Label`.
   On click: save via `SlackCredentialRepository.Save`, then `await SlackApiClient.AuthTestAsync`. On success set the label to `$"Connected as @{identity.UserName} ({identity.TeamName})"` in `Theme.Accent`; on `SlackApiException` show `ex.SlackError` in red and leave the token saved so the user can correct it.
3. **Heading** "Project channels".
4. **Grid** — one row per project key, built by:
   - `TicketTimeRepository.GetAllOpen()` plus a `SELECT DISTINCT ticket_key FROM ticket_time` pass, mapped through `JiraProjectKey.From` and de-duplicated; union with `SlackChannelRepository.GetAll()` so already-configured projects always appear.
   - Each row: project label, a `ComboBox` of channels (`DropDownList`, populated from `SlackApiClient.ListConversationsAsync`, display `"#" + name`, tag `id`), a multiline `TextBox` for the template, and a `FlatButton` "Send test message".
   - Pre-select the channel with `ChannelSuggester.Suggest(projectName, channelNames)` where `projectName` comes from `JiraApiClient.ListProjectsAsync`. **Only pre-select — never save without an explicit user action.**
   - Pre-fill the template with `SlackVariables.DefaultTemplate` when the project has no saved row.
   - Persist on `ComboBox.SelectedIndexChanged` and `TextBox.Leave` via `SlackChannelRepository.Upsert`.
5. **Variables panel** — render from `SlackVariables.Catalog`: one line per variable, `{NAME}` in `Theme.Mono`/`Theme.Accent` followed by its `Description`. Never hard-code this list.
6. **Live preview** — a read-only `Label` under the template box, updated on every `TextChanged` with
   `MessageTemplateRenderer.Render(templateBox.Text, SlackVariables.Sample())`.

The "Send test message" button posts to the **real** channel, so per spec §6.4 it must render an obviously-marked test:

```csharp
var sample = new Dictionary<string, string>(SlackVariables.Sample(), StringComparer.Ordinal);
var body = MessageTemplateRenderer.Render(templateBox.Text, sample);
await slack.PostMessageAsync(channelId,
    $"\U0001F9EA Test message from TmTimeTracker — sample values, no action needed\n{body}",
    CancellationToken.None);
```

All Slack and Jira calls are `async`; disable the invoking button while in flight and re-enable in a `finally`, exactly as `ConnectionPage` does for its OAuth buttons. Never block the UI thread with `.Result` or `.Wait()`.

- [ ] **Step 2: Register the tab**

In `src/TmTimeTracker/UI/SettingsWindow.cs`, following the existing pattern:

```csharp
    private readonly FlatButton _tabSlack;
    private readonly Lazy<Control> _slackPage;
```

In the constructor:

```csharp
        _slackPage = new Lazy<Control>(() => new SlackPage(_sp, _log));
        _tabSlack = MakeTabButton("Slack");
        _tabSlack.Click += (_, _) => Activate(_tabSlack, _slackPage.Value);
```

And add `_tabSlack` to the `foreach` array that populates `tabBar`.

- [ ] **Step 3: Build and verify it compiles**

Run: `dotnet build TmTimeTracker.sln`
Expected: build succeeds with no new warnings.

- [ ] **Step 4: Manual smoke test**

Run: `dotnet run --project src/TmTimeTracker/TmTimeTracker.csproj`

Verify, and note results in `docs/smoke-tests.md`:
- Settings shows a "Slack" tab that opens without error when no token is set.
- Pasting a valid token and clicking "Save & test" shows `Connected as @…`.
- An invalid token shows the Slack error string, not an unhandled exception.
- The channel dropdown lists both public and private channels.
- Typing `{TIKCET}` in a template shows it literally in the preview; correcting it to `{TICKET}` shows `SN-296`.
- "Send test message" posts a message that is visibly marked as a test.

- [ ] **Step 5: Commit**

```bash
git add src/TmTimeTracker/UI/ docs/smoke-tests.md
git commit -m "feat(ui): Settings page for Slack token, per-project channels and templates"
```

---

### Task 12: Slack app manifest, README, and final verification

**Files:**
- Create: `docs/slack-app-manifest.yml`
- Modify: `README.md`

**Interfaces:**
- Consumes: nothing.
- Produces: nothing consumed by code. This is the user's entire setup surface, so it is a deliverable, not documentation garnish.

- [ ] **Step 1: Write the manifest**

`docs/slack-app-manifest.yml`:

```yaml
display_information:
  name: TmTimeTracker
  description: Posts a message when a tracked Jira ticket moves to Review
  background_color: "#2b3137"
oauth_config:
  scopes:
    user:
      - chat:write
      - channels:read
      - groups:read
settings:
  org_deploy_enabled: false
  socket_mode_enabled: false
  token_rotation_enabled: false
```

Only `user` scopes are declared — there is deliberately no bot user, because messages must be authored by the user and a user token already carries their private-channel membership. There is no `redirect_urls` entry because the app does not run an OAuth flow.

- [ ] **Step 2: Document the setup in the README**

Add a "Slack notifications" section to `README.md`:

```markdown
## Slack notifications

Posts a message to a per-project Slack channel when a tracked ticket moves to
Review. Setup is once, and takes about two minutes.

1. Go to https://api.slack.com/apps -> **Create New App** -> **From an app
   manifest**, pick your workspace, and paste the contents of
   [`docs/slack-app-manifest.yml`](docs/slack-app-manifest.yml).
2. Click **Install to Workspace** and approve.
3. On **OAuth & Permissions**, copy the **User OAuth Token** (starts with `xoxp-`).
4. In TmTimeTracker: **Settings -> Slack**, paste the token, click **Save & test**.
   You should see `Connected as @you`.
5. The project list fills itself from tickets you have tracked. Confirm the
   suggested channel for each project and adjust the message template if you want.

The message template accepts `{TICKET}`, `{PROJECT}`, `{SUMMARY}`, `{URL}`,
`{FROM}`, `{TO}`, `{MINUTES}`, `{HOURS}` and `{DATE}`. Unknown placeholders are
left as-is so typos are visible. A project with no channel selected simply never
notifies.

Messages are posted **as you**, not as a bot, so the app needs no invitation to
private channels you are already in.
```

- [ ] **Step 3: Run the full suite**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj`
Expected: all tests pass, including every pre-existing test.

- [ ] **Step 4: Confirm no network calls crept into tests**

Run: `grep -rn "https://slack.com\|api.atlassian.com" tests/ --include=*.cs | grep -v "/bin/\|/obj/"`
Expected: no results. Every HTTP test must point at a WireMock URL.

- [ ] **Step 5: Commit**

```bash
git add docs/slack-app-manifest.yml README.md
git commit -m "docs: Slack app manifest and setup walkthrough"
```

---

## Self-Review

**Spec coverage:**

| Spec section | Covered by |
| ------------ | ---------- |
| §1 Goal | Tasks 7, 10 |
| §3 Trigger, no dedupe | Task 7 (reuses the existing edge-triggered event) |
| §3 User token, scopes, no `as_user` | Tasks 8, 12 |
| §4.1 Data model (3 tables) | Task 5 |
| §5 Components | Tasks 1-11, one task per component |
| §6.1 Project-key derivation | Task 1 |
| §6.2 Template rendering + omission rule | Tasks 2, 3 |
| §6.3 Variable catalog | Task 3 |
| §6.4 Minimising setup, marked test message | Tasks 11, 12 |
| §6.5 Notify pipeline | Task 10 |
| §6.6 Site-URL resolution | Task 9 |
| §7 Error handling | Tasks 8 (`ok`, 429), 9 (returns null), 10 (never propagates) |
| §8 Testing | Every task's test step |
| §9 Acceptance criteria | Tasks 11 (manual), 12 (suite) |

No spec requirement is unimplemented.

**Placeholder scan:** No "TBD", "TODO", or "add error handling" steps. Every code step contains real code. Task 11 is prose-specified rather than fully coded because it is WinForms layout with no testable logic, but each element names the exact API it calls.

**Type consistency:** `JiraProjectKey.From` (Tasks 1, 3, 10, 11), `MessageTemplateRenderer.Render` (2, 3, 10, 11), `SlackVariables.Build/Sample/Catalog/DefaultTemplate` (3, 10, 11), `SlackChannelMapping` fields (5, 10, 11), `ISlackPoster.PostMessageAsync` (8, 10, 11), `IJiraSiteResolver.GetSiteUrlAsync` (9, 10) — all used with identical names and signatures throughout.

**Known risks flagged inline:** Task 7 notes the possible need for an `IJiraIssueSource` seam; Task 8 notes `HttpContent` reuse on retry and WireMock callback-API drift. Both have a stated fallback.
