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

        _status = new Label
        {
            Top = 140, Left = 10, Width = 560, Height = 200, AutoSize = false,
            Text = "Not connected."
        };
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
