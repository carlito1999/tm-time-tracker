using Microsoft.Extensions.DependencyInjection;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;

namespace TmTimeTracker.UI.SetupPages;

public sealed class PathsPage : UserControl
{
    private readonly IServiceProvider _sp;
    private readonly TrackedRepoRepository _repos;
    private readonly RepoProjectRepository _mappings;
    private readonly RepoEstimationRepository _switches;
    private readonly ListBox _repoList;
    private readonly ComboBox _projectPicker;
    private readonly Button _assign;
    private readonly CheckBox _estimate;
    private readonly NumericUpDown _idleMin;
    private readonly NumericUpDown _pollSec;
    private readonly TextBox _inProgress;
    private readonly TextBox _transitionTo;
    public event Action? StateChanged;

    public PathsPage(IServiceProvider sp)
    {
        _sp = sp;
        _repos = sp.GetRequiredService<TrackedRepoRepository>();
        _mappings = sp.GetRequiredService<RepoProjectRepository>();
        _switches = sp.GetRequiredService<RepoEstimationRepository>();
        Dock = DockStyle.Fill;

        Controls.Add(new Label { Top = 10, Left = 10, AutoSize = true, Text = "Tracked repos:" });
        _repoList = new ListBox { Top = 30, Left = 10, Width = 470, Height = 110 };
        _repoList.SelectedIndexChanged += (_, _) =>
        {
            SyncPickerToSelection();
            SyncEstimateToSelection();
        };
        Controls.Add(_repoList);

        var add = new Button { Top = 30, Left = 490, Width = 70, Text = "Add…" };
        add.Click += (_, _) => AddRepo();
        Controls.Add(add);

        var remove = new Button { Top = 65, Left = 490, Width = 70, Text = "Remove" };
        remove.Click += (_, _) => RemoveSelected();
        Controls.Add(remove);

        // Estimation needs to know which Jira board covers each repo. The name match handles
        // most of them; this is for the ones it cannot resolve, like a "payload-site" folder
        // whose board is called "New site".
        Controls.Add(new Label
        {
            Top = 145, Left = 10, AutoSize = true, Text = "Jira project for the selected repo:"
        });
        _projectPicker = new ComboBox
        {
            Top = 163, Left = 10, Width = 260, DropDownStyle = ComboBoxStyle.DropDownList
        };
        _projectPicker.Items.Add(UnmappedOption);
        Controls.Add(_projectPicker);

        _assign = new Button { Top = 162, Left = 280, Width = 180, Text = "Assign to selected repo" };
        _assign.Click += (_, _) => AssignProject();
        Controls.Add(_assign);

        // Click rather than CheckedChanged: syncing the box to a newly selected repo sets
        // Checked from code, and that must not be written back as though the user toggled it.
        _estimate = new CheckBox
        {
            Top = 192, Left = 10, AutoSize = true, Enabled = false,
            Text = "Auto-estimate this repo's To-Do tickets"
        };
        _estimate.Click += (_, _) => ToggleEstimation();
        Controls.Add(_estimate);

        Controls.Add(new Label { Top = 225, Left = 10, AutoSize = true, Text = "Idle threshold (minutes):" });
        _idleMin = new NumericUpDown { Top = 243, Left = 10, Width = 80, Minimum = 1, Maximum = 120, Value = 10 };
        Controls.Add(_idleMin);

        Controls.Add(new Label { Top = 225, Left = 200, AutoSize = true, Text = "Jira poll interval (seconds):" });
        _pollSec = new NumericUpDown { Top = 243, Left = 200, Width = 80, Minimum = 30, Maximum = 600, Value = 90 };
        Controls.Add(_pollSec);

        Controls.Add(new Label { Top = 280, Left = 10, AutoSize = true, Text = "'In Progress' status name:" });
        _inProgress = new TextBox { Top = 300, Left = 10, Width = 260, Text = "In Progress" };
        _inProgress.TextChanged += (_, _) => StateChanged?.Invoke();
        Controls.Add(_inProgress);

        Controls.Add(new Label { Top = 280, Left = 290, AutoSize = true, Text = "Transition target status:" });
        _transitionTo = new TextBox { Top = 300, Left = 290, Width = 270, Text = "Review" };
        _transitionTo.TextChanged += (_, _) => StateChanged?.Invoke();
        Controls.Add(_transitionTo);

        ReloadList();
        _ = LoadProjectsAsync();
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
        RepoPath: _repoList.Items.Count > 0 ? ((RepoItem)_repoList.Items[0]!).Path : "",
        RememberPath: "",
        InProgressStatusName: _inProgress.Text.Trim(),
        TransitionToStatusName: _transitionTo.Text.Trim());

