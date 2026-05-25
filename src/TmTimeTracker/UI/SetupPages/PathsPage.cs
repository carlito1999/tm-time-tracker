using Microsoft.Extensions.DependencyInjection;
using TmTimeTracker.Data;

namespace TmTimeTracker.UI.SetupPages;

public sealed class PathsPage : UserControl
{
    private readonly TextBox _repoPath;
    private readonly TextBox _rememberPath;
    private readonly NumericUpDown _idleMin;
    private readonly NumericUpDown _pollSec;
    private readonly TextBox _inProgress;
    private readonly TextBox _transitionTo;
    private string _lastRepoDefault = "";
    public event Action? StateChanged;

    public PathsPage(IServiceProvider sp)
    {
        Dock = DockStyle.Fill;

        Controls.Add(new Label { Top = 10, Left = 10, AutoSize = true, Text = "Repo path:" });
        _repoPath = new TextBox { Top = 30, Left = 10, Width = 470 };
        var browseRepo = new Button { Top = 29, Left = 490, Width = 70, Text = "Browse…" };
        browseRepo.Click += (_, _) => BrowseInto(_repoPath);
        Controls.Add(_repoPath);
        Controls.Add(browseRepo);

        Controls.Add(new Label { Top = 65, Left = 10, AutoSize = true, Text = "Remember path:" });
        _rememberPath = new TextBox { Top = 85, Left = 10, Width = 470 };
        var browseRem = new Button { Top = 84, Left = 490, Width = 70, Text = "Browse…" };
        browseRem.Click += (_, _) => BrowseInto(_rememberPath);
        Controls.Add(_rememberPath);
        Controls.Add(browseRem);

        _repoPath.TextChanged += (_, _) =>
        {
            if (_rememberPath.Text == Path.Combine(_lastRepoDefault, ".remember") || _rememberPath.Text == "")
                _rememberPath.Text = Path.Combine(_repoPath.Text, ".remember");
            _lastRepoDefault = _repoPath.Text;
            StateChanged?.Invoke();
        };

        Controls.Add(new Label { Top = 120, Left = 10, AutoSize = true, Text = "Idle threshold (minutes):" });
        _idleMin = new NumericUpDown { Top = 138, Left = 10, Width = 80, Minimum = 1, Maximum = 120, Value = 10 };
        Controls.Add(_idleMin);

        Controls.Add(new Label { Top = 120, Left = 200, AutoSize = true, Text = "Jira poll interval (seconds):" });
        _pollSec = new NumericUpDown { Top = 138, Left = 200, Width = 80, Minimum = 30, Maximum = 600, Value = 90 };
        Controls.Add(_pollSec);

        Controls.Add(new Label { Top = 175, Left = 10, AutoSize = true, Text = "'In Progress' status name:" });
        _inProgress = new TextBox { Top = 195, Left = 10, Width = 260, Text = "In Progress" };
        _inProgress.TextChanged += (_, _) => StateChanged?.Invoke();
        Controls.Add(_inProgress);

        Controls.Add(new Label { Top = 175, Left = 290, AutoSize = true, Text = "Transition target status:" });
        _transitionTo = new TextBox { Top = 195, Left = 290, Width = 270, Text = "Review" };
        _transitionTo.TextChanged += (_, _) => StateChanged?.Invoke();
        Controls.Add(_transitionTo);

        var existing = sp.GetRequiredService<ConfigRepository>().TryGet();
        if (existing is not null)
        {
            _repoPath.Text = existing.RepoPath;
            _rememberPath.Text = existing.RememberPath;
            _idleMin.Value = Math.Clamp(existing.IdleThresholdSeconds / 60, 1, 120);
            _pollSec.Value = Math.Clamp(existing.JiraPollIntervalSeconds, 30, 600);
            _inProgress.Text = existing.InProgressStatusName;
            _transitionTo.Text = existing.TransitionToStatusName;
        }
        else
        {
            _repoPath.Text = @"c:\projects\training-manager";
            _rememberPath.Text = @"c:\projects\training-manager\.remember";
        }
        _lastRepoDefault = _repoPath.Text;
    }

    public bool IsValid =>
        Directory.Exists(_repoPath.Text) &&
        _inProgress.Text.Trim().Length > 0 &&
        _transitionTo.Text.Trim().Length > 0;

    public AppConfig BuildConfig() => new(
        IdleThresholdSeconds: (int)_idleMin.Value * 60,
        JiraPollIntervalSeconds: (int)_pollSec.Value,
        RepoPath: _repoPath.Text.Trim(),
        RememberPath: _rememberPath.Text.Trim(),
        InProgressStatusName: _inProgress.Text.Trim(),
        TransitionToStatusName: _transitionTo.Text.Trim());

    private static void BrowseInto(TextBox tb)
    {
        using var fbd = new FolderBrowserDialog();
        if (Directory.Exists(tb.Text)) fbd.SelectedPath = tb.Text;
        if (fbd.ShowDialog() == DialogResult.OK) tb.Text = fbd.SelectedPath;
    }
}
