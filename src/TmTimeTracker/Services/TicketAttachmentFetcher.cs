using Microsoft.Extensions.Logging;
using TmTimeTracker.Jira;
using TmTimeTracker.Logic;

namespace TmTimeTracker.Services;

/// <summary>
/// Downloads a ticket's readable attachments into the estimation worktree so Claude can open
/// them alongside the code.
///
/// They go inside the worktree because the run is sandboxed to its working directory - a file
/// written anywhere else would be named in the prompt and then refused when Claude tried to read
/// it. The worktree is thrown away after every sweep, so nothing accumulates.
///
/// A spreadsheet is converted to text rather than downloaded as-is. The run is --restricted,
/// with no Bash or REPL to parse a binary workbook, so shipping the .xlsx alone would hand
/// Claude a file it cannot open.
///
/// Nothing here is allowed to fail an estimate. A ticket whose attachment cannot be downloaded
/// is still worth estimating from its text.
/// </summary>
public sealed class TicketAttachmentFetcher
{
    private const string FolderName = ".ticket-attachments";

    private readonly IJiraAttachmentSource _attachments;
    private readonly ILogger<TicketAttachmentFetcher> _log;

    public TicketAttachmentFetcher(IJiraAttachmentSource attachments,
        ILogger<TicketAttachmentFetcher> log)
    {
        _attachments = attachments;
        _log = log;
    }

    /// <returns>Paths relative to the worktree, ready to name in the prompt.</returns>
    public async Task<IReadOnlyList<string>> FetchAsync(string worktree, string ticketKey,
        IReadOnlyList<JiraAttachment>? attachments, CancellationToken ct)
    {
        var wanted = TicketAttachments.FilesToFetch(attachments);
        if (wanted.Count == 0) return Array.Empty<string>();

        var relativeDir = Path.Combine(FolderName, TicketAttachments.SafeFileName(ticketKey));
        var absoluteDir = Path.Combine(worktree, relativeDir);

        try
        {
            Directory.CreateDirectory(absoluteDir);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not create the attachment folder for {Ticket}", ticketKey);
            return Array.Empty<string>();
        }

        var written = new List<string>();

        foreach (var file in wanted)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var bytes = await _attachments
                    .DownloadAttachmentAsync(file.Attachment.Content!, ct).ConfigureAwait(false);

                var name = TicketAttachments.SafeFileName(file.Attachment.Filename);
                var saved = file.Kind == AttachmentKind.Spreadsheet
                    ? SaveSpreadsheetAsText(absoluteDir, name, bytes)
                    : Save(absoluteDir, name, bytes);

                if (saved is null) continue;

                written.Add(Path.Combine(relativeDir, saved).Replace('\\', '/'));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not fetch {File} for {Ticket}",
                    file.Attachment.Filename, ticketKey);
            }
        }

        if (written.Count > 0)
            _log.LogDebug("Fetched {Count} attachment(s) for {Ticket}", written.Count, ticketKey);

        return written;
    }

    private static string Save(string dir, string name, byte[] bytes)
    {
        File.WriteAllBytes(Path.Combine(dir, name), bytes);
        return name;
    }

    /// <summary>
    /// Writes the workbook's text beside nothing else: the binary itself is not saved, because
    /// the only tool that could open it is unavailable by design. An unreadable workbook yields
    /// no file rather than an empty one that wastes a read.
    /// </summary>
    private string? SaveSpreadsheetAsText(string dir, string name, byte[] bytes)
    {
        var text = XlsxText.Extract(bytes);
        if (text.Length == 0)
        {
            _log.LogDebug("No readable text in {File}; skipping it", name);
            return null;
        }

        var textName = name + ".txt";
        File.WriteAllText(Path.Combine(dir, textName), text);
        return textName;
    }
}
