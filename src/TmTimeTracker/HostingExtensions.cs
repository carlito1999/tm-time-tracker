using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Configuration;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;
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
            services.AddSingleton<OAuthAppConfigRepository>();
            services.AddSingleton<TrackedRepoRepository>();
            services.AddSingleton<MinuteSampleRepository>();
            services.AddSingleton<IClock, SystemClock>();
            services.AddSingleton<IEventBus, EventBus>();
            services.AddSingleton<IIdleProbe, Win32IdleProbe>();
            services.AddSingleton<IGitBranchProbe, GitBranchProbe>();
            services.AddSingleton<IForegroundWindowProbe, Win32ForegroundWindowProbe>();
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
            services.AddHostedService<MaintenanceService>();
        });
        return builder;
    }

    public static IHostBuilder AddTrayUI(this IHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<TmTimeTracker.UI.WindowsHost>();
            services.AddHostedService<TmTimeTracker.UI.TrayIconHost>();
        });
        return builder;
    }

    public static IHostBuilder AddPollGate(this IHostBuilder builder)
    {
        builder.ConfigureServices(services => services.AddSingleton<PollServiceGate>());
        return builder;
    }

    public static IHostBuilder AddJiraServices(this IHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddHttpClient();
            services.AddSingleton<ITokenProtector, DpapiTokenProtector>();
            services.AddSingleton<IOAuthAppConfigSource, RepositoryOAuthAppConfigSource>();
            services.AddSingleton(sp =>
            {
                var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("jira-oauth");
                var src = sp.GetRequiredService<IOAuthAppConfigSource>();
                return new JiraOAuthClient(http, src);
            });
            services.AddSingleton(sp => new OAuthCoordinator(
                sp.GetRequiredService<OAuthStateRepository>(),
                sp.GetRequiredService<ITokenProtector>(),
                sp.GetRequiredService<JiraOAuthClient>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("jira-cloud"),
                sp.GetRequiredService<ILogger<OAuthCoordinator>>()));
            services.AddSingleton<IAccessTokenSource>(sp => sp.GetRequiredService<OAuthCoordinator>());
            services.AddSingleton(sp =>
            {
                var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("jira-api");
                var tokens = sp.GetRequiredService<IAccessTokenSource>();
                var log = sp.GetRequiredService<ILogger<JiraApiClient>>();
                return new JiraApiClient(http, tokens, log);
            });
            services.AddSingleton<LocalCallbackListener>();
        });
        return builder;
    }

    public static IHostBuilder AddPollServices(this IHostBuilder builder)
    {
        builder.ConfigureServices(services => services.AddHostedService<JiraPollService>());
        return builder;
    }
}
