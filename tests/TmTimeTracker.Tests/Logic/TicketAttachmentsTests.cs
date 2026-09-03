using FluentAssertions;
using TmTimeTracker.Jira;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

/// <summary>
/// Tickets here are routinely nothing but an attachment - TM-50's whole description is one media
/// node with no text - so the files are the ticket content, not decoration. They still arrive
/// from whoever filed the ticket, so kind, count, size and filename are all bounded.
/// </summary>
public class TicketAttachmentsTests
{
    private static JiraAttachment A(string name, string? mime = "image/png", long size = 1000) =>
        new(Id: "1", Filename: name, MimeType: mime, Size: size,
            Content: "https://example/attachment/content/1");

    [Theory]
    [InlineData("shot.png", AttachmentKind.Image)]
    [InlineData("photo.JPEG", AttachmentKind.Image)]
    [InlineData("spec.pdf", AttachmentKind.Pdf)]
    [InlineData("figures.xlsx", AttachmentKind.Spreadsheet)]
    [InlineData("export.csv", AttachmentKind.Text)]
    [InlineData("data.tsv", AttachmentKind.Text)]
    [InlineData("trace.log", AttachmentKind.Text)]
    public void Recognises_the_kinds_worth_reading(string name, AttachmentKind expected)
    {
        TicketAttachments.KindOf(A(name, mime: null)).Should().Be(expected);
    }

    // Jira reports plenty of uploads as application/octet-stream, so the extension decides first.
    [Fact]
    public void Trusts_the_extension_over_a_generic_mime_type()
    {
        var generic = A("figures.xlsx", mime: "application/octet-stream");

        TicketAttachments.KindOf(generic).Should().Be(AttachmentKind.Spreadsheet);
    }

    [Fact]
    public void Falls_back_to_the_mime_type_when_there_is_no_useful_extension()
    {
        TicketAttachments.KindOf(A("screenshot", mime: "image/png"))
            .Should().Be(AttachmentKind.Image);
    }

    [Theory]
    [InlineData("archive.zip")]
    [InlineData("installer.exe")]
    [InlineData("clip.mp4")]
    public void Skips_kinds_that_cannot_be_read(string name)
    {
        TicketAttachments.KindOf(A(name, mime: "application/octet-stream")).Should().BeNull();
        TicketAttachments.FilesToFetch(new[] { A(name, "application/octet-stream") })
            .Should().BeEmpty();
    }

    [Fact]
    public void Fetches_a_mixture_of_kinds()
    {
        var picked = TicketAttachments.FilesToFetch(new[]
        {
            A("shot.png", "image/png"),
            A("spec.pdf", "application/pdf"),
            A("numbers.xlsx", "application/octet-stream"),
            A("rows.csv", "text/csv")
        });

        picked.Select(f => f.Kind).Should().BeEquivalentTo(new[]
        {
            AttachmentKind.Image, AttachmentKind.Pdf,
            AttachmentKind.Spreadsheet, AttachmentKind.Text
        });
    }

    [Fact]
    public void Skips_an_attachment_with_no_download_url()
    {
        var noUrl = new JiraAttachment("1", "shot.png", "image/png", 1000, null);

        TicketAttachments.FilesToFetch(new[] { noUrl }).Should().BeEmpty();
    }

    // A ticket with twenty screenshots must not decide the cost of a run.
    [Fact]
    public void Caps_how_many_files_are_fetched()
    {
        var many = Enumerable.Range(1, 20).Select(i => A($"shot{i}.png")).ToArray();

        TicketAttachments.FilesToFetch(many).Should().HaveCount(TicketAttachments.MaxFiles);
    }

    [Fact]
    public void Skips_a_file_too_large_to_be_worth_reading()
    {
        var huge = A("huge.png", size: 50L * 1024 * 1024);

        TicketAttachments.FilesToFetch(new[] { huge }).Should().BeEmpty();
    }

    // A PDF is allowed to be larger than a screenshot before it is rejected.
    [Fact]
    public void Allows_a_pdf_larger_than_the_image_ceiling()
    {
        var pdf = A("spec.pdf", "application/pdf", size: 15L * 1024 * 1024);

        TicketAttachments.FilesToFetch(new[] { pdf }).Should().ContainSingle();
    }

    [Fact]
    public void Copes_with_a_ticket_that_has_no_attachments()
    {
        TicketAttachments.FilesToFetch(null).Should().BeEmpty();
    }

    /// <summary>
    /// The filename comes from whoever uploaded it and is used to build a path inside the
    /// worktree, so a traversal attempt must not escape the directory.
    /// </summary>
    [Theory]
    [InlineData(@"..\..\..\evil.png", "evil.png")]
    [InlineData("../../evil.png", "evil.png")]
    [InlineData(@"C:\windows\system32\evil.png", "evil.png")]
    [InlineData("normal-shot.png", "normal-shot.png")]
    public void Strips_any_path_from_the_filename(string given, string expected)
    {
        TicketAttachments.SafeFileName(given).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    public void Falls_back_to_a_placeholder_when_the_name_is_unusable(string given)
    {
        TicketAttachments.SafeFileName(given).Should().NotBeNullOrWhiteSpace();
        TicketAttachments.SafeFileName(given).Should().NotContain("..");
    }
}
