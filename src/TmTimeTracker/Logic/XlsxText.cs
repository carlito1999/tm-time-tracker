using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace TmTimeTracker.Logic;

/// <summary>
/// Pulls the readable text out of an .xlsx.
///
/// The estimation run is --restricted: no Bash, no PowerShell, no REPL. That is deliberate, but
/// it means a spreadsheet attached to a ticket is a binary blob Claude cannot open - it would be
/// downloaded, named in the prompt, and useless. Rather than relax the sandbox, the text is
/// extracted here and written beside the original as a .txt.
///
/// An .xlsx is a zip of XML parts, so this needs no third-party library: shared strings live in
/// xl/sharedStrings.xml and cell values in xl/worksheets/*.xml. Formatting, formulas and layout
/// are all discarded - the point is to convey what the sheet says, not to reproduce it.
/// </summary>
public static class XlsxText
{
    private const int MaxCharacters = 20_000;

    public static string Extract(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

            var shared = SharedStrings(zip);
            var sb = new StringBuilder();

            foreach (var sheet in zip.Entries
                         .Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                                     && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"# {sheet.FullName}");
                AppendSheet(sheet, shared, sb);
                sb.AppendLine();

                if (sb.Length > MaxCharacters) break;
            }

            var text = sb.ToString().Trim();
            if (text.Length > MaxCharacters)
                text = text[..MaxCharacters] + "\n\n[spreadsheet truncated]";

            return text;
        }
        catch (Exception)
        {
            // A corrupt or unexpected workbook is not worth failing an estimate over.
            return "";
        }
    }

    private static void AppendSheet(ZipArchiveEntry sheet, IReadOnlyList<string> shared, StringBuilder sb)
    {
        using var reader = sheet.Open();
        var doc = XDocument.Load(reader);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        foreach (var row in doc.Descendants(ns + "row"))
        {
            var values = new List<string>();

            foreach (var cell in row.Elements(ns + "c"))
            {
                var raw = cell.Element(ns + "v")?.Value;
                var type = cell.Attribute("t")?.Value;

                // t="s" means the value is an index into the shared string table; t="inlineStr"
                // carries the text inside the cell itself.
                var value = type switch
                {
                    "s" when int.TryParse(raw, out var i) && i >= 0 && i < shared.Count => shared[i],
                    "inlineStr" => string.Concat(cell.Descendants(ns + "t").Select(t => t.Value)),
                    _ => raw ?? ""
                };

                values.Add(value);
            }

            if (values.Any(v => v.Length > 0))
                sb.AppendLine(string.Join("\t", values));
        }
    }

    private static IReadOnlyList<string> SharedStrings(ZipArchive zip)
    {
        var entry = zip.Entries.FirstOrDefault(e =>
            e.FullName.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase));
        if (entry is null) return Array.Empty<string>();

        using var reader = entry.Open();
        var doc = XDocument.Load(reader);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        // A shared string can be split across several <t> runs when parts of it are styled.
        return doc.Root?.Elements(ns + "si")
            .Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value)))
            .ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();
    }
}
