using ExtractConsole.Config;
using ExtractConsole.Models;
using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ExtractConsole.Services;

/// <summary>
/// Reconciles what landed in the ODS recon tables against what was written to the file
/// share. The extract itself already produces a per-file line count
/// (MayoClinic_filecounts_MMddyyyy.txt, written by step 9) - reading that is far cheaper
/// than re-counting 300 MB files, so the file side comes from there.
/// </summary>
public sealed partial class ReconService(EnvironmentConfig env)
{
    private readonly EnvironmentConfig _env = env;
    private readonly Db _db = new(env);

    public async Task<List<ReconCount>> GetCountsAsync(
        DateTime forDate, bool exact, bool countRecords, CancellationToken ct)
    {
        if (_env.ReconTables.Count == 0) return [];

        var tableRows = await GetTableRowsAsync(exact, ct);
        var fileLines = ReadFileCounts(forDate);
        var stamp = forDate.ToString("MMddyyyy", CultureInfo.InvariantCulture);
        var onShare = ListShare(stamp);

        var results = new List<ReconCount>();
        foreach (var t in _env.ReconTables)
        {
            long? rows = tableRows.TryGetValue($"{t.Schema}.{t.Table}", out var r) ? r : null;

            var single = t.FilePattern.Replace("{date}", stamp);

            // One file, or the numbered parts it was split into - in whichever of the three
            // sources have it. Parts win where both exist: a split replaces the single file.
            var counted = ResolveFiles(single, fileLines.Keys);
            var present = ResolveFiles(single, onShare.Keys);

            // The counts file already excludes the header row - of EACH part, since every
            // part carries its own header - so the sum is directly comparable to the table.
            // Verified: HB Charge 10/19, parts f1-f6 sum to 17,955,188, the staging count.
            // Missing from the summary is unknown, not zero: zero would read as "the extract
            // produced nothing".
            long? dataLines = counted.Names.Count > 0 ? counted.Names.Sum(n => fileLines[n].Lines) : null;
            long? sizeKb = counted.Names.Count > 0 ? counted.Names.Sum(n => fileLines[n].SizeKb) : null;

            DateTime? modified = present.Names.Count > 0 ? present.Names.Max(n => onShare[n]) : null;

            long? records = countRecords && present.Names.Count > 0
                ? CountRecordsInFiles(present.Names, ct)
                : null;

            // Describe what was found, and anything off about it - a missing part means
            // records are missing, and the comparison will show it as short.
            var files = present.Names.Count > 0 ? present : counted;
            var fileName = files.Display ?? single;
            var note = files.Gap ?? (present.Parts > 0 && counted.Parts > 0 && present.Parts != counted.Parts
                ? $"The counts file lists {counted.Parts} part(s) but {present.Parts} are on the share."
                : null);

            // Records answer "did every row reach the file". Lines answer "is the file
            // well formed". They are different questions and both have to be reported:
            // reconciling the records and then calling a file with split records "Match"
            // would retire the very defect that needs fixing.
            var comparable = records ?? dataLines;
            long? diff = rows is not null && comparable is not null ? rows - comparable : null;
            long? continuations = records is not null && dataLines is not null
                ? dataLines - records
                : null;

            results.Add(new ReconCount(
                Key: t.Key,
                TableName: $"{t.Schema}.{t.Table}",
                TableRows: rows,
                TableRowsExact: exact,
                FileName: fileName,
                FileLines: dataLines,
                FileRecords: records,
                ContinuationLines: continuations,
                FileSizeKb: sizeKb,
                FileModifiedLocal: modified,
                Difference: diff,
                Status: Verdict(rows, dataLines, records, continuations, diff))
            {
                FileParts = files.Parts,
                FileNote = note
            });
        }
        return results;
    }

    // ---- single file or split parts ----------------------------------------------

