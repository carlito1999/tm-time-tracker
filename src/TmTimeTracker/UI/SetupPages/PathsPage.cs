using Microsoft.Extensions.DependencyInjection;
using TmTimeTracker.Data;

namespace TmTimeTracker.UI.SetupPages;

public sealed class PathsPage : UserControl
{
    private readonly TrackedRepoRepository _repos;
    private readonly ListBox _repoList;
    private readonly NumericUpDown _idleMin;
    private readonly NumericUpDown _pollSec;
    private readonly TextBox _inProgress;
    private readonly TextBox _transitionTo;
    public event Action? StateChanged;

    public PathsPage(IServiceProvider sp)
    {
        _repos = sp.GetRequiredService<TrackedRepoRepository>();
        Dock = DockStyle.Fill;

        Controls.Add(new Label { Top = 10, Left = 10, AutoSize = true, Text = "Tracked repos:" });
        _repoList = new ListBox { Top = 30, Left = 10, Width = 470, Height = 110 };
        Controls.Add(_repoList);

        var add = new Button { Top = 30, Left = 490, Width = 70, Text = "Add…" };
        add.Click += (_, _) => AddRepo();
        Controls.Add(add);

        var remove = new Button { Top = 65, Left = 490, Width = 70, Text = "Remove" };
        remove.Click += (_, _) => RemoveSelected();
        Controls.Add(remove);

        Controls.Add(new Label { Top = 155, Left = 10, AutoSize = true, Text = "Idle threshold (minutes):" });
        _idleMin = new NumericUpDown { Top = 173, Left = 10, Width = 80, Minimum = 1, Maximum = 120, Value = 10 };
        Controls.Add(_idleMin);

        Controls.Add(new Label { Top = 155, Left = 200, AutoSize = true, Text = "Jira poll interval (seconds):" });
        _pollSec = new NumericUpDown { Top = 173, Left = 200, Width = 80, Minimum = 30, Maximum = 600, Value = 90 };
        Controls.Add(_pollSec);

        Controls.Add(new Label { Top = 210, Left = 10, AutoSize = true, Text = "'In Progress' status name:" });
        _inProgress = new TextBox { Top = 230, Left = 10, Width = 260, Text = "In Progress" };
        _inProgress.TextChanged += (_, _) => StateChanged?.Invoke();
        Controls.Add(_inProgress);

        Controls.Add(new Label { Top = 210, Left = 290, AutoSize = true, Text = "Transition target status:" });
        _transitionTo = new TextBox { Top = 230, Left = 290, Width = 270, Text = "Review" };
        _transitionTo.TextChanged += (_, _) => StateChanged?.Invoke();
        Controls.Add(_transitionTo);

        ReloadList();
        var existing = sp.GetRequiredService<ConfigRepository>().TryGet();
        if (existing is not null)
        {
            _idleMin.Value = Math.Clamp(existing.IdleThresholdSeconds / 60, 1, 120);
            _pollSec.Value = Math.Clamp(existing.JiraPollIntervalSeconds, 30, 600);
            _inProgress.Text = existing.InProgressStatusName;
            _transitionTo.Text = existing.TransitionToStatusName;
        }
    }

    public bool IsValid =>
        _repoList.Items.Count > 0 &&
        _inProgress.Text.Trim().Length > 0 &&
        _transitionTo.Text.Trim().Length > 0;

    public AppConfig BuildConfig() => new(
        IdleThresholdSeconds: (int)_idleMin.Value * 60,
        JiraPollIntervalSeconds: (int)_pollSec.Value,
        RepoPath: _repoList.Items.Count > 0 ? (string)_repoList.Items[0]! : "",
        RememberPath: "",
        InProgressStatusName: _inProgress.Text.Trim(),
        TransitionToStatusName: _transitionTo.Text.Trim());

    private void ReloadList()
    {
        _repoList.Items.Clear();
        foreach (var r in _repos.GetAll())
            _repoList.Items.Add(r.Path);
    }

    private void AddRepo()
    {
        using var fbd = new FolderBrowserDialog { Description = "Select a git repository folder" };
        if (fbd.ShowDialog() != DialogResult.OK) return;
        var path = fbd.SelectedPath;
        if (!Directory.Exists(Path.Combine(path, ".git")))
        {
            MessageBox.Show($"{path} does not contain a .git folder.", "TmTimeTracker",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _repos.Add(path);
        ReloadList();
        StateChanged?.Invoke();
    }

    private void RemoveSelected()
    {
        if (_repoList.SelectedItem is not string path) return;
        _repos.Remove(path);
        ReloadList();
        StateChanged?.Invoke();
    }
}
