namespace TmTimeTracker.UI;

internal static class AppIcon
{
    private const string ResourceName = "TmTimeTracker.app.ico";

    public static Icon Load(Size? size = null)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found");
        return size is { } s ? new Icon(stream, s.Width, s.Height) : new Icon(stream);
    }
}