    /// <summary>
    /// What one dataset was written to on a date. Parts is 0 for an unsplit file, else the
    /// number of parts found. Display is the name to show: the file itself, or the run of
    /// parts ("MayoClinic_HBCharge_10192026_f1-f6.txt"). Gap describes missing parts.
    /// </summary>
    private sealed record ResolvedFiles(List<string> Names, int Parts, string? Display, string? Gap);

    /// <summary>
    /// A file too big to send is split into numbered parts - 1 GB is the limit, and HB
    /// Charge hit it on 2026-10-18 at ~18 million rows. "MayoClinic_HBCharge_10192026.txt"
    /// becomes "..._10192026_f1.txt" to "..._f6.txt", each a complete file with its own
    /// header, cut at a line boundary. Any dataset can do this, so every one is resolved
    /// the same way: the parts if there are any, otherwise the single file.
    /// </summary>
    private static ResolvedFiles ResolveFiles(string single, IEnumerable<string> available)
    {
        var names = available as ICollection<string> ?? available.ToList();

        var ext = Path.GetExtension(single);
        var stem = single[..^ext.Length];
        var partRx = new Regex("^" + Regex.Escape(stem) + @"_f(\d+)" + Regex.Escape(ext) + "$",
                               RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var parts = names
            .Select(n => (Name: n, M: partRx.Match(n)))
            .Where(x => x.M.Success)
            .Select(x => (x.Name, N: int.Parse(x.M.Groups[1].Value, CultureInfo.InvariantCulture)))
            .OrderBy(x => x.N)
            .ToList();

        if (parts.Count == 0)
        {
            var hit = names.FirstOrDefault(n => string.Equals(n, single, StringComparison.OrdinalIgnoreCase));
            return hit is null
                ? new ResolvedFiles([], 0, null, null)
                : new ResolvedFiles([hit], 0, hit, null);
        }

        var numbers = parts.Select(p => p.N).ToList();
        var max = numbers.Max();
        var missing = Enumerable.Range(1, max).Except(numbers).ToList();
        string? gap = missing.Count > 0
            ? $"Part{(missing.Count == 1 ? "" : "s")} {string.Join(", ", missing.Select(m => "f" + m))} not found - "
              + "those records are missing from the count."
            : null;

        var display = parts.Count == 1
            ? parts[0].Name
            : $"{stem}_f{numbers.Min()}-f{max}{ext}";

        return new ResolvedFiles([.. parts.Select(p => p.Name)], parts.Count, display, gap);
    }

    /// <summary>
    /// Every data file on the share for the date, with its last-write time, in one listing.
    /// One directory read rather than a probe per dataset per possible part.
    /// </summary>
    private Dictionary<string, DateTime> ListShare(string stamp)
    {
        var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(_env.FileShare)) return map;
        try
        {
            foreach (var fi in new DirectoryInfo(_env.FileShare).EnumerateFiles($"*{stamp}*"))
                map[fi.Name] = fi.LastWriteTime;
        }
        catch (IOException) { /* share unreachable - reported by the files panel */ }
        catch (UnauthorizedAccessException) { }
        return map;
    }

    /// <summary>
    /// Row counts for every configured recon table in one round trip. Metadata counts
    /// (sys.dm_db_partition_stats) are the default because COUNT(*) over these tables scans
    /// hundreds of millions of rows; <paramref name="exact"/> opts into the real count.
    /// </summary>
    private async Task<Dictionary<string, long>> GetTableRowsAsync(bool exact, CancellationToken ct)
    {
        await using var cn = _db.Data();
        await cn.OpenAsync(ct);

        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        if (!exact)
        {
            const string sql = """
                SELECT s.name + '.' + t.name, SUM(p.row_count)
                FROM sys.dm_db_partition_stats AS p
                JOIN sys.tables AS t ON t.object_id = p.object_id
                JOIN sys.schemas AS s ON s.schema_id = t.schema_id
                WHERE p.index_id IN (0, 1)
                GROUP BY s.name, t.name;
                """;
            await using var cmd = _db.Cmd(cn, sql);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                result[r.GetString(0)] = r.GetInt64(1);
            return result;
        }

        // Exact: one parameterised COUNT per table, names validated against config so the
        // dynamic SQL can only ever name a table this app was configured with.
        foreach (var t in _env.ReconTables)
        {
            var key = $"{t.Schema}.{t.Table}";
            var sql = $"SELECT COUNT_BIG(*) FROM {Quote(t.Schema)}.{Quote(t.Table)};";
            await using var cmd = _db.Cmd(cn, sql);
            cmd.CommandTimeout = Math.Max(_env.CommandTimeoutSeconds, 300);
            try
            {
                var v = await cmd.ExecuteScalarAsync(ct);
                if (v is not null and not DBNull) result[key] = Convert.ToInt64(v);
            }
            catch (Microsoft.Data.SqlClient.SqlException)
            {
                // Missing or unreadable table shows as "unknown" rather than failing the panel.
            }
        }
        return result;
    }

