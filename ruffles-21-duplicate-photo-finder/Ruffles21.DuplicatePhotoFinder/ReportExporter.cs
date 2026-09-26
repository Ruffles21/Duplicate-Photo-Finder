using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Ruffles21.DuplicatePhotoFinder;

public sealed record ReportRow(int Group, string Path, long Size, DateTime CreatedUtc,
    DateTime ModifiedUtc, string Status, string Sha256);

public static class ReportExporter
{
    public static List<ReportRow> Snapshot(IEnumerable<Group> groups) => groups.SelectMany(g =>
        g.Photos.Select(p => new ReportRow(g.Id, p.Path, p.Size, p.Created, p.Modified, p.Status, p.Hash))).ToList();

    public static void Export(string path, IReadOnlyList<ReportRow> rows, string format)
    {
        string extension = format == "CSV" ? ".csv" : format == "JSON" ? ".json" : throw new ArgumentException("Unsupported report format.");
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Save this report with the {extension} extension. Media files cannot be used as report destinations.");
        if (rows.Any(row => string.Equals(path, row.Path, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The report cannot replace a scanned media file.");

        string content;
        if (format == "JSON")
            content = JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true });
        else
        {
            var csv = new StringBuilder("Group,Path,Size,CreatedUTC,ModifiedUTC,Status,SHA256\r\n");
            foreach (var row in rows)
                csv.AppendLine(string.Join(",", new object[] { row.Group, row.Path, row.Size,
                    row.CreatedUtc.ToString("O"), row.ModifiedUtc.ToString("O"), row.Status, row.Sha256 }.Select(QuoteCsv)));
            content = csv.ToString();
        }
        // Atomic replacement does not write through a hard link to a media file.
        AtomicFile.WriteText(path, content, format == "CSV");
    }

    public static string QuoteCsv(object value)
    {
        string text = value.ToString() ?? "";
        if (text.Length > 0 && "=+-@\t\r\n".Contains(text[0]))
            text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}

public static class ResultFilter
{
    public static bool Matches(IEnumerable<Photo> photos, string query, int mediaScope)
    {
        query = query.Trim();
        return photos.Any(p => (mediaScope == 0 || (mediaScope == 2) == p.IsVideo)
            && (query.Length == 0 || p.Path.Contains(query, StringComparison.OrdinalIgnoreCase)));
    }
}
