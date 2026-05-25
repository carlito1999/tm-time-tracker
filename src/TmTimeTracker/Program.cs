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

AppPaths.EnsureExists();

if (args.Length >= 1 && args[0] == "--login")     { await RunCli(b => b, RunLogin); return; }
if (args.Length == 2 && args[0] == "--probe-jira"){ await RunCli(b => b, h => RunProbe(h, args[1])); return; }
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
