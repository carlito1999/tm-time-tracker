using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker;
using TmTimeTracker.Data;
using TmTimeTracker.Services;

if (args.Length == 1 && args[0] == "--smoke-activity")
{
    await RunActivitySmoke();
    return;
}

Console.WriteLine("TmTimeTracker scaffold OK. Try --smoke-activity once Phase 2 is wired.");

static async Task RunActivitySmoke()
{
    var host = Host.CreateDefaultBuilder()
        .ConfigureLogging(b => b.ClearProviders().AddSimpleConsole())
        .AddTmTimeTrackerCore()
        .AddActivityServices()
        .Build();

    using var scope = host.Services.CreateScope();
    var init = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    init.EnsureCreated();
    var cfg = scope.ServiceProvider.GetRequiredService<ConfigRepository>();
    cfg.SetIfMissing(new AppConfig(
        IdleThresholdSeconds: 600,
        JiraPollIntervalSeconds: 90,
        RepoPath: Environment.CurrentDirectory,
        RememberPath: Path.Combine(Environment.CurrentDirectory, ".remember"),
        InProgressStatusName: "In Progress",
        TransitionToStatusName: "Review"));

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
