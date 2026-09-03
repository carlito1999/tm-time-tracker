using System.IO.Compression;
using System.Text;
using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

/// <summary>
/// The estimation run is --restricted, so Claude has no Bash or REPL to open a spreadsheet with.
/// Rather than relax that, the text is extracted here. These tests build real .xlsx zips - the
/// format is the thing under test, so a fake would prove nothing.
/// </summary>
public class XlsxTextTests
{
    private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static byte[] Workbook(string? sharedStrings, params string[] sheets)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (sharedStrings is not null) Write(zip, "xl/sharedStrings.xml", sharedStrings);
            for (var i = 0; i < sheets.Length; i++)
                Write(zip, $"xl/worksheets/sheet{i + 1}.xml", sheets[i]);
        }
        return ms.ToArray();
    }

    private static void Write(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string Shared(params string[] values) =>
        $"""<sst xmlns="{Ns}">{string.Concat(values.Select(v => $"<si><t>{v}</t></si>"))}</sst>""";

    private static string Sheet(string rows) =>
        $"""<worksheet xmlns="{Ns}"><sheetData>{rows}</sheetData></worksheet>""";

    [Fact]
    public void Reads_shared_string_cells()
    {
        var bytes = Workbook(Shared("Ticket", "Status"),
            Sheet("""<row><c t="s"><v>0</v></c><c t="s"><v>1</v></c></row>"""));

        XlsxText.Extract(bytes).Should().Contain("Ticket").And.Contain("Status");
    }

    [Fact]
    public void Reads_numeric_cells()
    {
        var bytes = Workbook(null, Sheet("""<row><c><v>42</v></c><c><v>7.5</v></c></row>"""));

        XlsxText.Extract(bytes).Should().Contain("42").And.Contain("7.5");
    }

    [Fact]
    public void Reads_inline_strings()
    {
        var bytes = Workbook(null,
            Sheet("""<row><c t="inlineStr"><is><t>written inline</t></is></c></row>"""));

        XlsxText.Extract(bytes).Should().Contain("written inline");
    }

    // Excel splits one styled string across several runs; joining with a separator would corrupt it.
    [Fact]
    public void Joins_a_shared_string_split_across_runs()
    {
        var split = $"""<sst xmlns="{Ns}"><si><t>Total </t><t>amount</t></si></sst>""";
        var bytes = Workbook(split, Sheet("""<row><c t="s"><v>0</v></c></row>"""));

        XlsxText.Extract(bytes).Should().Contain("Total amount");
    }

    [Fact]
    public void Keeps_rows_on_separate_lines()
    {
        var bytes = Workbook(Shared("first", "second"),
            Sheet("""<row><c t="s"><v>0</v></c></row><row><c t="s"><v>1</v></c></row>"""));

        var lines = XlsxText.Extract(bytes)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).ToList();

        lines.Should().Contain("first").And.Contain("second");
    }

    [Fact]
    public void Reads_every_sheet()
    {
        var bytes = Workbook(Shared("on sheet one", "on sheet two"),
            Sheet("""<row><c t="s"><v>0</v></c></row>"""),
            Sheet("""<row><c t="s"><v>1</v></c></row>"""));

        XlsxText.Extract(bytes).Should().Contain("on sheet one").And.Contain("on sheet two");
    }

    [Fact]
    public void Skips_empty_rows()
    {
        var bytes = Workbook(Shared("only value"),
            Sheet("""<row/><row><c t="s"><v>0</v></c></row><row/>"""));

        XlsxText.Extract(bytes).Trim().Split('\n')
            .Count(l => l.Trim().Length > 0 && !l.StartsWith("#")).Should().Be(1);
    }

    // A shared-string index pointing past the table must not throw mid-estimate.
    [Fact]
    public void Survives_an_out_of_range_shared_string_index()
    {
        var bytes = Workbook(Shared("only one"), Sheet("""<row><c t="s"><v>99</v></c></row>"""));

        var extract = () => XlsxText.Extract(bytes);

        extract.Should().NotThrow();
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3, 4 })]
    public void Returns_empty_for_something_that_is_not_a_workbook(byte[] bytes)
    {
        XlsxText.Extract(bytes).Should().BeEmpty();
    }
}
