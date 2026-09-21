using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace TmTimeTracker.Logic;

/// <summary>
/// Writes a single-sheet .xlsx of text cells.
///
/// An .xlsx is a zip of XML parts, so this needs no third-party library - the same call
/// <see cref="XlsxText"/> makes in the other direction. Taking ClosedXML or EPPlus for one
/// report would also grow a self-contained single-file executable that is already ~158 MB.
///
/// Excel is far stricter than the reader here. Three things it rejects silently, "repairing" the
/// workbook and dropping every bit of styling rather than reporting an error:
///   - fills must begin with the two reserved defaults, none at 0 and gray125 at 1
///   - cols must precede sheetData
///   - every row and cell needs an explicit reference; implicit positioning is not accepted
/// All three are covered by tests, because none of them fail loudly.
/// </summary>
public static class XlsxWriter
{
    private static readonly XNamespace Main =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PkgRel =
        "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace ContentTypes =
        "http://schemas.openxmlformats.org/package/2006/content-types";

    // Style indexes into cellXfs below. 0 is the plain default Excel expects to exist.
    private const int HeaderStyle = 1;
    private const int TintedStyle = 2;
    private const int WrappedStyle = 3;

    /// <summary>The last column holds several stacked tickets, so it is the wide, wrapping one.</summary>
    private static readonly double[] ColumnWidths = { 12, 14, 26, 70 };

