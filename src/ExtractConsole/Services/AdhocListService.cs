using ExtractConsole.Config;
using ExtractConsole.Models;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ExtractConsole.Services;

/// <summary>
/// The ten "AdhocHARList - *.csv" files the staging packages read to re-pull specific
/// encounters, or encounter/transaction pairs, for data correction.
/// <para>
/// How they are used, as the team runs it: near the end of each daily extract, DEV step
/// 14 ("Generate the list of missing parent PB and HB encounters") REPLACES the HB and PB
/// Encounter lists with the missing parents it found, ready for the next day. When
/// Finance sends extra entries for correction, they are APPENDED to the end of the
/// relevant files, and the next day's extract picks up everything. This service does
/// exactly that append - plus removing an entry added by mistake - and nothing else. It
/// never replaces a list and never creates a file.
/// </para>
/// <para>
/// Not to be confused with \\ropeps013L01\ftp\AdhocHARList.csv in the ftp ROOT, which
/// PROD's "Mayo EPSi - Build AdHocHARList File" job writes. That is a different file and
/// nothing here touches it.
/// </para>
/// </summary>
public sealed partial class AdhocListService(EnvironmentConfig env)
{
    private readonly EnvironmentConfig _env = env;

    /// <summary>How many of the last entries the panel shows.</summary>
    public const int TailSize = 100;

    /// <summary>How many search matches are returned.</summary>
    private const int MaxMatches = 200;

    /// <summary>How many of the entries about to change are echoed back in a preview.</summary>
    private const int SampleSize = 50;

    private sealed record Column(string Name, Regex Pattern, string Rule, bool Optional = false);

    private static readonly Column HspAccountId = new("HSP_ACCOUNT_ID", TenDigits(), "10 digits");

    // The transaction is optional. Very often all that is known is the encounter, and an
    // encounter on its own is written "1234567890|" - the pipe kept, the transaction left
    // empty - so the file stays two columns throughout.
    private static readonly Column TxId = new("TX_ID", NineDigits(), "9 digits", Optional: true);
    private static readonly Column TdlId = new("TDL_ID", FifteenDigits(), "15 digits", Optional: true);

    private sealed record ListDef(string Key, string FileName, Column[] Columns);

    /// <summary>
    /// The contract with the staging packages: file names and column layouts are fixed by
    /// them, which is why these live here rather than in configuration. Both environments
    /// use the same names.
    /// </summary>
    private static readonly ListDef[] Lists =
    [
        new("HBCharge",    "AdhocHARList - HB Charge.csv",    [HspAccountId, TxId]),
        new("HBCPT",       "AdhocHARList - HB CPT.csv",       [HspAccountId]),
        new("HBEncounter", "AdhocHARList - HB Encounter.csv", [HspAccountId]),
        new("HBICD10Dx",   "AdhocHARList - HB ICD10Dx.csv",   [HspAccountId]),
        new("HBICD10Px",   "AdhocHARList - HB ICD10Px.csv",   [HspAccountId]),
        new("HBPayments",  "AdhocHARList - HB Payments.csv",  [HspAccountId, TxId]),
        new("PBCharge",    "AdhocHARList - PB Charge.csv",    [HspAccountId, TdlId]),
        new("PBEncounter", "AdhocHARList - PB Encounter.csv", [HspAccountId]),
        new("PBICD10Dx",   "AdhocHARList - PB ICD10Dx.csv",   [HspAccountId]),
        new("PBPayments",  "AdhocHARList - PB Payments.csv",  [HspAccountId, TdlId]),
    ];

    // ---- reading ------------------------------------------------------------

    public List<AdhocListInfo> ListAll() => [.. Lists.Select(Describe)];

    public AdhocListDetail Detail(string key, string? search)
    {
        var def = Find(key);
        var info = Describe(def);
        if (!info.Exists || info.Error is not null)
            return new AdhocListDetail(info, [], search, [], 0);

        var parsed = ReadFile(PathOf(def));
        var tail = parsed.Entries.Skip(Math.Max(0, parsed.Entries.Count - TailSize)).ToList();

        var term = search?.Trim();
        List<string> matches = [];
        var matchCount = 0;
        if (!string.IsNullOrEmpty(term))
        {
            var all = parsed.Entries.Where(e => e.Contains(term, StringComparison.Ordinal)).ToList();
            matchCount = all.Count;
            matches = [.. all.Take(MaxMatches)];
        }

        return new AdhocListDetail(info, tail, term, matches, matchCount);
    }

