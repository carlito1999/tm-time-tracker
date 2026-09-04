using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using TmTimeTracker;
using TmTimeTracker.Configuration;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;
using TmTimeTracker.Platform;
using TmTimeTracker.Services;
using TmTimeTracker.UI;

// Clicking a toast COM-activates this exe. RunDaemon's SingleInstanceGuard would stop the second
// daemon anyway, but exiting here means a toast click does no work at all rather than racing it.
//
// Guarded because this runs before AppPaths and before any logger exists: if the WinRT contract
// is missing or COM is in an odd state, a throw here would kill the daemon at startup with
// nowhere to report why. Degrading to "possibly two instances" beats "will not start".
try
{
    if (Microsoft.Toolkit.Uwp.Notifications.ToastNotificationManagerCompat.WasCurrentProcessToastActivated())
        return;
}
catch
{
    // Fall through to normal startup.
}

AppPaths.EnsureExists();

if (args.Length >= 1 && args[0] == "--login")     { await RunCli(b => b, RunLogin); return; }
if (args.Length == 2 && args[0] == "--probe-jira"){ await RunCli(b => b, h => RunProbe(h, args[1])); return; }
if (args.Length == 3 && args[0] == "--probe-pr")
    { await RunCli(b => b, h => RunPullRequestProbe(h, args[1], args[2])); return; }
if (args.Length == 3 && args[0] == "--set-jira-token")
    { await RunCli(b => b, h => SetJiraToken(h, args[1], args[2])); return; }
if (args.Length == 3 && args[0] == "--set-bitbucket-token")
    { await RunCli(b => b, h => SetBitbucketToken(h, args[1], args[2])); return; }
// The overdue warning cannot fire until the read:dev-info:jira scope is granted, so this is the
// only way to see a real toast come out of the published exe.
if (args.Length == 1 && args[0] == "--test-toast")
{
    // Deliberately NOT going through WindowsToastNotifier: its balloon fallback swallows the
    // failure, which is right in production and useless for verifying that toasts work at all.
    try
    {
        var xml = new Windows.Data.Xml.Dom.XmlDocument();
        xml.LoadXml(WindowsToastNotifier.BuildXml(
            "TmTimeTracker", "Test notification - this one is marked urgent.", urgent: true));
        Microsoft.Toolkit.Uwp.Notifications.ToastNotificationManagerCompat
            .CreateToastNotifier()
            .Show(new Windows.UI.Notifications.ToastNotification(xml));
        Console.WriteLine("Toast handed to Windows without error.");
        // Windows delivers it asynchronously; exiting immediately can cut it off.
        await Task.Delay(TimeSpan.FromSeconds(2));
        return;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Toast failed: {ex}");
        Environment.ExitCode = 1;
        return;
    }
}
if (args.Length == 1 && args[0] == "--smoke-activity")
    { await RunCli(b => b.AddActivityServices(), RunStreaming); return; }
if (args.Length == 1 && args[0] == "--smoke-poll")
    { await RunCli(b => b.AddActivityServices().AddPollGate().AddPollServices(),
                   h => { h.Services.GetRequiredService<PollServiceGate>().Enabled = true; return RunStreaming(h); }); return; }

await RunDaemon();

static IHostBuilder BaseBuilder() =>
    Host.CreateDefaultBuilder()
        .ConfigureAppConfiguration(c => c.AddJsonFile("secrets.json", optional: true, reloadOnChange: false))
        .ConfigureServices((ctx, services) =>
        {
            var secrets = ctx.Configuration.Get<AppSecrets>() ?? new AppSecrets();
            services.AddSingleton(secrets);
        })
        .ConfigureLogging(b => b.ClearProviders().AddSimpleConsole())
        .AddTmTimeTrackerCore()
        .AddJiraServices();

static async Task RunCli(Func<IHostBuilder, IHostBuilder> compose, Func<IHost, Task> body)
{
    var host = compose(BaseBuilder()).Build();
    using var scope = host.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().EnsureCreated();
    SeedConfigIfMissing(host);
    await body(host);
}

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
            .AddPollGate()
            .AddPollServices()
            .AddSlackServices()
            .AddClaudeServices()
            .AddTrayUI()
            .Build();

        var sp = host.Services;
        using (var scope = sp.CreateScope())
            scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().EnsureCreated();

        TryMigrateSecretsJson(sp);
        TryMigrateTrackedRepos(sp);

        var appConfig = sp.GetRequiredService<OAuthAppConfigRepository>().Load();
        var oauthState = sp.GetRequiredService<OAuthStateRepository>().Load();
        var cfg = sp.GetRequiredService<ConfigRepository>().TryGet();
        var hasTrackedRepo = sp.GetRequiredService<TrackedRepoRepository>().GetAll().Count > 0;

        var setupNeeded = appConfig is null || oauthState is null || cfg is null || !hasTrackedRepo;
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
            _ = Task.Run(async () =>
            {
                await Task.Delay(800);
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
        try
        {
            System.Windows.Forms.MessageBox.Show(
                $"TmTimeTracker failed to start.\n\n{ex.GetType().Name}: {ex.Message}\n\nSee log at {AppPaths.LogsDir}",
                "TmTimeTracker", System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
        }
        catch { /* if even MessageBox fails, give up quietly */ }
        throw;
    }
    finally
    {
        Log.CloseAndFlush();
    }
}

static void TryMigrateTrackedRepos(IServiceProvider sp)
{
    var trackedRepo = sp.GetRequiredService<TrackedRepoRepository>();
    if (trackedRepo.GetAll().Count > 0) return;

    var cfg = sp.GetRequiredService<ConfigRepository>().TryGet();
    if (cfg is null || string.IsNullOrWhiteSpace(cfg.RepoPath)) return;
    if (!Directory.Exists(cfg.RepoPath)) return;

    trackedRepo.Add(cfg.RepoPath);
}

static void TryMigrateSecretsJson(IServiceProvider sp)
{
    var repo = sp.GetRequiredService<OAuthAppConfigRepository>();
    if (repo.Load() is not null) return;
    const string path = "secrets.json";
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
        try { File.Move(path, "secrets.json.migrated", overwrite: true); } catch { /* best-effort */ }
    }
    catch { /* migration is best-effort; ignore */ }
}

