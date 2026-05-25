# TmTimeTracker UI — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a 3-page Setup Wizard and a live Dashboard window for TmTimeTracker per [docs/superpowers/specs/2026-05-25-tmtt-ui-design.md](../specs/2026-05-25-tmtt-ui-design.md). Move OAuth app credentials from `secrets.json` into a DPAPI-encrypted SQLite table so the published exe is fully portable.

**Architecture:** Both windows live on the existing daemon's WinForms UI thread (the one `TrayIconHost` already runs). A new `WindowsHost` background service owns singleton window references and marshals show requests via `WindowsFormsSynchronizationContext`. Dashboard subscribes to `IEventBus` for live updates; pending-worklog table refreshes on event + a 5s fallback timer. Setup wizard runs as a step-through on first run and as a `TabControl` when re-opened via "Settings…".

**Tech Stack:** No new NuGet packages. Uses existing `Microsoft.Data.Sqlite` + `Dapper`, WinForms (`UseWindowsForms=true`), DPAPI via existing `ITokenProtector`, and `Microsoft.Extensions.Hosting`.

---

## Phases at a Glance

| Phase | Theme | Output |
|-------|-------|--------|
| 1 | Schema + `OAuthAppConfigRepository` | Encrypted-cred storage with TDD coverage |
| 2 | `ConfigRepository.Update` + `IOAuthAppConfigSource` refactor | `JiraOAuthClient` reads creds from DB at call time |
| 3 | `SetupWindow` (3 pages) | Wizard form opens and saves data |
| 4 | `DashboardWindow` (read-only) | State strip + pending table + events log + status bar |
| 5 | Per-row Submit/Edit/Discard | Full row-action wiring |
| 6 | Tray rewire + first-run detection in `Program.cs` | Wizard auto-opens; dashboard menu works |
| 7 | Drop hardcoded paths + `secrets.json` optional/migrated | Exe is fully portable |

Each phase ends with `dotnet test` green and a commit. Final phase ends with a fresh `dotnet publish` to Desktop.

---

# Phase 1: OAuth app config schema + repository

### Task 1.1: Extend Schema.sql

**Files:** `src/TmTimeTracker/Data/Schema.sql`

- [ ] **Step 1: Append the new table**

Add after the existing `oauth_state` table:

```sql
CREATE TABLE IF NOT EXISTS oauth_app_config (
    id                  INTEGER PRIMARY KEY CHECK(id = 1),
    client_id_dpapi     BLOB NOT NULL,
    client_secret_dpapi BLOB NOT NULL,
    redirect_uri        TEXT NOT NULL
);
```

- [ ] **Step 2: Verify build**

Run: `dotnet build`
Expected: success.

---

### Task 1.2: OAuthAppConfigRepository (TDD)

**Files:**
- Create: `src/TmTimeTracker/Data/OAuthAppConfigRepository.cs`
- Create: `tests/TmTimeTracker.Tests/Data/OAuthAppConfigRepositoryTests.cs`

- [ ] **Step 1: Write failing tests**

Create the test file with these cases:
- `Save_round_trips_encrypted_credentials` — uses a fake `ITokenProtector` that base64-encodes; verifies plaintext returned by Load matches the saved input
- `Save_overwrites_existing_row` (call Save twice, second wins)
- `Load_returns_null_when_absent`

Use `SharedSqlite.NewInMemory()` + `DatabaseInitializer.EnsureCreated()` pattern (same as other repo tests).

- [ ] **Step 2: Implement**

```csharp
using Dapper;
using TmTimeTracker.Platform;

namespace TmTimeTracker.Data;

public sealed record OAuthAppConfig(string ClientId, string ClientSecret, string RedirectUri);

public sealed class OAuthAppConfigRepository
{
    private readonly ISqliteConnectionFactory _factory;
    private readonly ITokenProtector _protector;

    public OAuthAppConfigRepository(ISqliteConnectionFactory factory, ITokenProtector protector)
    {
        _factory = factory;
        _protector = protector;
    }

    public void Save(OAuthAppConfig c)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO oauth_app_config (id, client_id_dpapi, client_secret_dpapi, redirect_uri)
              VALUES (1, @cid, @sec, @ru)
              ON CONFLICT(id) DO UPDATE SET
                client_id_dpapi=excluded.client_id_dpapi,
                client_secret_dpapi=excluded.client_secret_dpapi,
                redirect_uri=excluded.redirect_uri",
            new
            {
                cid = _protector.Protect(c.ClientId),
                sec = _protector.Protect(c.ClientSecret),
                ru = c.RedirectUri
            });
    }

    public OAuthAppConfig? Load()
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<AppConfigRow>(
            "SELECT * FROM oauth_app_config WHERE id=1");
        if (row is null) return null;
        return new OAuthAppConfig(
            ClientId: _protector.Unprotect(row.client_id_dpapi),
            ClientSecret: _protector.Unprotect(row.client_secret_dpapi),
            RedirectUri: row.redirect_uri);
    }

    private sealed class AppConfigRow
    {
        public byte[] client_id_dpapi { get; set; } = Array.Empty<byte>();
        public byte[] client_secret_dpapi { get; set; } = Array.Empty<byte>();
        public string redirect_uri { get; set; } = "";
    }
}
```

- [ ] **Step 3: Verify tests pass**

Run: `dotnet test --filter "FullyQualifiedName~OAuthAppConfigRepositoryTests"`
Expected: 3 pass.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(data): OAuthAppConfigRepository with DPAPI-encrypted client creds"
```

---

# Phase 2: Config update + OAuth source refactor

### Task 2.1: ConfigRepository.Update method (TDD)

**Files:** modify `src/TmTimeTracker/Data/ConfigRepository.cs`; extend `tests/TmTimeTracker.Tests/Data/SmallRepositoriesTests.cs`

- [ ] **Step 1: Add test**

Add to `SmallRepositoriesTests`:

```csharp
[Fact]
public void Config_Update_overwrites_all_fields()
{
    var repo = new ConfigRepository(Setup());
    repo.SetIfMissing(new AppConfig(600, 90, "a", "b", "ip", "rev"));
    repo.Update(new AppConfig(900, 30, "x", "y", "z", "w"));
    var loaded = repo.Get();
    loaded.IdleThresholdSeconds.Should().Be(900);
    loaded.JiraPollIntervalSeconds.Should().Be(30);
    loaded.RepoPath.Should().Be("x");
    loaded.RememberPath.Should().Be("y");
    loaded.InProgressStatusName.Should().Be("z");
    loaded.TransitionToStatusName.Should().Be("w");
}