    private AdhocListInfo Describe(ListDef def)
    {
        var columns = def.Columns.Select(c => c.Name).ToList();
        var path = PathOf(def);
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists)
                return new AdhocListInfo(def.Key, def.FileName, columns, false, 0, 0, null, null,
                    "The file is not there. The staging package expects it; this console will not create it.");

            var parsed = ReadFile(path);
            return new AdhocListInfo(def.Key, def.FileName, columns, true, parsed.Entries.Count,
                fi.Length, fi.LastWriteTime, parsed.Header, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AdhocListInfo(def.Key, def.FileName, columns, false, 0, 0, null, null, ex.Message);
        }
    }

    // ---- changing -----------------------------------------------------------

    public AdhocChangeResult Append(string key, AdhocChangeRequest req) => Change(key, req, append: true);

    public AdhocChangeResult Remove(string key, AdhocChangeRequest req) => Change(key, req, append: false);

    private AdhocChangeResult Change(string key, AdhocChangeRequest req, bool append)
    {
        var mode = append ? "append" : "remove";
        var def = Find(key);
        var path = PathOf(def);

        var input = ParseInput(def, req);

        if (!File.Exists(path))
            return Fail(mode, $"{def.FileName} is not there. Nothing was changed, and the console does not create it.", input);

        // Nothing at all to act on. Distinct from "some lines were bad", which is reported
        // line by line below.
        if (input.Entries.Count == 0 && input.Rejected.Count == 0)
            return Fail(mode, "Nothing to " + mode + " - no entries were found in what was supplied.", input);

        // One exclusive open for the read, the decision and the write, so nothing can
        // change the file in between. If a staging package has it open this fails with
        // "in use", which is the right outcome.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var bytes = ReadAll(fs);
        var file = ParseFile(bytes);

        if (file.Header is null)
            return Fail(mode, $"{def.FileName} has no header line, which the staging package expects. Nothing was changed - fix the file first.", input);

        var existing = new HashSet<string>(file.Entries.Select(e => Normalise(def, e)), StringComparer.Ordinal);

        List<string> toChange;
        var alreadyPresent = 0;
        var notFound = 0;

        if (append)
        {
            toChange = [.. input.Entries.Where(e => !existing.Contains(e))];
            alreadyPresent = input.Entries.Count - toChange.Count;
        }
        else
        {
            toChange = [.. input.Entries.Where(existing.Contains)];
            notFound = input.Entries.Count - toChange.Count;
        }

        var before = file.Entries.Count;

        // Every line has to be valid before anything is written. A correction list that
        // half-loads is worse than one that does not load: nobody can tell which half.
        if (input.Rejected.Count > 0)
        {
            return new AdhocChangeResult(false,
                $"{input.Rejected.Count} line(s) are not valid for {def.FileName}. Fix them and try again - nothing was {(append ? "appended" : "removed")}.",
                mode, false, toChange.Count, [.. toChange.Take(SampleSize)], alreadyPresent,
                input.DuplicateInInput, notFound, input.HeaderSkipped, input.Rejected, before, before);
        }

        if (toChange.Count == 0)
        {
            var why = append
                ? "every entry is already in the list"
                : "none of those entries are in the list";
            return new AdhocChangeResult(false, $"Nothing to {mode} - {why}.", mode, false, 0, [],
                alreadyPresent, input.DuplicateInInput, notFound, input.HeaderSkipped, [], before, before);
        }

        if (!req.Commit)
        {
            return new AdhocChangeResult(true,
                $"Ready to {mode} {toChange.Count:N0} entr{(toChange.Count == 1 ? "y" : "ies")} {(append ? "to" : "from")} {def.FileName}.",
                mode, false, toChange.Count, [.. toChange.Take(SampleSize)], alreadyPresent,
                input.DuplicateInInput, notFound, input.HeaderSkipped, [], before,
                append ? before + toChange.Count : before - toChange.Count);
        }

        if (append) WriteAppend(fs, bytes, toChange);
        else WriteRemove(fs, def, file, toChange);

        var after = append ? before + toChange.Count : before - toChange.Count;
        return new AdhocChangeResult(true,
            $"{(append ? "Appended" : "Removed")} {toChange.Count:N0} entr{(toChange.Count == 1 ? "y" : "ies")} {(append ? "to" : "from")} {def.FileName}. It now holds {after:N0}.",
            mode, true, toChange.Count, [.. toChange.Take(SampleSize)], alreadyPresent,
            input.DuplicateInInput, notFound, input.HeaderSkipped, [], before, after);
    }

    /// <summary>
    /// Appends at the end, as the team does by hand. Written the way step 14 writes these
    /// files - ASCII, CRLF between lines, no line ending after the last - and the header
    /// and every existing line are left byte for byte as they were.
    /// </summary>
    private static void WriteAppend(FileStream fs, byte[] existing, List<string> entries)
    {
        var endsWithNewline = existing.Length > 0 && existing[^1] == (byte)'\n';
        var payload = (endsWithNewline ? "" : "\r\n") + string.Join("\r\n", entries);

        fs.Seek(0, SeekOrigin.End);
        var data = Encoding.ASCII.GetBytes(payload);
        fs.Write(data, 0, data.Length);
        fs.Flush(true);
    }

    /// <summary>
    /// Rewrites the file without the removed entries. The header line is kept exactly as
    /// it was - its case varies between files and the packages do not care, so neither
    /// should this.
    /// </summary>
    private static void WriteRemove(FileStream fs, ListDef def, ParsedFile file, List<string> entries)
    {
        var drop = new HashSet<string>(entries, StringComparer.Ordinal);
        var kept = file.Entries.Where(e => !drop.Contains(Normalise(def, e)));

        var content = string.Join("\r\n", new[] { file.Header! }.Concat(kept));
        var data = Encoding.ASCII.GetBytes(content);

        fs.SetLength(0);
        fs.Seek(0, SeekOrigin.Begin);
        fs.Write(data, 0, data.Length);
        fs.Flush(true);
    }

    // ---- parsing what was supplied -------------------------------------------

    private sealed record ParsedInput(
        List<string> Entries, List<AdhocRejected> Rejected, int DuplicateInInput, string? HeaderSkipped);

    /// <summary>
    /// Accepts typed or pasted lines, an uploaded text file, or an uploaded .xlsx.
    /// <para>
    /// Fields may be separated by a pipe, a tab or a comma. Pipe is the file format; tab is
    /// what you get pasting cells from Excel; comma is what Excel's "Save as CSV" writes.
    /// The values are digits only, so none of the three can be ambiguous. What is
    /// written is always pipe-delimited.
    /// </para>
    /// <para>
    /// A header is optional. If the first row holds no number at all it is taken as a
    /// header and skipped - and the result says so, rather than doing it silently.
    /// </para>
    /// </summary>
    private static ParsedInput ParseInput(ListDef def, AdhocChangeRequest req)
    {
        List<(int Line, List<string> Fields)> rows;

        if (!string.IsNullOrEmpty(req.FileBase64))
        {
            var name = req.FileName ?? "";
            if (name.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))
                return new ParsedInput([], [new AdhocRejected(0, name,
                    "Old-format .xls is not supported. Save it as .xlsx or as CSV and upload that.")], 0, null);

            byte[] bytes;
            try { bytes = Convert.FromBase64String(req.FileBase64); }
            catch (FormatException)
            {
                return new ParsedInput([], [new AdhocRejected(0, name, "The upload could not be read.")], 0, null);
            }

            try { rows = ReadXlsx(bytes); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or System.Xml.XmlException or InvalidOperationException)
            {
                return new ParsedInput([], [new AdhocRejected(0, name,
                    "Not a readable .xlsx workbook: " + ex.Message)], 0, null);
            }
        }
        else
        {
            var lines = (req.Text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            rows = [];
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim().TrimStart('\uFEFF');
                if (line.Length == 0) continue;
                rows.Add((i + 1, [.. DelimiterRegex().Split(line).Select(f => f.Trim())]));
            }
        }

        // Drop trailing empty cells - an Excel row often carries a blank column or two.
        rows = [.. rows
            .Select(r => (r.Line, Fields: TrimTrailingEmpty(r.Fields)))
            .Where(r => r.Fields.Count > 0)];

        string? headerSkipped = null;
        if (rows.Count > 0 && rows[0].Fields.All(f => !DigitsOnlyRegex().IsMatch(f)))
        {
            headerSkipped = string.Join("|", rows[0].Fields);
            rows.RemoveAt(0);
        }

        var entries = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rejected = new List<AdhocRejected>();
        var dupes = 0;

        var required = def.Columns.Count(c => !c.Optional);

        foreach (var (line, fields) in rows)
        {
            var shown = string.Join("|", fields);

            if (fields.Count < required || fields.Count > def.Columns.Length)
            {
                var expected = string.Join("|", def.Columns.Select(c => c.Optional ? $"[{c.Name}]" : c.Name));
                rejected.Add(new AdhocRejected(line, shown,
                    $"Expected {expected}, found {fields.Count} value{(fields.Count == 1 ? "" : "s")}."));
                continue;
            }

            // An optional column left empty is fine; one that is filled in must be right.
            var bad = def.Columns
                .Select((c, i) => (c, v: i < fields.Count ? fields[i] : ""))
                .FirstOrDefault(x => !(x.c.Optional && x.v.Length == 0) && !x.c.Pattern.IsMatch(x.v));
            if (bad.c is not null)
            {
                rejected.Add(new AdhocRejected(line, shown,
                    $"{bad.c.Name} must be {bad.c.Rule}" + (bad.v.Length == 0 ? " - it is empty." : $" - \"{bad.v}\" is {Describe(bad.v)}.")));
                continue;
            }

            // "1234567890" typed into a two-column list becomes "1234567890|".
            var entry = Normalise(def, string.Join("|", fields));
            if (!seen.Add(entry)) { dupes++; continue; }
            entries.Add(entry);
        }

        return new ParsedInput(entries, rejected, dupes, headerSkipped);
    }

    private static string Describe(string v) =>
        DigitsOnlyRegex().IsMatch(v) ? $"{v.Length} digits" : "not a number";

    private static List<string> TrimTrailingEmpty(List<string> fields)
    {
        var n = fields.Count;
        while (n > 0 && fields[n - 1].Length == 0) n--;
        return n == fields.Count ? fields : fields.GetRange(0, n);
    }

    /// <summary>
    /// Reads the first worksheet of an .xlsx. An .xlsx is a zip of XML, so this needs no
    /// library; only the parts a list of IDs can involve are handled.
    /// <para>
    /// Excel holds numbers as numbers, so a 15-digit TDL_ID typed into a cell comes back as
    /// 1.23456789012345E+14. That is converted back to its digits - exactly, since 15
    /// digits is within Excel's precision. A number that lost a leading zero, or that has
    /// a fraction, comes back the wrong length and is rejected rather than "fixed".
    /// Line numbers reported are the Excel row numbers.
    /// </para>
    /// </summary>
    private static List<(int Line, List<string> Fields)> ReadXlsx(byte[] bytes)
    {
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace pkg = "http://schemas.openxmlformats.org/package/2006/relationships";

        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        XDocument Load(string path) =>
            XDocument.Load((zip.GetEntry(path) ?? throw new InvalidDataException($"missing {path}")).Open());

        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is not null)
        {
            foreach (var si in Load("xl/sharedStrings.xml").Root!.Elements(main + "si"))
                shared.Add(string.Concat(si.Descendants(main + "t").Select(t => t.Value)));
        }

        var sheet = Load("xl/workbook.xml").Descendants(main + "sheet").FirstOrDefault()
                    ?? throw new InvalidDataException("the workbook has no worksheets");
        var rid = (string?)sheet.Attribute(rel + "id") ?? throw new InvalidDataException("worksheet has no relationship id");

        var target = Load("xl/_rels/workbook.xml.rels").Root!
            .Elements(pkg + "Relationship")
            .FirstOrDefault(r => (string?)r.Attribute("Id") == rid)?.Attribute("Target")?.Value
            ?? throw new InvalidDataException("worksheet relationship not found");
        var sheetPath = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;

        var result = new List<(int, List<string>)>();
        var rowCounter = 0;

        foreach (var row in Load(sheetPath).Descendants(main + "row"))
        {
            rowCounter++;
            var line = int.TryParse((string?)row.Attribute("r"), out var r) ? r : rowCounter;

            var cells = new SortedDictionary<int, string>();
            var next = 0;
            foreach (var c in row.Elements(main + "c"))
            {
                var col = ColumnIndex((string?)c.Attribute("r")) ?? next;
                next = col + 1;

                var type = (string?)c.Attribute("t");
                var v = c.Element(main + "v")?.Value;
                string value = type switch
                {
                    "s" when int.TryParse(v, out var i) && i >= 0 && i < shared.Count => shared[i],
                    "inlineStr" => string.Concat(c.Descendants(main + "t").Select(t => t.Value)),
                    "str" or "b" or "e" => v ?? "",
                    _ => NumberToDigits(v)
                };
                cells[col] = value.Trim();
            }

            if (cells.Count == 0) continue;
            var width = cells.Keys.Max() + 1;
            var fields = Enumerable.Range(0, width).Select(i => cells.GetValueOrDefault(i, "")).ToList();
            result.Add((line, fields));
        }
        return result;
    }

    private static string NumberToDigits(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        if (decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d == decimal.Truncate(d))
            return d.ToString("0", CultureInfo.InvariantCulture);
        return v;
    }

    /// <summary>"B12" -> 1. Null when the reference is missing or malformed.</summary>
    private static int? ColumnIndex(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return null;
        var n = 0;
        var any = false;
        foreach (var ch in cellRef)
        {
            if (ch is >= 'A' and <= 'Z') { n = n * 26 + (ch - 'A' + 1); any = true; }
            else break;
        }
        return any ? n - 1 : null;
    }

    // ---- the file itself -------------------------------------------------------

    private sealed record ParsedFile(string? Header, List<string> Entries);

    private static ParsedFile ReadFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return ParseFile(ReadAll(fs));
    }

    /// <summary>
    /// First line is the header; every non-blank line after it is an entry. Decoded as
    /// UTF-8 - a superset of the ASCII these are written in - with any BOM dropped.
    /// </summary>
    private static ParsedFile ParseFile(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        var lines = text.Replace("\r\n", "\n").Split('\n');

        var firstNonBlank = Array.FindIndex(lines, l => l.Trim().Length > 0);
        if (firstNonBlank < 0) return new ParsedFile(null, []);

        var header = lines[firstNonBlank].TrimEnd('\r');
        var entries = lines.Skip(firstNonBlank + 1)
                           .Select(l => l.Trim())
                           .Where(l => l.Length > 0)
                           .ToList();
        return new ParsedFile(header, entries);
    }

    private static byte[] ReadAll(FileStream fs)
    {
        fs.Seek(0, SeekOrigin.Begin);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// The canonical form of an entry: fields trimmed, and padded to the list's full column
    /// count. So in a two-column list "1234567890", "1234567890|" and " 1234567890 | " are
    /// all "1234567890|" - one entry, whichever way it was typed or already sits in the file.
    /// "1234567890|111111111" is a different entry: that one transaction, not the encounter.
    /// </summary>
    private static string Normalise(ListDef def, string entry)
    {
        var fields = entry.Split('|').Select(f => f.Trim()).ToList();
        while (fields.Count > def.Columns.Length && fields[^1].Length == 0) fields.RemoveAt(fields.Count - 1);
        while (fields.Count < def.Columns.Length) fields.Add("");
        return string.Join("|", fields);
    }

    private ListDef Find(string key) =>
        Lists.FirstOrDefault(l => string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Unknown AdHoc list '{key}'.");

    private string PathOf(ListDef def)
    {
        if (string.IsNullOrWhiteSpace(_env.FileShare))
            throw new InvalidOperationException($"Environment '{_env.Name}' has no FileShare configured.");
        return Path.Combine(_env.FileShare, def.FileName);
    }

    private static AdhocChangeResult Fail(string mode, string message, ParsedInput input) =>
        new(false, message, mode, false, 0, [], 0, input.DuplicateInInput, 0, input.HeaderSkipped,
            input.Rejected, 0, 0);

    [GeneratedRegex(@"^\d{10}$")] private static partial Regex TenDigits();
    [GeneratedRegex(@"^\d{9}$")] private static partial Regex NineDigits();
    [GeneratedRegex(@"^\d{15}$")] private static partial Regex FifteenDigits();
    [GeneratedRegex(@"^\d+$")] private static partial Regex DigitsOnlyRegex();
    [GeneratedRegex(@"[|\t,]")] private static partial Regex DelimiterRegex();
}