// Diagnostic: the development-information endpoint is undocumented, so this reports what the
// app's OAuth token can actually reach - the api.atlassian.com gateway, the site host, or neither.
static async Task SetJiraToken(IHost host, string email, string token)
{
    host.Services.GetRequiredService<JiraApiTokenRepository>().Save(email, token);
    Console.WriteLine($"Stored Jira API token for {email} (DPAPI-encrypted).");
    await Task.CompletedTask;
}

static async Task SetBitbucketToken(IHost host, string email, string token)
{
    host.Services.GetRequiredService<BitbucketApiTokenRepository>().Save(email, token);
    Console.WriteLine($"Stored Bitbucket API token for {email} (DPAPI-encrypted).");
    await Task.CompletedTask;
}

// Exercises the same source the worker uses - Bitbucket first, Jira's dev-status only as the
// fallback - so a green probe means the worker will work too.
static async Task RunPullRequestProbe(IHost host, string issueId, string ticketKey)
{
    var credential = host.Services.GetRequiredService<JiraApiTokenRepository>().Get();
    if (credential is null)
    {
        Console.Error.WriteLine("No Jira API token stored. Set one with:");
        Console.Error.WriteLine("  TmTimeTracker.exe --set-jira-token <atlassian-email> <api-token>");
        Console.Error.WriteLine("Create one at https://id.atlassian.com/manage-profile/security/api-tokens");
        Environment.ExitCode = 1;
        return;
    }

    Console.WriteLine($"Using basic auth as {credential.Email}.");

    var snapshot = await host.Services.GetRequiredService<IPullRequestSource>()
        .GetSnapshotAsync(issueId, ticketKey, CancellationToken.None);

    var lastCommit = snapshot.LastCommitUtc?.ToString("u") ?? "(none)";
    Console.WriteLine($"last commit: {lastCommit}");
    if (snapshot.PullRequest is null)
    {
        Console.WriteLine("pull request: (none reported)");
        return;
    }

    Console.WriteLine($"pull request: {snapshot.PullRequest.Status} - {snapshot.PullRequest.Title}");
    Console.WriteLine($"         url: {snapshot.PullRequest.Url}");
}
static void SeedConfigIfMissing(IHost host)
{
    using var scope = host.Services.CreateScope();
    var cfg = scope.ServiceProvider.GetRequiredService<ConfigRepository>();
    cfg.SetIfMissing(new AppConfig(
        IdleThresholdSeconds: 600,
        JiraPollIntervalSeconds: 90,
        RepoPath: Environment.CurrentDirectory,
        RememberPath: Path.Combine(Environment.CurrentDirectory, ".remember"),
        InProgressStatusName: "In Progress",
        TransitionToStatusName: "Review"));
}

static async Task RunLogin(IHost host)
{
    var coord = host.Services.GetRequiredService<OAuthCoordinator>();
    var oauth = host.Services.GetRequiredService<JiraOAuthClient>();
    var listener = host.Services.GetRequiredService<LocalCallbackListener>();
    var src = host.Services.GetRequiredService<IOAuthAppConfigSource>();

    var state = Guid.NewGuid().ToString("N");
    var url = oauth.BuildAuthorizationUrl(state);
    Console.WriteLine("Opening browser to:");
    Console.WriteLine(url);
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = url,
        UseShellExecute = true
    });

    var redirect = new Uri(src.Get().RedirectUri);
    var listenerPrefix = $"{redirect.Scheme}://{redirect.Authority}/";
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    var cb = await listener.ListenOnceAsync(listenerPrefix, cts.Token);
    if (cb.State != state) { Console.WriteLine("State mismatch - aborting."); return; }
    await coord.CompleteFirstRunAsync(cb.Code, cts.Token);
    Console.WriteLine("Login complete. Tokens stored.");
}

static async Task RunProbe(IHost host, string ticketKey)
{
    var api = host.Services.GetRequiredService<JiraApiClient>();
    var issue = await api.GetIssueAsync(ticketKey, CancellationToken.None);
    Console.WriteLine($"{issue.Key}: {issue.Fields.Status.Name} ({issue.Fields.Status.Category.Key})");
}

static async Task RunStreaming(IHost host)
{
    var bus = host.Services.GetRequiredService<IEventBus>();
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    _ = Task.Run(async () =>
    {
        await foreach (var evt in bus.Subscribe(cts.Token))
            Console.WriteLine($"[{evt.AtUtc:HH:mm:ss}] {evt.GetType().Name}: {evt}");
    });
    await host.RunAsync(cts.Token);
}