[Fact]
public void Config_Update_creates_row_if_absent()
{
    var repo = new ConfigRepository(Setup());
    repo.Update(new AppConfig(1, 2, "p", "q", "r", "s"));
    repo.Get().RepoPath.Should().Be("p");
}
```

- [ ] **Step 2: Implement**

Add to `ConfigRepository`:

```csharp
public void Update(AppConfig c)
{
    using var conn = _factory.Open();
    conn.Execute(
        @"INSERT INTO config
            (id, idle_threshold_seconds, jira_poll_interval_seconds,
             repo_path, remember_path, in_progress_status_name, transition_to_status_name)
          VALUES (1, @it, @pi, @rp, @rm, @ip, @tr)
          ON CONFLICT(id) DO UPDATE SET
            idle_threshold_seconds=excluded.idle_threshold_seconds,
            jira_poll_interval_seconds=excluded.jira_poll_interval_seconds,
            repo_path=excluded.repo_path,
            remember_path=excluded.remember_path,
            in_progress_status_name=excluded.in_progress_status_name,
            transition_to_status_name=excluded.transition_to_status_name",
        new
        {
            it = c.IdleThresholdSeconds,
            pi = c.JiraPollIntervalSeconds,
            rp = c.RepoPath,
            rm = c.RememberPath,
            ip = c.InProgressStatusName,
            tr = c.TransitionToStatusName
        });
}

public AppConfig? TryGet()
{
    using var conn = _factory.Open();
    var row = conn.QueryFirstOrDefault<ConfigRow>("SELECT * FROM config WHERE id=1");
    return row is null ? null : new AppConfig(
        row.idle_threshold_seconds, row.jira_poll_interval_seconds,
        row.repo_path, row.remember_path,
        row.in_progress_status_name, row.transition_to_status_name);
}
```

(`TryGet` is added so the first-run flow can check for presence without throwing.)

- [ ] **Step 3: Verify tests**

Run: `dotnet test --filter "FullyQualifiedName~SmallRepositoriesTests"`
Expected: 7 pass (5 existing + 2 new).

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(data): ConfigRepository.Update for editable settings UI"
```

---

### Task 2.2: IOAuthAppConfigSource indirection

**Files:**
- Create: `src/TmTimeTracker/Jira/IOAuthAppConfigSource.cs`
- Modify: `src/TmTimeTracker/Jira/JiraOAuthClient.cs`
- Modify: `src/TmTimeTracker/HostingExtensions.cs`
- Modify: `tests/TmTimeTracker.Tests/Jira/JiraOAuthClientTests.cs`

- [ ] **Step 1: Define the source interface and a DB-backed implementation**

Create `src/TmTimeTracker/Jira/IOAuthAppConfigSource.cs`:

```csharp
using TmTimeTracker.Data;

namespace TmTimeTracker.Jira;

public interface IOAuthAppConfigSource
{
    /// <summary>Returns current OAuth app config, or throws if not yet set up.</summary>
    OAuthAppConfig Get();

    bool IsConfigured { get; }
}

public sealed class RepositoryOAuthAppConfigSource : IOAuthAppConfigSource
{
    private readonly OAuthAppConfigRepository _repo;
    public RepositoryOAuthAppConfigSource(OAuthAppConfigRepository repo) => _repo = repo;

    public bool IsConfigured => _repo.Load() is not null;

    public OAuthAppConfig Get() =>
        _repo.Load() ?? throw new InvalidOperationException(
            "OAuth app credentials not configured. Open Settings to add Client ID and Secret.");
}
```

- [ ] **Step 2: Refactor JiraOAuthClient to take IOAuthAppConfigSource**

Replace the constructor and update internal calls in `JiraOAuthClient.cs`:

```csharp
public sealed class JiraOAuthClient
{
    private const string DefaultOAuthBase = "https://auth.atlassian.com";
    private const string AuthorizePath = "/authorize";
    private const string TokenPath = "/oauth/token";

    private readonly HttpClient _http;
    private readonly IOAuthAppConfigSource _source;
    private readonly string _oauthBase;

    public JiraOAuthClient(HttpClient http, IOAuthAppConfigSource source, string? oauthBaseOverride = null)
    {
        _http = http; _source = source;
        _oauthBase = (oauthBaseOverride ?? DefaultOAuthBase).TrimEnd('/');
    }

    public string BuildAuthorizationUrl(string state)
    {
        var c = _source.Get();
        var qs = new StringBuilder()
            .Append("audience=api.atlassian.com")
            .Append("&client_id=").Append(Uri.EscapeDataString(c.ClientId))
            .Append("&scope=").Append(Uri.EscapeDataString("read:jira-work write:jira-work offline_access"))
            .Append("&redirect_uri=").Append(Uri.EscapeDataString(c.RedirectUri))
            .Append("&state=").Append(Uri.EscapeDataString(state))
            .Append("&response_type=code&prompt=consent");
        return $"{_oauthBase}{AuthorizePath}?{qs}";
    }

    public async Task<TokenResponse> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        var c = _source.Get();
        var payload = new
        {
            grant_type = "authorization_code",
            client_id = c.ClientId,
            client_secret = c.ClientSecret,
            code,
            redirect_uri = c.RedirectUri
        };
        var resp = await _http.PostAsJsonAsync($"{_oauthBase}{TokenPath}", payload, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct).ConfigureAwait(false))!;
    }

    public async Task<TokenResponse> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var c = _source.Get();
        var payload = new
        {
            grant_type = "refresh_token",
            client_id = c.ClientId,
            client_secret = c.ClientSecret,
            refresh_token = refreshToken
        };
        var resp = await _http.PostAsJsonAsync($"{_oauthBase}{TokenPath}", payload, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct).ConfigureAwait(false))!;
    }
}
```

- [ ] **Step 3: Update tests**

In `JiraOAuthClientTests.cs`, replace the `AtlassianSecrets` constructor argument with an in-test `IOAuthAppConfigSource` whose `Get()` returns a stub `OAuthAppConfig` with `ClientId="client-id"`, `ClientSecret="client-secret"`, `RedirectUri="http://localhost:53682/callback"`. Use Moq.

- [ ] **Step 4: Update HostingExtensions**

In `AddJiraServices`, replace the `JiraOAuthClient` registration:

```csharp
services.AddSingleton<IOAuthAppConfigSource, RepositoryOAuthAppConfigSource>();
services.AddSingleton(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("jira-oauth");
    var src = sp.GetRequiredService<IOAuthAppConfigSource>();
    return new JiraOAuthClient(http, src);
});
```

Remove the `AppSecrets` dependency from `JiraOAuthClient` construction (but keep `AppSecrets` registered for now — Phase 7 deletes it).

- [ ] **Step 5: Verify**

