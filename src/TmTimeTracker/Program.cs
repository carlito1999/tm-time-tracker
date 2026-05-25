using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker;
using TmTimeTracker.Configuration;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;
using TmTimeTracker.Services;

var baseBuilder = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration(c => c.AddJsonFile("secrets.json", optional: true, reloadOnChange: false))
    .ConfigureServices((ctx, services) =>
    {
        var secrets = ctx.Configuration.Get<AppSecrets>() ?? new AppSecrets();
        services.AddSingleton(secrets);
    })
    .ConfigureLogging(b => b.ClearProviders().AddSimpleConsole())
    .AddTmTimeTrackerCore()
    .AddJiraServices();

if (args.Length >= 1 && args[0] == "--login")
{
    var host = baseBuilder.Build();
    InitDb(host);
    await RunLogin(host);
    return;
}

if (args.Length == 2 && args[0] == "--probe-jira")
{
    var host = baseBuilder.Build();
    InitDb(host);
    await RunProbe(host, args[1]);
    return;
}

if (args.Length == 1 && args[0] == "--smoke-activity")
{
    var host = baseBuilder.AddActivityServices().Build();
    InitDb(host); SeedConfigIfMissing(host);
    await RunStreaming(host);
    return;
}

if (args.Length == 1 && args[0] == "--smoke-poll")
{
    var host = baseBuilder.AddActivityServices().AddPollServices().Build();
    InitDb(host); SeedConfigIfMissing(host);
    await RunStreaming(host);
    return;
}

Console.WriteLine("Usage: --login | --probe-jira TM-NN | --smoke-activity | --smoke-poll");

static void InitDb(IHost host)
{
    using var scope = host.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().EnsureCreated();
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
