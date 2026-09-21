using System.IO.Compression;
using System.Xml.Linq;
using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class XlsxWriterTests
{
    private static readonly XNamespace Main =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static readonly string[] Headers = { "Date", "Time", "Repo", "Ticket" };

    private static byte[] Write(params string[][] rows) =>
        XlsxWriter.Write("Week Log", Headers,
            rows.Select(r => (IReadOnlyList<string>)r).ToList());

    private static ZipArchive Open(byte[] bytes) =>
        new(new MemoryStream(bytes), ZipArchiveMode.Read);

    private static XDocument Part(byte[] bytes, string path)
    {
        using var zip = Open(bytes);
        var entry = zip.Entries.Single(e =>
            e.FullName.Equals(path, StringComparison.OrdinalIgnoreCase));
        using var reader = entry.Open();
        return XDocument.Load(reader);
    }

    // Excel refuses a workbook missing any of these outright.
    [Fact]
    public void Writes_every_part_a_workbook_requires()
    {
        using var zip = Open(Write(new[] { "31-08-26", "08:00", "repo", "TM-1" }));

        zip.Entries.Select(e => e.FullName).Should().Contain(new[]
        {
            "[Content_Types].xml",
            "_rels/.rels",
            "xl/workbook.xml",
            "xl/_rels/workbook.xml.rels",
            "xl/styles.xml",
            "xl/worksheets/sheet1.xml"
        });
    }

    // The reader already in this codebase is the cheapest proof the cells really landed.
    [Fact]
    public void Round_trips_its_own_cells_through_the_reader()
    {
        var text = XlsxText.Extract(Write(new[] { "31-08-26", "08:00–09:00", "sheeponline-new", "SN-1: studbooks" }));

        text.Should().Contain("sheeponline-new").And.Contain("SN-1: studbooks");
    }

    [Fact]
    public void Writes_the_header_row_first()
    {
        var sheet = Part(Write(new[] { "a", "b", "c", "d" }), "xl/worksheets/sheet1.xml");

        var first = sheet.Descendants(Main + "row").First();
        first.Descendants(Main + "t").Select(t => t.Value)
            .Should().Equal("Date", "Time", "Repo", "Ticket");
    }

    // Ticket summaries carry ampersands and angle brackets often enough to matter, and an
    // unescaped one makes the whole part unparseable.
    [Fact]
    public void Escapes_xml_special_characters_in_a_cell()
    {
        var bytes = Write(new[] { "31-08-26", "08:00", "repo", "TM-1: fix <List> & \"quotes\"" });

        XlsxText.Extract(bytes).Should().Contain("fix <List> & \"quotes\"");
    }

    // The blank spacer row between days is written as empty cells, not skipped.
    [Fact]
    public void Writes_a_row_of_empty_cells_without_dropping_it()
    {
        var sheet = Part(Write(new[] { "a", "b", "c", "d" }, new[] { "", "", "", "" }),
            "xl/worksheets/sheet1.xml");

        sheet.Descendants(Main + "row").Should().HaveCount(3);
    }

    /// <summary>
    /// The trap that silently costs you every bit of styling: Excel requires the two reserved
    /// default fills at indexes 0 and 1 before any of your own. Get it wrong and Excel "repairs"
    /// the workbook and drops the formatting, without ever reporting an error.
    /// </summary>
    [Fact]
    public void Declares_the_two_reserved_default_fills_before_its_own()
    {
        var styles = Part(Write(new[] { "a", "b", "c", "d" }), "xl/styles.xml");

        var patterns = styles.Descendants(Main + "fill")
            .Select(f => f.Element(Main + "patternFill")?.Attribute("patternType")?.Value)
            .ToList();

        patterns[0].Should().Be("none");
        patterns[1].Should().Be("gray125");
        patterns.Should().HaveCountGreaterThan(2, "the header and the tinted columns need a fill of their own");
    }

    // Schema order, and Excel enforces it.
    [Fact]
    public void Places_the_column_widths_before_the_sheet_data()
    {
        var sheet = Part(Write(new[] { "a", "b", "c", "d" }), "xl/worksheets/sheet1.xml");

        var names = sheet.Root!.Elements().Select(e => e.Name.LocalName).ToList();
        names.IndexOf("cols").Should().BeGreaterThanOrEqualTo(0);
        names.IndexOf("cols").Should().BeLessThan(names.IndexOf("sheetData"));
    }

    // Several tickets share one hour, and the newline that separates them only renders when the
    // cell is marked as wrapping.
    [Fact]
    public void Marks_the_last_column_as_wrapping()
    {
        var styles = Part(Write(new[] { "a", "b", "c", "d" }), "xl/styles.xml");

        styles.Descendants(Main + "alignment")
            .Should().Contain(a => a.Attribute("wrapText")!.Value == "1");
    }

    [Fact]
    public void Preserves_a_newline_inside_a_cell()
    {
        var bytes = Write(new[] { "31-08-26", "08:00", "repo", "TM-1\nTM-2" });

        var sheet = Part(bytes, "xl/worksheets/sheet1.xml");
        sheet.Descendants(Main + "t").Select(t => t.Value).Should().Contain("TM-1\nTM-2");
    }

    // Implicit positioning is the other thing Excel quietly rejects.
    [Fact]
    public void Gives_every_row_and_cell_an_explicit_reference()
    {
        var sheet = Part(Write(new[] { "a", "b", "c", "d" }), "xl/worksheets/sheet1.xml");

        sheet.Descendants(Main + "row").Should().OnlyContain(r => r.Attribute("r") != null);
        sheet.Descendants(Main + "c").Should().OnlyContain(c => c.Attribute("r") != null);
        sheet.Descendants(Main + "row").First().Elements(Main + "c").First()
            .Attribute("r")!.Value.Should().Be("A1");
    }

    [Fact]
    public void Names_the_sheet()
    {
        var workbook = Part(Write(new[] { "a", "b", "c", "d" }), "xl/workbook.xml");

        workbook.Descendants(Main + "sheet").Single()
            .Attribute("name")!.Value.Should().Be("Week Log");
    }
}