Run: `dotnet test` — all 61+ tests should pass.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor(jira): JiraOAuthClient reads creds via IOAuthAppConfigSource"
```

---

# Phase 3: Setup Wizard

### Task 3.1: SetupWindow shell + page 1 (OAuth app)

**Files:**
- Create: `src/TmTimeTracker/UI/SetupWindow.cs`
- Create: `src/TmTimeTracker/UI/SetupPages/OAuthAppPage.cs`

- [ ] **Step 1: Create the page UserControl**

Create `src/TmTimeTracker/UI/SetupPages/OAuthAppPage.cs`:

```csharp
using TmTimeTracker.Data;

namespace TmTimeTracker.UI.SetupPages;

public sealed class OAuthAppPage : UserControl
{
    private readonly TextBox _clientId;
    private readonly TextBox _clientSecret;
    private readonly TextBox _redirectUri;
    public event Action? ValidationChanged;

    public OAuthAppPage()
    {
        Dock = DockStyle.Fill;
        var lbl = new Label
        {
            Top = 10, Left = 10, Width = 540, Height = 60, AutoSize = false,
            Text = "Register an OAuth 2.0 (3LO) integration at developer.atlassian.com " +
                   "and paste its Client ID and Secret below. The values are encrypted " +
                   "with Windows DPAPI before being saved to state.db."
        };
        var openLink = new Button { Top = 75, Left = 10, Width = 260, Text = "Open developer.atlassian.com" };
        openLink.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://developer.atlassian.com/console/myapps/",
            UseShellExecute = true
        });

        Controls.Add(lbl);
        Controls.Add(openLink);

        Controls.Add(new Label { Top = 120, Left = 10, AutoSize = true, Text = "Client ID:" });
        _clientId = new TextBox { Top = 140, Left = 10, Width = 540 };
        _clientId.TextChanged += (_, _) => ValidationChanged?.Invoke();
        Controls.Add(_clientId);

        Controls.Add(new Label { Top = 175, Left = 10, AutoSize = true, Text = "Client Secret:" });
        _clientSecret = new TextBox { Top = 195, Left = 10, Width = 540, UseSystemPasswordChar = true };
        _clientSecret.TextChanged += (_, _) => ValidationChanged?.Invoke();
        Controls.Add(_clientSecret);

        Controls.Add(new Label { Top = 230, Left = 10, AutoSize = true, Text = "Redirect URI (configure this exact URL in your Atlassian app):" });
        _redirectUri = new TextBox { Top = 250, Left = 10, Width = 540, ReadOnly = true, Text = "http://localhost:53682/callback" };
        Controls.Add(_redirectUri);
    }

    public bool IsValid => _clientId.Text.Trim().Length > 0 && _clientSecret.Text.Length > 0;

    public OAuthAppConfig BuildConfig() => new(
        ClientId: _clientId.Text.Trim(),
        ClientSecret: _clientSecret.Text,
        RedirectUri: _redirectUri.Text.Trim());

    public void Load(OAuthAppConfig? c)
    {
        if (c is null) return;
        _clientId.Text = c.ClientId;
        _clientSecret.Text = c.ClientSecret;
        _redirectUri.Text = c.RedirectUri;
    }
}
```

- [ ] **Step 2: Create the SetupWindow shell**

Create `src/TmTimeTracker/UI/SetupWindow.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;
using TmTimeTracker.Services;
using TmTimeTracker.UI.SetupPages;

namespace TmTimeTracker.UI;

public sealed class SetupWindow : Form
{
    public enum Page { OAuthApp = 0, Connect = 1, Paths = 2 }

    public event Action? SetupCompleted;

    private readonly IServiceProvider _sp;
    private readonly ILogger<SetupWindow> _log;
    private readonly Panel _content;
    private readonly Button _back, _next, _cancel;

    private readonly OAuthAppPage _page1;
    private ConnectPage _page2 = null!;
    private PathsPage _page3 = null!;

    private Page _current;

    public SetupWindow(IServiceProvider sp, ILogger<SetupWindow> log, Page startPage = Page.OAuthApp)
    {
        _sp = sp; _log = log;
        Text = "TmTimeTracker — Setup";
        Width = 600; Height = 480;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false;

        _content = new Panel { Top = 0, Left = 0, Width = 580, Height = 400, Dock = DockStyle.Top };
        _back = new Button { Top = 405, Left = 280, Width = 90, Text = "Back" };
        _next = new Button { Top = 405, Left = 380, Width = 90, Text = "Next" };
        _cancel = new Button { Top = 405, Left = 480, Width = 90, Text = "Cancel" };
        _back.Click += (_, _) => GoTo((Page)((int)_current - 1));
        _next.Click += (_, _) => AdvanceAsync();
        _cancel.Click += (_, _) => Close();

        Controls.Add(_content);
        Controls.Add(_back);
        Controls.Add(_next);
        Controls.Add(_cancel);

        _page1 = new OAuthAppPage();
        _page1.ValidationChanged += UpdateButtons;

        var existing = sp.GetRequiredService<OAuthAppConfigRepository>().Load();
        _page1.Load(existing);

        GoTo(startPage);
    }

    private void GoTo(Page p)
    {
        if (p < Page.OAuthApp || p > Page.Paths) return;
        _current = p;
        _content.Controls.Clear();
        _content.Controls.Add(p switch
        {
            Page.OAuthApp => _page1,
            Page.Connect  => (_page2 ??= new ConnectPage(_sp, _log)),
            Page.Paths    => (_page3 ??= new PathsPage(_sp)),
            _ => throw new InvalidOperationException()
        });
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _back.Enabled = _current != Page.OAuthApp;
        _next.Text = _current == Page.Paths ? "Finish" : "Next";
        _next.Enabled = _current switch
        {
            Page.OAuthApp => _page1.IsValid,
            Page.Connect  => _page2?.IsConnected ?? false,
            Page.Paths    => _page3?.IsValid ?? false,
            _ => false
        };
    }

