using TmTimeTracker.Jira;

namespace TmTimeTracker.Logic;

/// <summary>How a downloaded attachment should be presented to the estimation run.</summary>
public enum AttachmentKind
{
    /// <summary>Read natively.</summary>
    Image,

    /// <summary>Read natively.</summary>
    Pdf,

    /// <summary>Binary. Converted to text by <see cref="XlsxText"/> before Claude sees it.</summary>
    Spreadsheet,

    /// <summary>CSV, TSV, plain text, logs, JSON - readable as-is.</summary>
    Text
}

public sealed record TicketFile(JiraAttachment Attachment, AttachmentKind Kind);

/// <summary>
/// Decides which of a ticket's attachments are worth putting in front of Claude.
///
/// This is not a nicety. Tickets here are routinely nothing but an attachment: TM-50's entire
/// description is a single ADF media node carrying no text, so flattening it yields an empty
/// string and the estimate would rest on the summary alone. The attachment is the ticket.
///
/// The kind is decided by file extension first and MIME type second, because Jira reports plenty
/// of uploads as application/octet-stream.
///
/// Everything is bounded, because attachments come from whoever filed the ticket: a fixed set of
/// kinds, a cap on how many, a per-kind size cap, and filenames stripped to a bare name before
/// they are used to build a path.
/// </summary>
public static class TicketAttachments
{
    /// <summary>Each file is content in a paid context window.</summary>
    public const int MaxFiles = 6;

    private static readonly Dictionary<string, AttachmentKind> ByExtension =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = AttachmentKind.Image,
            [".jpg"] = AttachmentKind.Image,
            [".jpeg"] = AttachmentKind.Image,
            [".gif"] = AttachmentKind.Image,
            [".webp"] = AttachmentKind.Image,
            [".bmp"] = AttachmentKind.Image,

            [".pdf"] = AttachmentKind.Pdf,

            [".xlsx"] = AttachmentKind.Spreadsheet,
            [".xlsm"] = AttachmentKind.Spreadsheet,

            [".csv"] = AttachmentKind.Text,
            [".tsv"] = AttachmentKind.Text,
            [".txt"] = AttachmentKind.Text,
            [".log"] = AttachmentKind.Text,
            [".md"] = AttachmentKind.Text,
            [".json"] = AttachmentKind.Text,
            [".xml"] = AttachmentKind.Text,
            [".yml"] = AttachmentKind.Text,
            [".yaml"] = AttachmentKind.Text
        };

    private static long MaxBytesFor(AttachmentKind kind) => kind switch
    {
        AttachmentKind.Image => 10L * 1024 * 1024,
        AttachmentKind.Pdf => 25L * 1024 * 1024,
        AttachmentKind.Spreadsheet => 25L * 1024 * 1024,
        _ => 5L * 1024 * 1024
    };

    public static IReadOnlyList<TicketFile> FilesToFetch(IReadOnlyList<JiraAttachment>? all)
    {
        if (all is null) return Array.Empty<TicketFile>();

        var picked = new List<TicketFile>();

        foreach (var attachment in all)
        {
            if (string.IsNullOrWhiteSpace(attachment.Content)) continue;

            var kind = KindOf(attachment);
            if (kind is null) continue;
            if (attachment.Size <= 0 || attachment.Size > MaxBytesFor(kind.Value)) continue;

            picked.Add(new TicketFile(attachment, kind.Value));
            if (picked.Count == MaxFiles) break;
        }

        return picked;
    }

    /// <summary>
    /// Extension first, MIME second. Jira reports many uploads as application/octet-stream, and
    /// an .xlsx arriving with a generic type is still a spreadsheet.
    /// </summary>
    public static AttachmentKind? KindOf(JiraAttachment attachment)
    {
        var extension = Path.GetExtension(SafeFileName(attachment.Filename));
        if (extension.Length > 0 && ByExtension.TryGetValue(extension, out var byExtension))
            return byExtension;

        var mime = attachment.MimeType ?? "";
        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return AttachmentKind.Image;
        if (mime.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)) return AttachmentKind.Pdf;
        if (mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) return AttachmentKind.Text;
        if (mime.Contains("spreadsheet", StringComparison.OrdinalIgnoreCase)) return AttachmentKind.Spreadsheet;

        return null;
    }

    /// <summary>
    /// Reduces an uploaded filename to a bare name. The value is attacker-influenced and is used
    /// to build a path inside the worktree, so anything resembling a directory is discarded
    /// rather than sanitised in place.
    /// </summary>
    public static string SafeFileName(string? filename)
    {
        var name = (filename ?? "").Replace('\\', '/');

        var lastSlash = name.LastIndexOf('/');
        if (lastSlash >= 0) name = name[(lastSlash + 1)..];

        // Drops a drive prefix such as "C:" that survives having no separator after it.
        var colon = name.LastIndexOf(':');
        if (colon >= 0) name = name[(colon + 1)..];

        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        name = name.Trim().Trim('.');

        return name.Length == 0 ? "attachment" : name;
    }
}