    private static string Quote(string identifier)
    {
        if (identifier.Contains(']')) identifier = identifier.Replace("]", "]]");
        return $"[{identifier}]";
    }

    /// <summary>
    /// The reconciliation verdict.
    /// <para>
    /// Deliberately does NOT normalise embedded CR/LF away. Counting records rather than
    /// lines answers whether any data was lost - it was not - but a file whose records
    /// span several physical lines is still malformed for anything that reads it a line
    /// at a time, which is how a flat-file consumer normally reads it. So a file with
    /// continuation lines stays amber even when its record count reconciles exactly: the
    /// row count is reconciled, the defect is not fixed.
    /// </para>
    /// </summary>
    private static string Verdict(long? rows, long? lines, long? records, long? continuations, long? diff)
    {
        if (diff is null) return ReconStatus.Unknown;

        // Zero reconciles against zero. Checked before everything else, because an
        // extract that produced nothing otherwise reports a flawless match - which is
        // what happened on PROD 2026-10-08, when every step succeeded, staging came back
        // empty, and header-only files went to Strata under eleven green ticks.
        if (rows == 0) return ReconStatus.Empty;

        // No record count taken: the comparison is line-based and cannot tell a lost
        // record from a split one. More lines than rows still cannot mean lost data, so
        // that direction is the embedded-break signature and must be tested before the
        // generic disagreement check - otherwise it reports as a shortfall.
        if (records is null)
        {
            if (rows is not null && lines > rows) return ReconStatus.ExtraLines;
            return diff == 0 ? ReconStatus.Match : ReconStatus.Short;
        }

        // Records counted, so this is authoritative on whether any data is missing.
        // Checked before formedness: a file can be both short AND split, and short is
        // the graver of the two.
        if (diff != 0) return ReconStatus.Short;

        // Records reconcile - but is the file itself well formed?
        if (continuations is > 0) return ReconStatus.ExtraLines;

        return ReconStatus.Match;
    }