    private async void AdvanceAsync()
    {
        switch (_current)
        {
            case Page.OAuthApp:
                _sp.GetRequiredService<OAuthAppConfigRepository>().Save(_page1.BuildConfig());
                GoTo(Page.Connect);
                break;
            case Page.Connect:
                GoTo(Page.Paths);
                break;
            case Page.Paths:
                _sp.GetRequiredService<ConfigRepository>().Update(_page3.BuildConfig());
                SetupCompleted?.Invoke();
                Close();
                break;
        }
        await Task.CompletedTask;
    }
}
```

- [ ] **Step 3: Verify build (pages 2 and 3 will fail until 3.2 + 3.3)**

This step intentionally won't compile until ConnectPage and PathsPage exist; proceed to Task 3.2.

---

### Task 3.2: ConnectPage (page 2)

**Files:** `src/TmTimeTracker/UI/SetupPages/ConnectPage.cs`

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Jira;
using TmTimeTracker.Services;

namespace TmTimeTracker.UI.SetupPages;

public sealed class ConnectPage : UserControl
{
    private readonly IServiceProvider _sp;
    private readonly ILogger _log;
    private readonly Label _status;
    private readonly Button _signIn;
    private readonly Button _retry;
    public event Action? StateChanged;
    public bool IsConnected { get; private set; }

    public ConnectPage(IServiceProvider sp, ILogger log)
    {
        _sp = sp; _log = log;
        Dock = DockStyle.Fill;
        Controls.Add(new Label
        {
            Top = 10, Left = 10, Width = 560, Height = 60, AutoSize = false,
            Text = "Click Sign in to open your browser. Approve access on Atlassian, " +
                   "then return here. Tokens are stored encrypted in state.db."
        });
        _signIn = new Button { Top = 90, Left = 10, Width = 200, Text = "Sign in to Atlassian" };
        _signIn.Click += async (_, _) => await RunFlowAsync();
        Controls.Add(_signIn);

        _retry = new Button { Top = 90, Left = 220, Width = 100, Text = "Retry", Visible = false };
        _retry.Click += async (_, _) => await RunFlowAsync();
        Controls.Add(_retry);

        _status = new Label { Top = 140, Left = 10, Width = 560, Height = 200, AutoSize = false, Text = "Not connected." };
        Controls.Add(_status);

        if (_sp.GetRequiredService<OAuthCoordinator>().IsAuthenticated)
        {
            IsConnected = true;
            _status.Text = "Already connected. Click Next to continue.";
            _signIn.Text = "Sign in again";
        }
    }

    private async Task RunFlowAsync()
    {
        _signIn.Enabled = false;
        _retry.Visible = false;
        _status.Text = "Opening browser…";
        try
        {
            var oauth = _sp.GetRequiredService<JiraOAuthClient>();
            var listener = _sp.GetRequiredService<LocalCallbackListener>();
            var coord = _sp.GetRequiredService<OAuthCoordinator>();
            var src = _sp.GetRequiredService<IOAuthAppConfigSource>();

            var state = Guid.NewGuid().ToString("N");
            var url = oauth.BuildAuthorizationUrl(state);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });

            var redirect = new Uri(src.Get().RedirectUri);
            var prefix = $"{redirect.Scheme}://{redirect.Authority}/";
            _status.Text = "Waiting for browser callback…";
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var cb = await listener.ListenOnceAsync(prefix, cts.Token).ConfigureAwait(true);
            if (cb.State != state) throw new InvalidOperationException("OAuth state mismatch.");
            await coord.CompleteFirstRunAsync(cb.Code, cts.Token).ConfigureAwait(true);

            IsConnected = true;
            _status.Text = "Connected! Click Next to continue.";
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "OAuth sign-in failed");
            _status.Text = $"Sign-in failed: {ex.Message}";
            _retry.Visible = true;
        }
        finally
        {
            _signIn.Enabled = true;
        }
    }
}
```

Wire the `StateChanged` event in SetupWindow to call `UpdateButtons` (add `_page2.StateChanged += UpdateButtons;` after `_page2 ??= new ConnectPage(...)`).

---

### Task 3.3: PathsPage (page 3)

**Files:** `src/TmTimeTracker/UI/SetupPages/PathsPage.cs`

```csharp
using Microsoft.Extensions.DependencyInjection;
using TmTimeTracker.Data;

namespace TmTimeTracker.UI.SetupPages;

public sealed class PathsPage : UserControl
{
    private readonly TextBox _repoPath;
    private readonly TextBox _rememberPath;
    private readonly NumericUpDown _idleMin;
    private readonly NumericUpDown _pollSec;
    private readonly TextBox _inProgress;
    private readonly TextBox _transitionTo;
    private string _lastRepoDefault = "";
    public event Action? StateChanged;

    public PathsPage(IServiceProvider sp)
    {
        Dock = DockStyle.Fill;

        Controls.Add(new Label { Top = 10, Left = 10, AutoSize = true, Text = "Repo path:" });
        _repoPath = new TextBox { Top = 30, Left = 10, Width = 470 };
        var browseRepo = new Button { Top = 29, Left = 490, Width = 70, Text = "Browse…" };
        browseRepo.Click += (_, _) => BrowseInto(_repoPath);
        _repoPath.TextChanged += (_, _) =>
        {
            if (_rememberPath.Text == Path.Combine(_lastRepoDefault, ".remember") || _rememberPath.Text == "")
                _rememberPath.Text = Path.Combine(_repoPath.Text, ".remember");
            _lastRepoDefault = _repoPath.Text;
            StateChanged?.Invoke();
        };
        Controls.Add(_repoPath);
        Controls.Add(browseRepo);

        Controls.Add(new Label { Top = 65, Left = 10, AutoSize = true, Text = "Remember path:" });
        _rememberPath = new TextBox { Top = 85, Left = 10, Width = 470 };
        var browseRem = new Button { Top = 84, Left = 490, Width = 70, Text = "Browse…" };
        browseRem.Click += (_, _) => BrowseInto(_rememberPath);
        Controls.Add(_rememberPath);
        Controls.Add(browseRem);

        Controls.Add(new Label { Top = 120, Left = 10, AutoSize = true, Text = "Idle threshold (minutes):" });
        _idleMin = new NumericUpDown { Top = 138, Left = 10, Width = 80, Minimum = 1, Maximum = 120, Value = 10 };
        Controls.Add(_idleMin);

        Controls.Add(new Label { Top = 120, Left = 200, AutoSize = true, Text = "Jira poll interval (seconds):" });
        _pollSec = new NumericUpDown { Top = 138, Left = 200, Width = 80, Minimum = 30, Maximum = 600, Value = 90 };
        Controls.Add(_pollSec);

        Controls.Add(new Label { Top = 175, Left = 10, AutoSize = true, Text = "'In Progress' status name:" });
        _inProgress = new TextBox { Top = 195, Left = 10, Width = 260, Text = "In Progress" };
        Controls.Add(_inProgress);

        Controls.Add(new Label { Top = 175, Left = 290, AutoSize = true, Text = "Transition target status name:" });
        _transitionTo = new TextBox { Top = 195, Left = 290, Width = 270, Text = "Review" };
        Controls.Add(_transitionTo);

        var existing = sp.GetRequiredService<ConfigRepository>().TryGet();
        if (existing is not null)
        {
            _repoPath.Text = existing.RepoPath;
            _rememberPath.Text = existing.RememberPath;
            _idleMin.Value = Math.Clamp(existing.IdleThresholdSeconds / 60, 1, 120);
            _pollSec.Value = Math.Clamp(existing.JiraPollIntervalSeconds, 30, 600);
            _inProgress.Text = existing.InProgressStatusName;
            _transitionTo.Text = existing.TransitionToStatusName;
        }
        else
        {
            _repoPath.Text = @"c:\projects\training-manager";
            _rememberPath.Text = @"c:\projects\training-manager\.remember";
        }
        _lastRepoDefault = _repoPath.Text;
    }

    public bool IsValid =>
        Directory.Exists(_repoPath.Text) &&
        _inProgress.Text.Trim().Length > 0 &&
        _transitionTo.Text.Trim().Length > 0;

    public AppConfig BuildConfig() => new(
        IdleThresholdSeconds: (int)_idleMin.Value * 60,
        JiraPollIntervalSeconds: (int)_pollSec.Value,
        RepoPath: _repoPath.Text.Trim(),
        RememberPath: _rememberPath.Text.Trim(),
        InProgressStatusName: _inProgress.Text.Trim(),
        TransitionToStatusName: _transitionTo.Text.Trim());

    private static void BrowseInto(TextBox tb)
    {
        using var fbd = new FolderBrowserDialog();
        if (Directory.Exists(tb.Text)) fbd.SelectedPath = tb.Text;
        if (fbd.ShowDialog() == DialogResult.OK) tb.Text = fbd.SelectedPath;
    }
}
```

