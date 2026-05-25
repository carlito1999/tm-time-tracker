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

AppPaths.EnsureExists();

if (args.Length >= 1 && args[0] == "--login")    { await RunCli(b => b, RunLogin); return; }
if (args.Length == 2 && args[0] == "--probe-jira") { await RunCli(b => b, h => RunProbe(h, args[1])); return; }
if (args.Length == 1 && args[0] == "--smoke-activity")
    { await RunCli(b => b.AddActivityServices(), RunStreaming); return; }
if (args.Length == 1 && args[0] == "--smoke-poll")
    { await RunCli(b => b.AddActivityServices().AddPollServices(), RunStreaming); return; }

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
            .AddPollServices()
            .AddTrayUI()
            .Build();

        using (var scope = host.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().EnsureCreated();
            SeedConfigIfMissing(host);
        }

        AutoStartRegistrar.Register(Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location);
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

static void SeedConfigIfMissing(IHost host)
{
    using var scope = host.Services.CreateScope();
    var cfg = scope.ServiceProvider.GetRequiredService<ConfigRepository>();
    cfg.SetIfMissing(new AppConfig(
        IdleThresholdSeconds: 600,
        JiraPollIntervalSeconds: 90,
        RepoPath: @"c:\projects\training-manager",
        RememberPath: @"c:\projects\training-manager\.remember",
        InProgressStatusName: "In Progress",
        TransitionToStatusName: "Review"));
}

static async Task RunLogin(IHost host)
{
    var coord = host.Services.GetRequiredService<OAuthCoordinator>();
    var oauth = host.Services.GetRequiredService<JiraOAuthClient>();
    var listener = host.Services.GetRequiredService<LocalCallbackListener>();
    var secrets = host.Services.GetRequiredService<AppSecrets>();

    var state = Guid.NewGuid().ToString("N");
    var url = oauth.BuildAuthorizationUrl(state);
    Console.WriteLine("Opening browser to:");
    Console.WriteLine(url);
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = url,
        UseShellExecute = true
    });

    var redirect = new Uri(secrets.Atlassian.RedirectUri);
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