    /// <summary>
    /// Counts data records by streaming the file and counting lines that begin a record.
    /// <para>
    /// The extract's own counting step counts physical lines, which overstates the record
    /// count whenever a field holds an embedded CR/LF - on 2026-10-06 the DEV PB Encounter
    /// file had 397,071 lines but exactly 395,733 records, matching its table precisely.
    /// Reading the file is the only way to tell those apart, so it is done on request
    /// rather than on every refresh: these files run to hundreds of megabytes.
    /// </para>
    /// </summary>
    /// <param name="fileNames">The single file, or every part of a split one.</param>
    private long? CountRecordsInFiles(IReadOnlyList<string> fileNames, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_env.FileShare) || fileNames.Count == 0) return null;

        Regex rx;
        try { rx = new Regex(_env.RecordStartPattern, RegexOptions.CultureInvariant); }
        catch (ArgumentException) { return null; }   // bad pattern in config

        long total = 0;
        foreach (var name in fileNames)
        {
            // All or nothing: a total missing one part would read as missing records.
            var one = CountRecordsInOneFile(Path.Combine(_env.FileShare, name), rx, ct);
            if (one is null) return null;
            total += one.Value;
        }
        return total;
    }

    private static long? CountRecordsInOneFile(string path, Regex rx, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path)) return null;

            long records = 0;
            var first = true;
            using var reader = new StreamReader(path);
            while (reader.ReadLine() is { } line)
            {
                ct.ThrowIfCancellationRequested();
                if (first) { first = false; continue; }   // header - every split part has its own
                if (rx.IsMatch(line)) records++;
            }
            return records;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Per-file line counts for the run, preferring the summary file and falling back to
    /// the counting step's own log.
    /// </summary>
    private Dictionary<string, (long SizeKb, long Lines)> ReadFileCounts(DateTime forDate)
    {
        var fromSummary = ReadCountsSummary(forDate);
        return fromSummary.Count > 0 ? fromSummary : ReadCountsFromExecutionLog(forDate);
    }

    /// <summary>Parses the pipe-delimited File|SizeKB|Lines summary the extract writes.</summary>
    private Dictionary<string, (long SizeKb, long Lines)> ReadCountsSummary(DateTime forDate)
    {
        var map = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(_env.FileShare)) return map;

        var path = Path.Combine(_env.FileShare, $"MayoClinic_filecounts_{forDate:MMddyyyy}.txt");

        try
        {
            if (!File.Exists(path)) return map;
            foreach (var line in File.ReadLines(path).Skip(1))
            {
                var parts = line.Split('|');
                if (parts.Length < 3) continue;
                if (!long.TryParse(parts[1], out var kb)) continue;
                if (!long.TryParse(parts[2], out var lines)) continue;
                map[parts[0].Trim()] = (kb, lines);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return map;
    }

    /// <summary>
    /// Fallback for environments where the output files are collected off the share
    /// shortly after they are written - PROD, where the CLEO/FTP pickup removes both the
    /// data files and the counts summary. The counting step's own append-only log
    /// survives and carries the same numbers:
    ///
    ///   [2026-10-05 08:38:29] [INFO] Processing file: MayoClinic_HBCharge_10052026.txt
    ///   [2026-10-05 08:38:29] [INFO]  -> Calculated: 37 KB, 939 Lines. Appending to ...
    ///
    /// Entries are matched on the dated file name rather than the timestamp, so a log
    /// spanning many days needs no date arithmetic.
    /// </summary>
    private Dictionary<string, (long SizeKb, long Lines)> ReadCountsFromExecutionLog(DateTime forDate)
    {
        var map = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(_env.FileShare)) return map;

        var path = Path.Combine(_env.FileShare, "FileSizes_RecordCounts_ExecutionLog.txt");
        var stamp = forDate.ToString("MMddyyyy", CultureInfo.InvariantCulture);

        try
        {
            if (!File.Exists(path)) return map;

            string? pending = null;
            foreach (var line in File.ReadLines(path))
            {
                var processing = ProcessingFileRegex().Match(line);
                if (processing.Success)
                {
                    var name = processing.Groups[1].Value.Trim();
                    pending = name.Contains(stamp, StringComparison.OrdinalIgnoreCase) ? name : null;
                    continue;
                }

                if (pending is null) continue;

                var calc = CalculatedRegex().Match(line);
                if (!calc.Success) continue;

                if (long.TryParse(calc.Groups[1].Value, out var kb) &&
                    long.TryParse(calc.Groups[2].Value, out var lines))
                {
                    // Last writer wins: a re-run on the same date appends fresh entries.
                    map[pending] = (kb, lines);
                }
                pending = null;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return map;
    }

    [GeneratedRegex(@"Processing file:\s*(\S.*?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessingFileRegex();

    [GeneratedRegex(@"Calculated:\s*(\d+)\s*KB,\s*(\d+)\s*Lines", RegexOptions.IgnoreCase)]
    private static partial Regex CalculatedRegex();
}