Wire `_page3.StateChanged += UpdateButtons;` in SetupWindow after construction.

- [ ] **Step: Verify build**

Run: `dotnet build` — should succeed.

- [ ] **Step: Commit**

```bash
git add -A
git commit -m "feat(ui): SetupWindow 3-page wizard for OAuth app, login, paths"
```

---

# Phase 4: Dashboard window (read-only)

### Task 4.1: DashboardWindow skeleton

**Files:** `src/TmTimeTracker/UI/DashboardWindow.cs`

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using TmTimeTracker.Services;

namespace TmTimeTracker.UI;

public sealed class DashboardWindow : Form
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<DashboardWindow> _log;
    private readonly Label _stateBadge, _branchLabel, _ticketLabel;
    private readonly DataGridView _pending;
    private readonly ListView _events;
    private readonly ComboBox _filter;
    private readonly StatusStrip _statusBar;
    private readonly ToolStripStatusLabel _authStatus, _lastPoll, _nextPoll;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    private readonly LinkedList<DomainEvent> _eventBuffer = new();
    private CancellationTokenSource? _subscriptionCts;

    public DashboardWindow(IServiceProvider sp, ILogger<DashboardWindow> log)
    {
        _sp = sp; _log = log;
        Text = "TmTimeTracker — Dashboard";
        Width = 800; Height = 600;
        StartPosition = FormStartPosition.CenterScreen;
        FormClosing += (_, e) => { e.Cancel = true; Hide(); _subscriptionCts?.Cancel(); };

        // Top strip
        var top = new Panel { Top = 0, Left = 0, Width = 800, Height = 40, Dock = DockStyle.Top };
        _stateBadge = new Label { Top = 10, Left = 10, AutoSize = true, Text = "● ACTIVE", ForeColor = Color.DarkGreen, Font = new Font("Segoe UI", 10, FontStyle.Bold) };
        _branchLabel = new Label { Top = 12, Left = 130, AutoSize = true, Text = "Branch: —" };
        _ticketLabel = new Label { Top = 12, Left = 400, AutoSize = true, Text = "Ticket: —" };
        top.Controls.AddRange(new Control[] { _stateBadge, _branchLabel, _ticketLabel });
        Controls.Add(top);

        // Pending table
        _pending = new DataGridView
        {
            Top = 40, Left = 0, Width = 800, Height = 220, Dock = DockStyle.Top,
            ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        _pending.Columns.Add("ticket", "Ticket");
        _pending.Columns.Add("minutes", "Minutes");
        _pending.Columns.Add("first_seen", "First seen");
        _pending.Columns.Add("last_polled", "Last Jira poll");
        Controls.Add(_pending);

        // Action buttons (Phase 5 will wire these)
        var actions = new Panel { Top = 260, Left = 0, Width = 800, Height = 36, Dock = DockStyle.Top };
        var submitNow = new Button { Top = 5, Left = 10, Width = 110, Text = "Submit now" };
        var editSubmit = new Button { Top = 5, Left = 130, Width = 130, Text = "Edit && submit" };
        var discard = new Button { Top = 5, Left = 270, Width = 90, Text = "Discard" };
        submitNow.Click += (_, _) => OnSubmitNow();
        editSubmit.Click += (_, _) => OnEditSubmit();
        discard.Click += (_, _) => OnDiscard();
        actions.Controls.AddRange(new Control[] { submitNow, editSubmit, discard });
        Controls.Add(actions);

        // Filter + Events
        var filterPanel = new Panel { Top = 296, Left = 0, Width = 800, Height = 28, Dock = DockStyle.Top };
        filterPanel.Controls.Add(new Label { Top = 5, Left = 10, AutoSize = true, Text = "Recent events  Filter:" });
        _filter = new ComboBox
        {
            Top = 2, Left = 160, Width = 130, DropDownStyle = ComboBoxStyle.DropDownList
        };
        _filter.Items.AddRange(new object[] { "All", "Activity", "Branch", "Jira", "Worklog" });
        _filter.SelectedIndex = 0;
        _filter.SelectedIndexChanged += (_, _) => RenderEvents();
        filterPanel.Controls.Add(_filter);
        Controls.Add(filterPanel);

        _events = new ListView
        {
            Top = 324, Left = 0, Width = 800, Height = 200, Dock = DockStyle.Top,
            View = View.Details, FullRowSelect = true
        };
        _events.Columns.Add("Time", 80);
        _events.Columns.Add("Kind", 90);
        _events.Columns.Add("Detail", 600);
        Controls.Add(_events);

        // Status bar
        _statusBar = new StatusStrip();
        _authStatus = new ToolStripStatusLabel("Auth: —");
        _lastPoll = new ToolStripStatusLabel("Last Jira poll: —");
        _nextPoll = new ToolStripStatusLabel("Next: —");
        _statusBar.Items.AddRange(new ToolStripItem[] { _authStatus, _lastPoll, _nextPoll });
        Controls.Add(_statusBar);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 5_000 };
        _refreshTimer.Tick += (_, _) => RefreshFromDb();
    }

    public void ShowAndSubscribe()
    {
        if (Visible) { Activate(); return; }
        Show();
        BringToFront();
        _subscriptionCts = new CancellationTokenSource();
        var bus = _sp.GetRequiredService<IEventBus>();
        var token = _subscriptionCts.Token;
        _ = Task.Run(async () =>
        {
            await foreach (var evt in bus.Subscribe(token).ConfigureAwait(false))
                BeginInvoke(() => OnEvent(evt));
        }, token);
        RefreshFromDb();
        _refreshTimer.Start();
    }

    private void OnEvent(DomainEvent evt)
    {
        _eventBuffer.AddFirst(evt);
        while (_eventBuffer.Count > 50) _eventBuffer.RemoveLast();
        RenderEvents();
        switch (evt)
        {
            case ActivityChanged a:
                _stateBadge.Text = a.State == UserActivityState.Active ? "● ACTIVE" : "● IDLE";
                _stateBadge.ForeColor = a.State == UserActivityState.Active ? Color.DarkGreen : Color.DarkGray;
                break;
            case BranchChanged b:
                _branchLabel.Text = $"Branch: {b.Branch ?? "(none)"}";
                _ticketLabel.Text = $"Ticket: {b.TicketKey ?? "(none)"}";
                RefreshFromDb();
                break;
        }
    }

    private void RenderEvents()
    {
        var filter = _filter.SelectedItem as string ?? "All";
        bool Match(DomainEvent e) => filter switch
        {
            "Activity" => e is ActivityChanged,
            "Branch"   => e is BranchChanged,
            "Jira"     => e is JiraStatusTransition,
            "Worklog"  => e is WorklogSubmitted,
            _ => true
        };
        _events.BeginUpdate();
        _events.Items.Clear();
        foreach (var e in _eventBuffer.Where(Match))
        {
            var item = new ListViewItem(new[]
            {
                e.AtUtc.ToLocalTime().ToString("HH:mm:ss"),
                e.GetType().Name,
                Describe(e)
            });
            _events.Items.Add(item);
        }
        _events.EndUpdate();
    }

    private static string Describe(DomainEvent e) => e switch
    {
        ActivityChanged a => a.State.ToString(),
        BranchChanged b   => $"{b.Branch ?? "(detached)"} → {b.TicketKey ?? "(no ticket)"}",
        JiraStatusTransition t => $"{t.TicketKey}: {t.FromStatus} → {t.ToStatus}",
        WorklogSubmitted w => $"{w.TicketKey}: posted {w.Minutes}m (id={w.WorklogId})",
        RememberEntriesObserved r => $"{r.Entries.Count} entries observed for {r.EntryDate}",
        AuthenticationRequired ar => ar.Reason,
        _ => e.ToString() ?? ""
    };

    private void RefreshFromDb()
    {
        using var scope = _sp.CreateScope();
        var tickets = scope.ServiceProvider.GetRequiredService<TicketTimeRepository>();
        var oauth = scope.ServiceProvider.GetRequiredService<OAuthStateRepository>();
        var open = tickets.GetAllOpen();
        _pending.Rows.Clear();
        foreach (var t in open)
        {
            _pending.Rows.Add(t.TicketKey, t.MinutesActive,
                t.CycleStarted.ToLocalTime().ToString("HH:mm"),
                t.LastPolled?.ToLocalTime().ToString("HH:mm:ss") ?? "—");
        }
        _authStatus.Text = oauth.Load() is not null ? "Auth: ✓" : "Auth: ✗ (open Settings)";
        var maxPoll = open.Select(o => o.LastPolled).Max();
        _lastPoll.Text = $"Last Jira poll: {(maxPoll?.ToLocalTime().ToString("HH:mm:ss") ?? "—")}";
        var cfg = scope.ServiceProvider.GetRequiredService<ConfigRepository>().TryGet();
        if (cfg is not null && maxPoll is not null)
        {
            var nextIn = (int)(maxPoll.Value.AddSeconds(cfg.JiraPollIntervalSeconds) - DateTime.UtcNow).TotalSeconds;
            _nextPoll.Text = nextIn > 0 ? $"Next: in {nextIn}s" : "Next: due";
        }
    }

    private string? CurrentTicket() =>
        _pending.SelectedRows.Count == 0 ? null : _pending.SelectedRows[0].Cells["ticket"].Value as string;

    private void OnSubmitNow() { /* Phase 5 */ }
    private void OnEditSubmit() { /* Phase 5 */ }
    private void OnDiscard() { /* Phase 5 */ }
}
```

- [ ] **Step: Verify build + commit**

Run: `dotnet build` — succeeds.

```bash
git add -A
git commit -m "feat(ui): DashboardWindow with state strip, pending table, events log, status bar"
```

---

# Phase 5: Per-row Submit/Edit/Discard

### Task 5.1: Wire the three actions in DashboardWindow

Replace the three Phase 4 stubs with:

```csharp
private void OnSubmitNow()
{
    var key = CurrentTicket();
    if (key is null) return;
    try { SubmitWithMinutes(key, observedMinutesOnly: true); }
    catch (Exception ex) { ShowError(ex); }
}