    private const string UnmappedOption = "(no Jira project)";

    /// <summary>Carries the path while displaying the mapping, so selection stays path-based.</summary>
    private sealed record RepoItem(string Path, string? ProjectKey, bool Estimates)
    {
        public override string ToString() =>
            (ProjectKey is null ? $"{Path}      {UnmappedOption}" : $"{Path}      -> {ProjectKey}")
            + (Estimates ? "" : "      (estimation off)");
    }

    private sealed record ProjectItem(string Key, string Name)
    {
        public override string ToString() => $"{Name}  ({Key})";
    }

    private void ReloadList()
    {
        var selected = (_repoList.SelectedItem as RepoItem)?.Path;

        _repoList.Items.Clear();
        foreach (var r in _repos.GetAll())
        {
            var item = new RepoItem(r.Path, _mappings.Find(r.Path), _switches.IsEnabled(r.Path));
            _repoList.Items.Add(item);
            if (string.Equals(item.Path, selected, StringComparison.OrdinalIgnoreCase))
                _repoList.SelectedItem = item;
        }
    }

    /// <summary>
    /// Fills the picker from Jira in the background. The page stays usable if this fails - the
    /// name match covers most repos, and a failure here should not block editing paths.
    /// </summary>
    private async Task LoadProjectsAsync()
    {
        var source = _sp.GetService<IJiraProjectSource>();
        if (source is null) return;

        IReadOnlyList<JiraProject> projects;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            projects = await source.ListProjectsAsync(timeout.Token);
        }
        catch (Exception)
        {
            BeginInvoke(() => _assign.Enabled = false);
            return;
        }

        BeginInvoke(() =>
        {
            _projectPicker.Items.Clear();
            _projectPicker.Items.Add(UnmappedOption);
            foreach (var p in projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                _projectPicker.Items.Add(new ProjectItem(p.Key, p.Name));
            SyncPickerToSelection();
        });
    }

    private void SyncPickerToSelection()
    {
        var key = (_repoList.SelectedItem as RepoItem)?.ProjectKey;
        if (key is null)
        {
            _projectPicker.SelectedIndex = _projectPicker.Items.Count > 0 ? 0 : -1;
            return;
        }

        for (var i = 0; i < _projectPicker.Items.Count; i++)
            if (_projectPicker.Items[i] is ProjectItem p &&
                string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                _projectPicker.SelectedIndex = i;
                return;
            }
    }

    private void SyncEstimateToSelection()
    {
        var item = _repoList.SelectedItem as RepoItem;
        _estimate.Enabled = item is not null;
        _estimate.Checked = item?.Estimates ?? false;
    }

    /// <summary>
    /// Saved on the click, like Assign. The worker reads the switch at the start of each repo's
    /// sweep, so the change takes effect from the next one.
    /// </summary>
    private void ToggleEstimation()
    {
        if (_repoList.SelectedItem is not RepoItem item) return;

        _switches.SetEnabled(item.Path, _estimate.Checked);
        ReloadList();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// A hand-picked mapping is stored with autoMatched false, so the name matcher never
    /// silently replaces it on a later sweep.
    /// </summary>
    private void AssignProject()
    {
        if (_repoList.SelectedItem is not RepoItem item)
        {
            MessageBox.Show("Select a repo first.", "TmTimeTracker",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_projectPicker.SelectedItem is ProjectItem project)
            _mappings.Save(item.Path, project.Key, autoMatched: false);
        else
            _mappings.Remove(item.Path);

        ReloadList();
        StateChanged?.Invoke();
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
        if (_repoList.SelectedItem is not RepoItem item) return;
        _repos.Remove(item.Path);
        _mappings.Remove(item.Path);
        _switches.Remove(item.Path);
        ReloadList();
        StateChanged?.Invoke();
    }
}
