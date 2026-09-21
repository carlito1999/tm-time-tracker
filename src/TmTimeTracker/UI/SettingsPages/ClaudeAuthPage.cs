using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Platform;
using TmTimeTracker.UI.Theming;

namespace TmTimeTracker.UI.SettingsPages;

/// <summary>
/// The Claude half of the OAuth tab.
///
/// The token is optional, and saying so is the point of this page: with the field empty the
/// estimator inherits the Claude Code login already on this machine, which is what most people
/// want. A token exists for the headless case - the daemon starts from HKCU\Run, so an expired
/// interactive login would otherwise fail silently overnight with nowhere to see why.
///
/// Test runs a real, minimal session rather than just checking the executable exists. An
/// expired credential is exactly the failure this page is for, and only a real call reveals it.
/// </summary>
public sealed class ClaudeAuthPage : UserControl
{
    private readonly IServiceProvider _sp;
    private readonly ILogger _log;
    private readonly TextBox _token;
    private readonly Label _status;
    private readonly FlatButton _save;
    private readonly FlatButton _useMachineLogin;
    private readonly FlatButton _test;

    public ClaudeAuthPage(IServiceProvider sp, ILogger log)
    {
        _sp = sp;
        _log = log;
        Dock = DockStyle.Fill;
        BackColor = Theme.Background;

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(16, 8, 16, 8)
        };

        layout.Controls.Add(MakeHeading("Claude"));
        layout.Controls.Add(MakeHint(
            "Estimates run through the Claude Code CLI. By default they use the login already on "
            + "this machine, and there is nothing to fill in here."));
        layout.Controls.Add(MakeHint(
            "Add a token only if you want the daemon to have its own credential - it keeps "
            + "working when your interactive login expires. Run  claude setup-token  in a "
            + "terminal and paste the result. It is encrypted with Windows DPAPI before it is "
            + "saved."));

        _token = new TextBox
        {
            Width = 560,
            Font = Theme.Mono,
            BackColor = Theme.Surface,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            UseSystemPasswordChar = true,
            Margin = new Padding(0, 8, 0, 4)
        };
        layout.Controls.Add(_token);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 4)
        };
        _save = new FlatButton { Text = "Save token", Kind = ButtonKind.Accent, Width = 120 };
        _useMachineLogin = new FlatButton { Text = "Use machine login", Width = 160 };
        _test = new FlatButton { Text = "Test", Width = 90 };
        _save.Margin = _useMachineLogin.Margin = _test.Margin = new Padding(0, 0, 8, 0);
        buttons.Controls.Add(_save);
        buttons.Controls.Add(_useMachineLogin);
        buttons.Controls.Add(_test);
        layout.Controls.Add(buttons);

        _status = new Label
        {
            AutoSize = true,
            Font = Theme.Body,
            ForeColor = Theme.TextSecondary,
            Margin = new Padding(0, 4, 0, 8),
            MaximumSize = new Size(560, 0)
        };
        layout.Controls.Add(_status);

        _save.Click += (_, _) => Save();
        _useMachineLogin.Click += (_, _) => Clear();
        _test.Click += async (_, _) => await TestAsync();

        Controls.Add(layout);
        ShowCurrentCredential();
    }

    private ClaudeAuthRepository Auth => _sp.GetRequiredService<ClaudeAuthRepository>();

    private void ShowCurrentCredential()
    {
        try
        {
            var stored = Auth.Get();
            Report(stored is null
                ? "Using this machine's Claude Code login."
                : "Using a saved token rather than this machine's login.", ok: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read the stored Claude token");
            Report("Could not read the saved token. Estimates will use this machine's login.",
                ok: false);
        }
    }

    private void Save()
    {
        var token = _token.Text.Trim();
        if (token.Length == 0)
        {
            Report("Paste a token first, or press \"Use machine login\" to clear it.", ok: false);
            return;
        }

        try
        {
            Auth.Save(token);
            _token.Clear();
            Report("Token saved. Estimates will use it instead of this machine's login.", ok: true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Saving the Claude token failed");
            Report($"Could not save the token: {ex.Message}", ok: false);
        }
    }

    private void Clear()
    {
        try
        {
            Auth.Clear();
            _token.Clear();
            Report("Token removed. Estimates will use this machine's Claude Code login.", ok: true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Clearing the Claude token failed");
            Report($"Could not remove the token: {ex.Message}", ok: false);
        }
    }

    private async Task TestAsync()
    {
        SetBusy(true);
        Report("Running a short session to check the credential...", ok: true);

        try
        {
            var estimator = _sp.GetRequiredService<IClaudeEstimator>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var run = await estimator.CheckAsync(timeout.Token);

            if (run.ExitCode == 0)
            {
                Report(Auth.Get() is null
                    ? "Works. Estimates will use this machine's Claude Code login."
                    : "Works. Estimates will use the saved token.", ok: true);
            }
            else
            {
                // stderr carries the actionable part; stdout on a failed run is event noise.
                var detail = Summarise(run.Stderr);
                Report($"claude exited with code {run.ExitCode}. {detail}", ok: false);
            }
        }
        catch (ClaudeUnavailableException ex)
        {
            Report($"{ex.Message}", ok: false);
        }
        catch (OperationCanceledException)
        {
            Report("The check timed out after 2 minutes.", ok: false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Claude credential check failed");
            Report($"The check failed: {ex.Message}", ok: false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _save.Enabled = _useMachineLogin.Enabled = !busy;
        _test.Enabled = !busy;
        _test.Text = busy ? "Testing…" : "Test";
    }

    private static string Summarise(string stderr)
    {
        var line = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .LastOrDefault();
        if (string.IsNullOrEmpty(line)) return "Try running  claude  in a terminal to see why.";
        return line.Length > 200 ? line[..200] + "…" : line;
    }

    private void Report(string message, bool ok)
    {
        _status.Text = message;
        _status.ForeColor = ok ? Theme.TextSecondary : Theme.Danger;
    }

    private static Label MakeHeading(string text) => new()
    {
        Text = text,
        Font = Theme.Heading,
        ForeColor = Theme.TextPrimary,
        AutoSize = true,
        Margin = new Padding(0, 8, 0, 4)
    };

    private static Label MakeHint(string text) => new()
    {
        Text = text,
        Font = Theme.Body,
        ForeColor = Theme.TextSecondary,
        AutoSize = true,
        MaximumSize = new Size(560, 0),
        Margin = new Padding(0, 0, 0, 4)
    };
}