private void OnEditSubmit()
{
    var key = CurrentTicket();
    if (key is null) return;

    using var scope = _sp.CreateScope();
    var tickets = scope.ServiceProvider.GetRequiredService<TicketTimeRepository>();
    var entries = scope.ServiceProvider.GetRequiredService<RememberEntryRepository>();
    var cycle = tickets.GetAllOpen().FirstOrDefault(c => c.TicketKey == key);
    if (cycle is null) return;
    var unconsumed = entries.GetUnconsumedForTicket(key);
    var description = WorklogDescriptionBuilder.Build(unconsumed);
    using var form = new WorklogEditForm(key, cycle.MinutesActive, description);
    if (form.ShowDialog(this) != DialogResult.OK) return;
    try { Submit(cycle, form.SubmittedMinutes, form.Description, unconsumed); }
    catch (Exception ex) { ShowError(ex); }
}

private void OnDiscard()
{
    var key = CurrentTicket();
    if (key is null) return;
    using var scope = _sp.CreateScope();
    var tickets = scope.ServiceProvider.GetRequiredService<TicketTimeRepository>();
    var cycle = tickets.GetAllOpen().FirstOrDefault(c => c.TicketKey == key);
    if (cycle is null) return;

    var confirm = MessageBox.Show(this,
        $"Discard {cycle.MinutesActive} minute(s) on {key}? This cannot be undone.",
        "TmTimeTracker", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
    if (confirm != DialogResult.Yes) return;

    var clock = scope.ServiceProvider.GetRequiredService<IClock>();
    tickets.MarkSubmitted(cycle.Id, "discarded:" + Guid.NewGuid().ToString("N"), 0, clock.UtcNow);
    RefreshFromDb();
}

private void SubmitWithMinutes(string ticketKey, bool observedMinutesOnly)
{
    using var scope = _sp.CreateScope();
    var tickets = scope.ServiceProvider.GetRequiredService<TicketTimeRepository>();
    var entries = scope.ServiceProvider.GetRequiredService<RememberEntryRepository>();
    var cycle = tickets.GetAllOpen().FirstOrDefault(c => c.TicketKey == ticketKey);
    if (cycle is null) return;
    var unconsumed = entries.GetUnconsumedForTicket(ticketKey);
    var description = WorklogDescriptionBuilder.Build(unconsumed);
    Submit(cycle, cycle.MinutesActive, description, unconsumed);
}

private void Submit(TicketCycle cycle, int minutes, string description,
                    IReadOnlyList<StoredRememberEntry> unconsumed)
{
    using var scope = _sp.CreateScope();
    var api = scope.ServiceProvider.GetRequiredService<JiraApiClient>();
    var tickets = scope.ServiceProvider.GetRequiredService<TicketTimeRepository>();
    var entriesRepo = scope.ServiceProvider.GetRequiredService<RememberEntryRepository>();
    var clock = scope.ServiceProvider.GetRequiredService<IClock>();

    var req = TmTimeTracker.UI.WorklogRequestFactory.Build(minutes, description, clock.LocalNow);
    var resp = api.PostWorklogAsync(cycle.TicketKey, req, CancellationToken.None).GetAwaiter().GetResult();
    tickets.MarkSubmitted(cycle.Id, resp.Id, minutes, clock.UtcNow);
    entriesRepo.TagConsumed(unconsumed.Select(e => e.Id).ToList(), cycle.Id);
    RefreshFromDb();
}

private void ShowError(Exception ex)
{
    _log.LogError(ex, "Submit action failed");
    MessageBox.Show(this, $"Failed: {ex.Message}", "TmTimeTracker",
        MessageBoxButtons.OK, MessageBoxIcon.Error);
}
```

### Task 5.2: Extract WorklogRequestFactory (DRY with TrayIconHost)

`TrayIconHost.BuildAdfRequest` and dashboard need the same logic.

Create `src/TmTimeTracker/UI/WorklogRequestFactory.cs`:

```csharp
using TmTimeTracker.Jira;

namespace TmTimeTracker.UI;

internal static class WorklogRequestFactory
{
    public static WorklogRequest Build(int minutes, string description, DateTimeOffset localNow)
    {
        var content = new WorklogContent("paragraph",
            new[] { new WorklogTextNode("text", description) });
        var comment = new WorklogComment("doc", 1, new[] { content });
        var offset = localNow.Offset;
        var sign = offset.Ticks >= 0 ? "+" : "-";
        var started = $"{localNow:yyyy-MM-ddTHH:mm:ss.fff}{sign}{Math.Abs(offset.Hours):D2}{Math.Abs(offset.Minutes):D2}";
        return new WorklogRequest(TimeSpentSeconds: minutes * 60, StartedIso: started, Comment: comment);
    }
}
```

Replace the inline `BuildAdfRequest` call in `TrayIconHost` with `UI.WorklogRequestFactory.Build(...)`.

- [ ] **Verify + commit**

```bash
dotnet build && git add -A && git commit -m "feat(ui): wire dashboard row actions; extract WorklogRequestFactory"
```

---

# Phase 6: Tray rewire + first-run detection

### Task 6.1: WindowsHost (singleton window owner)

Create `src/TmTimeTracker/UI/WindowsHost.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TmTimeTracker.UI;

public sealed class WindowsHost
{
    private readonly IServiceProvider _sp;
    private readonly ILoggerFactory _logFactory;
    private SetupWindow? _setup;
    private DashboardWindow? _dashboard;
    private SynchronizationContext? _uiCtx;

    public WindowsHost(IServiceProvider sp, ILoggerFactory logFactory)
    {
        _sp = sp; _logFactory = logFactory;
    }

    public void RegisterUiContext(SynchronizationContext uiCtx) => _uiCtx = uiCtx;

    public event Action? SetupCompleted;

    public void ShowSetup(SetupWindow.Page startPage = SetupWindow.Page.OAuthApp)
    {
        Marshal(() =>
        {
            if (_setup is null || _setup.IsDisposed)
            {
                _setup = new SetupWindow(_sp, _logFactory.CreateLogger<SetupWindow>(), startPage);
                _setup.SetupCompleted += () => SetupCompleted?.Invoke();
                _setup.FormClosed += (_, _) => _setup = null;
            }
            _setup.Show();
            _setup.BringToFront();
            _setup.Activate();
        });
    }

    public void ShowDashboard()
    {
        Marshal(() =>
        {
            if (_dashboard is null || _dashboard.IsDisposed)
                _dashboard = new DashboardWindow(_sp, _logFactory.CreateLogger<DashboardWindow>());
            _dashboard.ShowAndSubscribe();
        });
    }

    private void Marshal(Action action)
    {
        if (_uiCtx is null) action();
        else _uiCtx.Post(_ => action(), null);
    }
}
```

### Task 6.2: Modify TrayIconHost to expose UI context + new menu items

In `TrayIconHost`:

- After creating `_uiCtx`, also call `_sp.GetRequiredService<WindowsHost>().RegisterUiContext(_uiCtx);`
- Replace the menu builder:

```csharp
private NotifyIcon BuildIcon()
{
    var host = _sp.GetRequiredService<WindowsHost>();
    var menu = new ContextMenuStrip();
    menu.Items.Add("Open dashboard…", null, (_, _) => host.ShowDashboard());
    menu.Items.Add("Settings…", null, (_, _) => host.ShowSetup());
    menu.Items.Add("Open log folder", null, (_, _) =>
        System.Diagnostics.Process.Start("explorer.exe", Configuration.AppPaths.LogsDir));
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add("Quit", null, (_, _) => _lifetime.StopApplication());

    return new NotifyIcon
    {
        Visible = true, Text = "TmTimeTracker",
        Icon = SystemIcons.Application, ContextMenuStrip = menu
    };
}
```

- Update `PromptForWorklog` to call `host.ShowDashboard()` then auto-select the row, OR keep the existing balloon → form flow. **Keep the existing flow** for backward compatibility — the dashboard is in addition to, not replacing, the balloon prompt.
- Delete `ShowPending()` (replaced by dashboard) and the inline ADF builder (now `UI.WorklogRequestFactory.Build`).

### Task 6.3: Conditional JiraPollService start

Add to `HostingExtensions.cs`:

```csharp
public sealed class PollServiceGate
{
    public bool Enabled { get; set; }
}
```

Register as singleton. Modify `JiraPollService.ExecuteAsync` to await `Enabled = true` before entering the timer loop:

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    var gate = _gate;  // injected
    while (!gate.Enabled && !stoppingToken.IsCancellationRequested)
        await Task.Delay(500, stoppingToken).ConfigureAwait(false);
    // (rest unchanged)
}
```

Add `PollServiceGate` to JiraPollService constructor.

### Task 6.4: First-run logic in Program.cs

Replace `RunDaemon()` body with:

```csharp
static async Task RunDaemon()
{
    using var guard = new SingleInstanceGuard();
    if (!guard.IsPrimary) return;

    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.File(
            Path.Combine(AppPaths.LogsDir, "daemon-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30)
        .CreateLogger();

    try
    {
        var host = BaseBuilder()
            .UseSerilog()
            .AddActivityServices()
            .AddPollServices()
            .AddTrayUI()
            .ConfigureServices(s =>
            {
                s.AddSingleton<WindowsHost>();
                s.AddSingleton<PollServiceGate>();
            })
            .Build();

        using (var scope = host.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().EnsureCreated();

        var sp = host.Services;
        var appConfig = sp.GetRequiredService<OAuthAppConfigRepository>().Load();
        var oauthState = sp.GetRequiredService<OAuthStateRepository>().Load();
        var cfg = sp.GetRequiredService<ConfigRepository>().TryGet();

        var setupNeeded = appConfig is null || oauthState is null || cfg is null;
        var windows = sp.GetRequiredService<WindowsHost>();
        var gate = sp.GetRequiredService<PollServiceGate>();

        windows.SetupCompleted += () =>
        {
            gate.Enabled = true;
            windows.ShowDashboard();
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path)) AutoStartRegistrar.Register(path);
        };

        if (setupNeeded)
        {
            var startPage = appConfig is null
                ? SetupWindow.Page.OAuthApp
                : oauthState is null
                    ? SetupWindow.Page.Connect
                    : SetupWindow.Page.Paths;
            // TrayIconHost will start, then we ask the UI thread (once ready) to show setup:
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);   // give TrayIconHost time to register its UI context
                windows.ShowSetup(startPage);
            });
        }
        else
        {
            gate.Enabled = true;
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path)) AutoStartRegistrar.Register(path);
        }

        await host.RunAsync();
    }
    catch (Exception ex)
    {
        Log.Fatal(ex, "TmTimeTracker crashed");
        throw;
    }
    finally
    {
        Log.CloseAndFlush();
    }
}
```

Drop the existing `SeedConfigIfMissing` call from `RunDaemon` (config comes from the wizard now). Keep `SeedConfigIfMissing` for the CLI smoke modes (they bypass the wizard).

- [ ] **Verify build + commit**

```bash
dotnet build
git add -A
git commit -m "feat(ui): WindowsHost, tray menu rewire, first-run wizard gate"
```

---

# Phase 7: Drop hardcoded paths + secrets.json migration

### Task 7.1: secrets.json fallback migration

In `Program.cs` `BaseBuilder()`, replace the `AddJsonFile("secrets.json"…)` call with a one-time migration block that runs only if `oauth_app_config` is empty. Pseudocode:

```csharp
.ConfigureServices((ctx, services) =>
{
    services.AddSingleton<AppSecrets>(new AppSecrets());  // kept as no-op singleton for any leftover refs
});
```

After `host.Build()` in `RunDaemon`, before checking setup:

```csharp
TryMigrateSecretsJson(sp);

static void TryMigrateSecretsJson(IServiceProvider sp)
{
    var repo = sp.GetRequiredService<OAuthAppConfigRepository>();
    if (repo.Load() is not null) return;
    var path = "secrets.json";
    if (!File.Exists(path)) return;
    try
    {
        using var stream = File.OpenRead(path);
        var doc = System.Text.Json.JsonDocument.Parse(stream);
        var atl = doc.RootElement.GetProperty("Atlassian");
        var id = atl.GetProperty("OAuthClientId").GetString() ?? "";
        var secret = atl.GetProperty("OAuthClientSecret").GetString() ?? "";
        var redirect = atl.TryGetProperty("RedirectUri", out var r)
            ? r.GetString() ?? "http://localhost:53682/callback"
            : "http://localhost:53682/callback";
        if (id == "REPLACE_ME" || string.IsNullOrEmpty(id)) return;
        repo.Save(new OAuthAppConfig(id, secret, redirect));
        File.Move(path, "secrets.json.migrated", overwrite: true);
    }
    catch { /* migration is best-effort; ignore */ }
}
```

(`AppSecrets` is still a registered no-op singleton so old test wiring keeps compiling; can be deleted after a future audit.)

### Task 7.2: Update smoke-tests.md

Replace the "Prerequisites" section with: "Launch the exe; the setup wizard auto-opens on first run. Atlassian OAuth registration is still a one-time external step but no `secrets.json` file is needed."

Append three new scenarios at the end:

```markdown
## 8. First-run wizard auto-opens
- [ ] Delete `%LOCALAPPDATA%\TmTimeTracker\state.db`.
- [ ] Launch the exe.
- [ ] Setup wizard opens on page 1 within ~1 second.
- [ ] Fill in Atlassian Client ID/Secret → Next → Sign in → Next → keep
      default paths → Finish.
- [ ] Dashboard opens automatically.

## 9. Dashboard reflects live state
- [ ] With daemon + dashboard open, switch git branches in your repo.
- [ ] Within 10 seconds: Branch and Ticket labels at top update.
- [ ] Recent events shows the BranchChanged event.

## 10. Submit Now posts a worklog
- [ ] In the dashboard, select a pending row with non-zero minutes.
- [ ] Click "Submit now".
- [ ] Verify the worklog appears in Jira within 5 seconds.
- [ ] Row disappears from the pending list.
```

- [ ] **Verify + commit**

```bash
dotnet build && dotnet test
git add -A
git commit -m "feat(app): secrets.json migration; first-run wizard makes config UI-driven"
```

---

# Final: Repack exe to Desktop

- [ ] **Step 1: Run full test suite**

```bash
dotnet test
```

Expected: all tests pass.

- [ ] **Step 2: Publish self-contained exe**

```bash
dotnet publish src/TmTimeTracker/TmTimeTracker.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish
```

- [ ] **Step 3: Copy to Desktop**

```powershell
Copy-Item publish\TmTimeTracker.exe (Join-Path ([Environment]::GetFolderPath('Desktop')) 'TmTimeTracker.exe') -Force
```

- [ ] **Step 4: Report**

Tell the user the exe is updated and that double-clicking it will now open the setup wizard automatically.

---

# Self-Review

Cross-checked vs spec sections 4–12:

| Spec section | Plan task |
|--------------|-----------|
| 4.1 Windows + WindowsHost | 6.1 |
| 4.2 Tray menu rewire | 6.2 |
| 5.1 New table | 1.1 |
| 5.2 ConfigRepository.Update | 2.1 |
| 5.3 Discard lifecycle | 5.1 (Discard action) |
| 6 component table | 1.2, 2.2, 3.x, 4.1, 6.1 |
| 7 wizard contracts | 3.1, 3.2, 3.3 |
| 8 dashboard contracts | 4.1, 5.1 |
| 9 first-run flow | 6.4 |
| 10 error handling | distributed across 3.2 (Connect error states), 5.1 (ShowError) |
| 11 testing strategy | unit tests in 1.2 and 2.1; manual scenarios in 7.2 |
| 12 acceptance criteria | covered by phase outputs |
| 13 secrets.json migration | 7.1 |
