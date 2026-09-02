using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;
using TmTimeTracker.Logic;
using TmTimeTracker.Services;

namespace TmTimeTracker.UI;

public sealed class TrayIconHost : BackgroundService
{
    private readonly IEventBus _bus;
    private readonly IServiceProvider _sp;
    private readonly ILogger<TrayIconHost> _log;
    private readonly IHostApplicationLifetime _lifetime;
    private NotifyIcon? _icon;
    private SynchronizationContext? _uiCtx;

    public TrayIconHost(IEventBus bus, IServiceProvider sp, ILogger<TrayIconHost> log,
        IHostApplicationLifetime lifetime)
    {
        _bus = bus; _sp = sp; _log = log; _lifetime = lifetime;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            _uiCtx = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(_uiCtx);

            _sp.GetRequiredService<WindowsHost>().RegisterUiContext(_uiCtx);

            _icon = BuildIcon();
            ready.Set();
            Application.ApplicationExit += (_, _) => _icon.Dispose();
            Application.Run();
        });
        thread.IsBackground = false;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(stoppingToken);

        _ = Task.Run(async () =>
        {
            await foreach (var evt in _bus.Subscribe(stoppingToken).ConfigureAwait(false))
            {
                if (evt is JiraStatusTransition t)
                    OnUiThread(() => PromptForWorklog(t.TicketKey));
            }
        }, stoppingToken);

        stoppingToken.Register(() => OnUiThread(Application.Exit));
        return Task.CompletedTask;
    }

    private void OnUiThread(Action action)
    {
        if (_uiCtx is null) action();
        else _uiCtx.Post(_ => action(), null);
    }

    private NotifyIcon BuildIcon()
    {
        var host = _sp.GetRequiredService<WindowsHost>();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open dashboard…", null, (_, _) => host.ShowDashboard());
        menu.Items.Add("Settings…", null, (_, _) => host.ShowSettings());
        menu.Items.Add("Open log folder", null, (_, _) =>
            System.Diagnostics.Process.Start("explorer.exe", Configuration.AppPaths.LogsDir));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => _lifetime.StopApplication());

        var icon = new NotifyIcon
        {
            Visible = true,
            Text = "TmTimeTracker",
            Icon = AppIcon.Load(SystemInformation.SmallIconSize),
            ContextMenuStrip = menu
        };

        // Double-click is the Windows convention for opening a tray app's primary window,
        // and saves right-clicking through the menu for the thing opened most often.
        icon.DoubleClick += (_, _) => host.ShowDashboard();

        return icon;
    }

    private void PromptForWorklog(string ticketKey)
    {
        using var scope = _sp.CreateScope();
        var tickets = scope.ServiceProvider.GetRequiredService<TicketTimeRepository>();
        var entries = scope.ServiceProvider.GetRequiredService<RememberEntryRepository>();
        var api = scope.ServiceProvider.GetRequiredService<JiraApiClient>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var cycle = tickets.GetAllOpen().FirstOrDefault(c => c.TicketKey == ticketKey);
        if (cycle is null) return;

        var unconsumed = entries.GetUnconsumedForTicket(ticketKey);
        var description = WorklogDescriptionBuilder.Build(unconsumed);

        using var form = new WorklogEditForm(ticketKey, cycle.MinutesActive, description);
        if (form.ShowDialog() != DialogResult.OK) return;

        try
        {
            var req = WorklogRequestFactory.Build(form.SubmittedMinutes, form.Description, clock.LocalNow);
            var resp = api.PostWorklogAsync(ticketKey, req, CancellationToken.None)
                          .GetAwaiter().GetResult();
            tickets.MarkSubmitted(cycle.Id, resp.Id, form.SubmittedMinutes, clock.UtcNow);
            entries.TagConsumed(unconsumed.Select(e => e.Id).ToList(), cycle.Id);
            _log.LogInformation("Worklog {Id} posted to {Ticket}", resp.Id, ticketKey);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Worklog submit failed for {Ticket}", ticketKey);
            MessageBox.Show($"Submit failed: {ex.Message}", "TmTimeTracker",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
