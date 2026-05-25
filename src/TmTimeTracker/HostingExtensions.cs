using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TmTimeTracker.Configuration;
using TmTimeTracker.Data;
using TmTimeTracker.Platform;
using TmTimeTracker.Services;

namespace TmTimeTracker;

public static class HostingExtensions
{
    public static IHostBuilder AddTmTimeTrackerCore(this IHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            AppPaths.EnsureExists();
            services.AddSingleton<ISqliteConnectionFactory>(
                new SqliteConnectionFactory($"Data Source={AppPaths.DatabasePath}"));
            services.AddSingleton<DatabaseInitializer>();
            services.AddSingleton<TicketTimeRepository>();
            services.AddSingleton<RememberEntryRepository>();
            services.AddSingleton<ConfigRepository>();
            services.AddSingleton<OAuthStateRepository>();
            services.AddSingleton<MinuteSampleRepository>();
            services.AddSingleton<IClock, SystemClock>();
            services.AddSingleton<IEventBus, EventBus>();
            services.AddSingleton<IIdleProbe, Win32IdleProbe>();
            services.AddSingleton<IGitBranchProbe, GitBranchProbe>();
        });
        return builder;
    }

    public static IHostBuilder AddActivityServices(this IHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddHostedService<IdleMonitor>();
            services.AddHostedService<BranchWatcher>();
            services.AddHostedService<RememberWatcher>();
            services.AddHostedService<TimeAggregator>();
        });
        return builder;
    }
}
