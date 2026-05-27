using System.Drawing.Drawing2D;

namespace TmTimeTracker.UI.Theming;

internal enum ButtonKind { Default, Accent, Danger }

internal sealed class FlatButton : Button
{
    private bool _hover;
    private bool _pressed;

    public ButtonKind Kind { get; set; } = ButtonKind.Default;

    public FlatButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
        Cursor = Cursors.Hand;
        Font = Theme.Body;
        Height = 32;
        BackColor = Theme.Background;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        _pressed = true;
        Invalidate();
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var (bg, fg, border) = ColorsForState();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Background);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedRectPath(rect, 6);
        using var bgBrush = new SolidBrush(bg);
        using var borderPen = new Pen(border);
        e.Graphics.FillPath(bgBrush, path);
        e.Graphics.DrawPath(borderPen, path);

        TextRenderer.DrawText(
            e.Graphics, Text, Font, rect, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private (Color bg, Color fg, Color border) ColorsForState()
    {
        if (!Enabled)
            return (Theme.Surface, Theme.TextSecondary, Theme.Border);

        return Kind switch
        {
            ButtonKind.Accent => _pressed
                ? (Theme.AccentMuted, Theme.Background, Theme.AccentMuted)
                : (Theme.Accent, Theme.Background, Theme.Accent),

            ButtonKind.Danger => _pressed
                ? (Theme.SurfaceAlt, Theme.Danger, Theme.Danger)
                : _hover
                    ? (Theme.Surface, Theme.Danger, Theme.Danger)
                    : (Theme.Surface, Theme.Danger, Theme.Border),

            _ => _pressed
                ? (Theme.SurfaceAlt, Theme.TextPrimary, Theme.Accent)
                : _hover
                    ? (Theme.SurfaceAlt, Theme.TextPrimary, Theme.Border)
                    : (Theme.Surface, Theme.TextPrimary, Theme.Border),
        };
    }

    private static GraphicsPath RoundedRectPath(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