    public static byte[] Write(string sheetName, IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", ContentTypesPart());
            Add(zip, "_rels/.rels", RootRels());
            Add(zip, "xl/workbook.xml", WorkbookPart(sheetName));
            Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRels());
            Add(zip, "xl/styles.xml", StylesPart());
            Add(zip, "xl/worksheets/sheet1.xml", SheetPart(headers, rows));
        }
        return buffer.ToArray();
    }

    private static void Add(ZipArchive zip, string path, XDocument doc)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        // No indentation: whitespace added between elements would be significant inside a cell.
        doc.Save(stream, SaveOptions.DisableFormatting);
    }

    private static XDocument ContentTypesPart() => new(
        new XElement(ContentTypes + "Types",
            new XElement(ContentTypes + "Default",
                new XAttribute("Extension", "rels"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(ContentTypes + "Default",
                new XAttribute("Extension", "xml"),
                new XAttribute("ContentType", "application/xml")),
            Override("/xl/workbook.xml", "sheet.main"),
            Override("/xl/worksheets/sheet1.xml", "worksheet"),
            Override("/xl/styles.xml", "styles")));

    private static XElement Override(string part, string kind) =>
        new(ContentTypes + "Override",
            new XAttribute("PartName", part),
            new XAttribute("ContentType",
                $"application/vnd.openxmlformats-officedocument.spreadsheetml.{kind}+xml"));

    private static XDocument RootRels() => new(
        new XElement(PkgRel + "Relationships",
            Relationship("rId1", "officeDocument", "xl/workbook.xml")));

    private static XDocument WorkbookRels() => new(
        new XElement(PkgRel + "Relationships",
            Relationship("rId1", "worksheet", "worksheets/sheet1.xml"),
            Relationship("rId2", "styles", "styles.xml")));

    private static XElement Relationship(string id, string kind, string target) =>
        new(PkgRel + "Relationship",
            new XAttribute("Id", id),
            new XAttribute("Type",
                $"http://schemas.openxmlformats.org/officeDocument/2006/relationships/{kind}"),
            new XAttribute("Target", target));

    private static XDocument WorkbookPart(string sheetName) => new(
        new XElement(Main + "workbook",
            new XAttribute(XNamespace.Xmlns + "r", Rel.NamespaceName),
            new XElement(Main + "sheets",
                new XElement(Main + "sheet",
                    new XAttribute("name", SafeSheetName(sheetName)),
                    new XAttribute("sheetId", "1"),
                    new XAttribute(Rel + "id", "rId1")))));

    /// <summary>Excel caps a sheet name at 31 characters and bans a handful of punctuation.</summary>
    private static string SafeSheetName(string name)
    {
        var banned = new[] { '[', ']', ':', '*', '?', '/', '\\' };
        var cleaned = new string(name.Where(c => !banned.Contains(c)).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = "Sheet1";
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }

    private static XDocument StylesPart() => new(
        new XElement(Main + "styleSheet",
            new XElement(Main + "fonts", new XAttribute("count", "2"),
                Font(bold: false), Font(bold: true)),
            new XElement(Main + "fills", new XAttribute("count", "4"),
                // Indexes 0 and 1 are reserved by the format and must come first.
                Pattern("none"), Pattern("gray125"),
                Solid("FFD9D2E9"),   // header
                Solid("FFEDE9F5")),  // the tinted date/time/repo columns
            new XElement(Main + "borders", new XAttribute("count", "2"),
                new XElement(Main + "border",
                    new XElement(Main + "left"), new XElement(Main + "right"),
                    new XElement(Main + "top"), new XElement(Main + "bottom"),
                    new XElement(Main + "diagonal")),
                ThinBorder()),
            new XElement(Main + "cellStyleXfs", new XAttribute("count", "1"),
                new XElement(Main + "xf",
                    new XAttribute("numFmtId", "0"), new XAttribute("fontId", "0"),
                    new XAttribute("fillId", "0"), new XAttribute("borderId", "0"))),
            new XElement(Main + "cellXfs", new XAttribute("count", "4"),
                Xf(fontId: 0, fillId: 0, borderId: 0, wrap: null),
                Xf(fontId: 1, fillId: 2, borderId: 1, wrap: null),
                Xf(fontId: 0, fillId: 3, borderId: 1, wrap: false),
                Xf(fontId: 0, fillId: 0, borderId: 1, wrap: true))));

    private static XElement Font(bool bold)
    {
        var font = new XElement(Main + "font",
            new XElement(Main + "sz", new XAttribute("val", "11")),
            new XElement(Main + "name", new XAttribute("val", "Calibri")));
        if (bold) font.AddFirst(new XElement(Main + "b"));
        return font;
    }

    private static XElement Pattern(string type) =>
        new(Main + "fill",
            new XElement(Main + "patternFill", new XAttribute("patternType", type)));

    private static XElement Solid(string argb) =>
        new(Main + "fill",
            new XElement(Main + "patternFill", new XAttribute("patternType", "solid"),
                new XElement(Main + "fgColor", new XAttribute("rgb", argb)),
                new XElement(Main + "bgColor", new XAttribute("indexed", "64"))));

    private static XElement ThinBorder()
    {
        XElement Side(string name) => new(Main + name,
            new XAttribute("style", "thin"),
            new XElement(Main + "color", new XAttribute("rgb", "FFBFBFBF")));

        return new XElement(Main + "border",
            Side("left"), Side("right"), Side("top"), Side("bottom"),
            new XElement(Main + "diagonal"));
    }

    /// <summary>
    /// Every alignment written here carries wrapText, present or absent, so a reader can compare
    /// the attribute without first checking whether it exists.
    /// </summary>
    private static XElement Xf(int fontId, int fillId, int borderId, bool? wrap)
    {
        var xf = new XElement(Main + "xf",
            new XAttribute("numFmtId", "0"),
            new XAttribute("fontId", fontId),
            new XAttribute("fillId", fillId),
            new XAttribute("borderId", borderId),
            new XAttribute("xfId", "0"),
            new XAttribute("applyFont", "1"),
            new XAttribute("applyFill", "1"),
            new XAttribute("applyBorder", "1"));

        if (wrap is { } w)
        {
            xf.Add(new XAttribute("applyAlignment", "1"));
            xf.Add(new XElement(Main + "alignment",
                new XAttribute("vertical", "top"),
                new XAttribute("wrapText", w ? "1" : "0")));
        }

        return xf;
    }

    private static XDocument SheetPart(IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var widest = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        var columnCount = Math.Max(headers.Count, widest);

        // cols must precede sheetData; Excel rejects the other order.
        var cols = new XElement(Main + "cols");
        for (var i = 0; i < columnCount; i++)
            cols.Add(new XElement(Main + "col",
                new XAttribute("min", i + 1),
                new XAttribute("max", i + 1),
                new XAttribute("width", i < ColumnWidths.Length ? ColumnWidths[i] : 20),
                new XAttribute("customWidth", "1")));

        var data = new XElement(Main + "sheetData");
        data.Add(Row(1, headers, _ => HeaderStyle));

        for (var i = 0; i < rows.Count; i++)
            // The last column stacks several tickets, so it wraps; the rest carry the tint.
            data.Add(Row(i + 2, rows[i], col => col == columnCount - 1 ? WrappedStyle : TintedStyle));

        return new XDocument(new XElement(Main + "worksheet", cols, data));
    }

    private static XElement Row(int rowNumber, IReadOnlyList<string> cells, Func<int, int> styleFor)
    {
        var row = new XElement(Main + "row", new XAttribute("r", rowNumber));

        for (var col = 0; col < cells.Count; col++)
        {
            var value = cells[col] ?? "";

            var cell = new XElement(Main + "c",
                new XAttribute("r", $"{ColumnName(col)}{rowNumber}"),
                new XAttribute("s", styleFor(col)));

            // An empty cell keeps its reference and styling but carries no value part - the
            // spacer row between days is written this way rather than skipped.
            if (value.Length > 0)
            {
                cell.Add(new XAttribute("t", "inlineStr"));
                cell.Add(new XElement(Main + "is",
                    new XElement(Main + "t",
                        new XAttribute(XNamespace.Xml + "space", "preserve"), value)));
            }

            row.Add(cell);
        }

        return row;
    }

    private static string ColumnName(int zeroBasedIndex)
    {
        var name = new StringBuilder();
        var n = zeroBasedIndex;
        do
        {
            name.Insert(0, (char)('A' + n % 26));
            n = n / 26 - 1;
        }
        while (n >= 0);
        return name.ToString();
    }
}
