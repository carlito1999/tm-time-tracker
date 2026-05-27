using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;

namespace TmTimeTracker.UI.Theming;

internal enum PillTone { Neutral, Active, Idle, Claude, Auth, Danger }

internal sealed class StatusPill : Control
{
    private string _text = string.Empty;
    private PillTone _tone = PillTone.Neutral;
    private bool _showDot = true;

    public StatusPill()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
        Font = Theme.Body;
        Height = 26;
    }

    [AllowNull]
    public override string Text
    {
        get => _text;
        set
        {
            _text = value ?? string.Empty;
            AutoFit();
            Invalidate();
        }
    }

    public PillTone Tone
    {
        get => _tone;
        set { _tone = value; Invalidate(); }
    }

    public bool ShowDot
    {
        get => _showDot;
        set { _showDot = value; AutoFit(); Invalidate(); }
    }

    public void Set(string text, PillTone tone)
    {
        _text = text ?? string.Empty;
        _tone = tone;
        AutoFit();
        Invalidate();
    }

    private void AutoFit()
    {
        var size = TextRenderer.MeasureText(_text, Font);
        int padX = 12;
        int dotW = _showDot ? 14 : 0;
        Width = dotW + size.Width + padX * 2;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Background);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        int radius = Height / 2;
        using var path = RoundedRectPath(rect, radius);
        using var bgBrush = new SolidBrush(Theme.Surface);
        using var borderPen = new Pen(Theme.Border);
        e.Graphics.FillPath(bgBrush, path);
        e.Graphics.DrawPath(borderPen, path);

        int textX = 12;
        if (_showDot)
        {
            using var dotBrush = new SolidBrush(ColorForTone(_tone));
            const int dotSize = 8;
            int dotY = (Height - dotSize) / 2;
            e.Graphics.FillEllipse(dotBrush, 12, dotY, dotSize, dotSize);
            textX = 12 + dotSize + 6;
        }

        var textColor = _tone == PillTone.Neutral ? Theme.TextSecondary : ColorForTone(_tone);
        TextRenderer.DrawText(
            e.Graphics, _text, Font,
            new Rectangle(textX, 0, Width - textX - 12, Height),
            textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }

    private static Color ColorForTone(PillTone tone) => tone switch
    {
        PillTone.Active => Theme.Accent,
        PillTone.Idle   => Theme.Idle,
        PillTone.Claude => Theme.ClaudeAccent,
        PillTone.Auth   => Theme.Accent,
        PillTone.Danger => Theme.Danger,
        _               => Theme.TextSecondary,
    };

    private static GraphicsPath RoundedRectPath(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
