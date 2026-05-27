using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.UI.SetupPages;

namespace TmTimeTracker.UI;

public sealed class SetupWindow : Form
{
    public enum Page { OAuthApp = 0, Connect = 1, Paths = 2 }

    public event Action? SetupCompleted;

    private readonly IServiceProvider _sp;
    private readonly ILogger<SetupWindow> _log;
    private readonly Panel _content;
    private readonly Button _back, _next, _cancel;

    private readonly OAuthAppPage _page1;
    private ConnectPage? _page2;
    private PathsPage? _page3;

    private Page _current;

    public SetupWindow(IServiceProvider sp, ILogger<SetupWindow> log, Page startPage = Page.OAuthApp)
    {
        _sp = sp; _log = log;
        Text = "TmTimeTracker — Setup";
        Icon = AppIcon.Load();
        Width = 600; Height = 480;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false;

        _content = new Panel { Top = 0, Left = 0, Width = 580, Height = 400, Dock = DockStyle.Top };
        _back = new Button { Top = 405, Left = 280, Width = 90, Text = "Back" };
        _next = new Button { Top = 405, Left = 380, Width = 90, Text = "Next" };
        _cancel = new Button { Top = 405, Left = 480, Width = 90, Text = "Cancel" };
        _back.Click += (_, _) => GoTo((Page)((int)_current - 1));
        _next.Click += (_, _) => AdvanceAsync();
        _cancel.Click += (_, _) => Close();

        Controls.Add(_content);
        Controls.Add(_back);
        Controls.Add(_next);
        Controls.Add(_cancel);

        _page1 = new OAuthAppPage();
        _page1.ValidationChanged += UpdateButtons;
        var existing = sp.GetRequiredService<OAuthAppConfigRepository>().Load();
        _page1.LoadConfig(existing);

        GoTo(startPage);
    }

    private void GoTo(Page p)
    {
        if (p < Page.OAuthApp || p > Page.Paths) return;
        _current = p;
        _content.Controls.Clear();
        UserControl ctrl;
        switch (p)
        {
            case Page.OAuthApp:
                ctrl = _page1;
                break;
            case Page.Connect:
                if (_page2 is null)
                {
                    _page2 = new ConnectPage(_sp, _log);
                    _page2.StateChanged += UpdateButtons;
                }
                ctrl = _page2;
                break;
            case Page.Paths:
                if (_page3 is null)
                {
                    _page3 = new PathsPage(_sp);
                    _page3.StateChanged += UpdateButtons;
                }
                ctrl = _page3;
                break;
            default: throw new InvalidOperationException();
        }
        _content.Controls.Add(ctrl);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _back.Enabled = _current != Page.OAuthApp;
        _next.Text = _current == Page.Paths ? "Finish" : "Next";
        _next.Enabled = _current switch
        {
            Page.OAuthApp => _page1.IsValid,
            Page.Connect  => _page2?.IsConnected ?? false,
            Page.Paths    => _page3?.IsValid ?? false,
            _ => false
        };
    }

    private void AdvanceAsync()
    {
        switch (_current)
        {
            case Page.OAuthApp:
                _sp.GetRequiredService<OAuthAppConfigRepository>().Save(_page1.BuildConfig());
                GoTo(Page.Connect);
                break;
            case Page.Connect:
                GoTo(Page.Paths);
                break;
            case Page.Paths:
                _sp.GetRequiredService<ConfigRepository>().Update(_page3!.BuildConfig());
                SetupCompleted?.Invoke();
                Close();
                break;
        }
    }
}
