using Microsoft.Extensions.Logging;
using TmTimeTracker.Logic;
using TmTimeTracker.Services;
using TmTimeTracker.UI.Theming;

namespace TmTimeTracker.UI;

/// <summary>
/// The weekly report window: pick a range and a day window, see exactly what will be written,
/// then write it. The preview and the file come from the same WeeklyReportExporter.BuildGrid,
/// so what is on screen is what lands in the spreadsheet.
/// </summary>
public sealed class ExportReportForm : Form
{
    private readonly WeeklyReportExporter _exporter;
    private readonly ILogger<ExportReportForm> _log;

    private readonly DateTimePicker _from;
    private readonly DateTimePicker _to;
    private readonly NumericUpDown _startHour;
    private readonly NumericUpDown _endHour;
    private readonly TextBox _fileName;
    private readonly DataGridView _preview;
    private readonly Label _status;

    // Set while the controls are being populated, so seeding a value does not trigger a rebuild
    // per control before the form is even visible.
    private bool _loading = true;

    public ExportReportForm(WeeklyReportExporter exporter, ILogger<ExportReportForm> log)
    {
        _exporter = exporter;
        _log = log;

        Text = "Export weekly report";
        Icon = AppIcon.Load();
        ClientSize = new Size(900, 620);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 480);

        var (from, to) = _exporter.CurrentWeek();

        _from = DatePicker(from);
        _to = DatePicker(to);
        _startHour = HourPicker(WeeklyReportExporter.DefaultDayStartHour);
        _endHour = HourPicker(WeeklyReportExporter.DefaultDayEndHour);

        _fileName = new TextBox
        {
            Width = 280,
            Text = WeeklyReportExporter.SuggestFileName(from),
            Margin = new Padding(0, 4, 16, 0)
        };

        _preview = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            // DisplayedCells, not AllCells: AllCells measures every wrapped row synchronously
            // on each Add, and a month-long range at 08-17 is around three hundred rows.
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells,
            ScrollBars = ScrollBars.Both
        };
        _preview.Columns.Add("date", "Date");
        _preview.Columns.Add("time", "Time");
        _preview.Columns.Add("repo", "Repo");
        _preview.Columns.Add("ticket", "Ticket");
        _preview.Columns[0].Width = 80;
        _preview.Columns[1].Width = 100;
        _preview.Columns[2].Width = 190;
        _preview.Columns[3].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        // Several tickets stack in one hour, and the newline only shows when the cell wraps.
        _preview.Columns[3].DefaultCellStyle.WrapMode = DataGridViewTriState.True;

        _status = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 8, 16, 0)
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(OptionsRow(), 0, 0);
        root.Controls.Add(_preview, 0, 1);
        root.Controls.Add(ActionsRow(), 0, 2);

        Controls.Add(root);

        _loading = false;
        RefreshPreview();

        Theme.Apply(this);
    }

    private Control OptionsRow()
    {
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(0, 0, 0, 12)
        };

        row.Controls.Add(Caption("From"));
        row.Controls.Add(_from);
        row.Controls.Add(Caption("To"));
        row.Controls.Add(_to);
        row.Controls.Add(Caption("Hours"));
        row.Controls.Add(_startHour);
        row.Controls.Add(Caption("to"));
        row.Controls.Add(_endHour);
        row.Controls.Add(Caption("File name"));
        row.Controls.Add(_fileName);

        return row;
    }

    private Control ActionsRow()
    {
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 12, 0, 0)
        };

        var export = new FlatButton
        {
            Text = "↓  Export",
            Kind = ButtonKind.Accent,
            Width = 120,
            Margin = new Padding(0, 0, 8, 0)
        };
        export.Click += (_, _) => Export();

        var close = new FlatButton { Text = "Close", Width = 100, Margin = new Padding(0, 0, 16, 0) };
        close.Click += (_, _) => Close();

        row.Controls.Add(export);
        row.Controls.Add(close);
        row.Controls.Add(_status);
        return row;
    }

    private static Label Caption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(0, 8, 6, 0)
    };

    private DateTimePicker DatePicker(DateTime value)
    {
        var picker = new DateTimePicker
        {
            Format = DateTimePickerFormat.Short,
            Width = 110,
            Value = value,
            Margin = new Padding(0, 4, 16, 0)
        };
        picker.ValueChanged += (_, _) => RefreshPreview();
        return picker;
    }

    private NumericUpDown HourPicker(int value)
    {
        var picker = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 24,
            Value = value,
            Width = 55,
            Margin = new Padding(0, 4, 8, 0)
        };
        picker.ValueChanged += (_, _) => RefreshPreview();
        return picker;
    }

    private void RefreshPreview()
    {
        if (_loading) return;

        try
        {
            var grid = BuildGrid();

            // Dragging a date picker fires this on every step, so the grid is refilled in one
            // layout pass rather than re-measuring per row.
            _preview.SuspendLayout();
            try
            {
                _preview.Rows.Clear();
                foreach (var row in grid)
                    _preview.Rows.Add(row.Date, row.TimeSlot, row.Repo, row.Tickets);
            }
            finally
            {
                _preview.ResumeLayout();
            }

            var tracked = grid.Count(r => r.Repo.Length > 0);
            _status.Text = grid.Count == 0
                ? "Nothing in this range - check the dates and the hour window."
                : $"{grid.Count} rows, {tracked} with tracked time.";
        }
        catch (Exception ex)
        {
            // A preview failure must not take the window down; the user can adjust and retry.
            _log.LogError(ex, "Building the report preview failed");
            _preview.Rows.Clear();
            _status.Text = "Could not build the preview - see the log.";
        }
    }

    private IReadOnlyList<GridRow> BuildGrid() =>
        _exporter.BuildGrid(_from.Value, _to.Value, (int)_startHour.Value, (int)_endHour.Value);

    private void Export()
    {
        try
        {
            var path = _exporter.Export(
                _from.Value, _to.Value, (int)_startHour.Value, (int)_endHour.Value,
                WeeklyReportExporter.DefaultFolder, _fileName.Text);

            _status.Text = $"Written to {path}";
            _log.LogInformation("Weekly report written to {Path}", path);

            // Reveals the file with it already selected, the way "Open log folder" opens its own.
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Exporting the weekly report failed");
            MessageBox.Show($"Could not write the report.\n\n{ex.Message}", "TmTimeTracker",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
