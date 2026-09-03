using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Configuration;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;
using TmTimeTracker.Platform;
using TmTimeTracker.Services;
using TmTimeTracker.Slack;

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
            services.AddSingleton<SlackChannelRepository>();
            services.AddSingleton<JiraSiteRepository>();
            services.AddSingleton<PrAnnouncementRepository>();
            services.AddSingleton<JiraApiTokenRepository>();
            services.AddSingleton<BitbucketApiTokenRepository>();
            // The balloon is only the fallback now, live once the tray icon attaches; a no-op in
            // the headless CLI modes. Toasts work in every mode, tray icon or not.
            services.AddSingleton<TmTimeTracker.UI.TrayBalloonNotifier>();
            services.AddSingleton<TmTimeTracker.UI.IUserNotifier,
                                  TmTimeTracker.UI.WindowsToastNotifier>();
            services.AddSingleton<MinuteSampleRepository>();
            services.AddSingleton<IClock, SystemClock>();
            services.AddSingleton<IEventBus, EventBus>();
            services.AddSingleton<IIdleProbe, Win32IdleProbe>();
            services.AddSingleton<IGitBranchProbe, GitBranchProbe>();
            services.AddSingleton<IForegroundWindowProbe, Win32ForegroundWindowProbe>();
            services.AddSingleton<IClaudeCodeActivityProbe, FileClaudeCodeActivityProbe>();
            services.AddSingleton<IClaudeSessionProbe, FileClaudeSessionProbe>();
            services.AddSingleton<IProcessLiveness, Win32ProcessLiveness>();
            services.AddSingleton<ActiveRepoResolver>();
            services.AddSingleton<RepoActivityMonitor>();
            services.AddSingleton<IRepoActivitySource>(
                sp => sp.GetRequiredService<RepoActivityMonitor>());
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
            services.AddSingleton<IJiraIssueSource>(sp => sp.GetRequiredService<JiraApiClient>());
            services.AddSingleton<IJiraSearchSource>(sp => sp.GetRequiredService<JiraApiClient>());
            services.AddSingleton<IJiraEstimateWriter>(sp => sp.GetRequiredService<JiraApiClient>());
            services.AddSingleton<IJiraProjectSource>(sp => sp.GetRequiredService<JiraApiClient>());
            services.AddSingleton<IJiraAttachmentSource>(sp => sp.GetRequiredService<JiraApiClient>());
            services.AddSingleton<IAccessibleSiteSource>(sp => sp.GetRequiredService<OAuthCoordinator>());
            services.AddSingleton<IJiraSiteResolver>(sp => new JiraSiteResolver(
                sp.GetRequiredService<JiraSiteRepository>(),
                sp.GetRequiredService<IAccessibleSiteSource>(),
                () => sp.GetRequiredService<OAuthStateRepository>().Load()?.CloudId,
                sp.GetRequiredService<ILogger<JiraSiteResolver>>()));

            // dev-status rejects OAuth tokens whatever their scopes, so it gets its own client on
            // basic auth rather than sharing JiraApiClient's credential.
            services.AddSingleton<IDevStatusSource>(sp => new BasicAuthDevStatusClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("jira-api"),
                sp.GetRequiredService<JiraApiTokenRepository>(),
                sp.GetRequiredService<IJiraSiteResolver>(),
                sp.GetRequiredService<ILogger<BasicAuthDevStatusClient>>()));
            services.AddSingleton<IPullRequestSource>(sp => new JiraPullRequestSource(
                sp.GetRequiredService<IDevStatusSource>(),
                sp.GetRequiredService<ILogger<JiraPullRequestSource>>()));
            services.AddSingleton<LocalCallbackListener>();
        });
        return builder;
    }

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

            services.AddHostedService<PrAnnouncementWorker>();
        });
        return builder;
    }

    /// <summary>
    /// Claude-powered estimation of To-Do tickets. Registered separately from the poll services
    /// because it is the only feature that spawns a child process and spends money, so it can be
    /// left out of the CLI modes.
    /// </summary>
    public static IHostBuilder AddClaudeServices(this IHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<ClaudeAuthRepository>();
            services.AddSingleton<RepoProjectRepository>();
            services.AddSingleton<RepoBranchRepository>();
            services.AddSingleton<TicketEstimateRepository>();
            services.AddSingleton(new ClaudeEstimatorOptions());
            services.AddSingleton<IClaudeEstimator, ClaudeCliEstimator>();
            services.AddSingleton<IGitWorktreeManager>(sp =>
                new GitWorktreeManager(sp.GetRequiredService<ILogger<GitWorktreeManager>>()));
            services.AddSingleton<TicketAttachmentFetcher>();
            services.AddHostedService<TicketEstimationWorker>();
        });
        return builder;
    }

    public static IHostBuilder AddPollServices(this IHostBuilder builder)
    {
        builder.ConfigureServices(services => services.AddHostedService<JiraPollService>());
        return builder;
    }
}
