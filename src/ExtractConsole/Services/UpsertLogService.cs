using ExtractConsole.Config;
using ExtractConsole.Models;
using System.Data;
using System.Text.RegularExpressions;

namespace ExtractConsole.Services;

/// <summary>
/// Reads STRATA_AUDIT.dbo.DailyReconCurrentProcessLog - the record of what the extract's
/// "Upsert Process for Recon_Current Tables" step did, one row per dataset per day.
/// <para>
/// The whole table is kept and shown. It is written by eleven Daily_Upsert_* procedures
/// and purged by nothing, so it grows at eleven rows a day - about four thousand a year,
/// which is not worth trimming. Keeping it matters: the only failures it has ever
/// recorded are historical, and the update/insert split it carries is meaningless
/// except against previous days.
/// </para>
/// <para>
/// RowsInSource duplicates what the reconciliation panel already shows - verified
/// identical on 2026-10-07 across all eleven datasets. The value here is the rest:
/// how the total divided into updates and inserts, when each dataset finished, and the
/// error text when one did not.
/// </para>
/// </summary>
public sealed partial class UpsertLogService(EnvironmentConfig env)
{
    private readonly EnvironmentConfig _env = env;
    private readonly Db _db = new(env);

    private const string Table = "dbo.DailyReconCurrentProcessLog";

    /// <summary>How many days of split history to chart per dataset.</summary>
    private const int TrendDays = 14;

    public async Task<UpsertLogReport> GetAsync(DateTime? forDate, CancellationToken ct)
    {
        await using var cn = _db.Data(_env.AuditDatabase);
        await cn.OpenAsync(ct);

        var days = await GetDaysAsync(cn, ct);

        // Default to the newest day present rather than today: DEV runs ad hoc and the
        // table has gaps, so "today" is very often empty and would read as a fault.
        var day = forDate?.Date ?? days.FirstOrDefault()?.Date ?? default;

        var rows = days.Count == 0 ? [] : await GetRowsAsync(cn, day, ct);
        var trends = await GetTrendsAsync(cn, ct);

        var total = await ScalarLongAsync(cn, $"SELECT COUNT_BIG(*) FROM {Table};", ct);

        return new UpsertLogReport(
            ProcessDate: days.Count == 0 ? null : day,
            AvailableDays: days,
            Rows: rows,
            // Taken from the day's own rows rather than queried across all dates.
            Failures: [.. rows.Where(r => r.Outcome != Outcome.Succeeded)],
            Trends: trends,
            DistinctDays: days.Count,
            TotalRows: total);
    }

    /// <summary>
    /// The selectable days, each with its row and failure counts. The failure count is
    /// what lets the picker mark a bad day without the panel having to display another
    /// day's failures to make them findable.
    /// </summary>
    private async Task<List<UpsertDay>> GetDaysAsync(
        Microsoft.Data.SqlClient.SqlConnection cn, CancellationToken ct)
    {
        // Not "RowCount" - ROWCOUNT is a reserved keyword and the batch will not parse.
        var sql = $"""
            SELECT ProcessDate,
                   COUNT(*) AS DayRows,
                   SUM(CASE WHEN ProcessStatus <> 'SUCCESS' THEN 1 ELSE 0 END) AS FailedRows
            FROM {Table}
            GROUP BY ProcessDate
            ORDER BY ProcessDate DESC;
            """;
        await using var cmd = _db.Cmd(cn, sql);
        await using var r = await cmd.ExecuteReaderAsync(ct);

        var list = new List<UpsertDay>();
        while (await r.ReadAsync(ct))
            list.Add(new UpsertDay(r.GetDateTime(0), r.GetInt32(1), r.GetInt32(2)));
        return list;
    }

    private async Task<List<UpsertLogRow>> GetRowsAsync(
        Microsoft.Data.SqlClient.SqlConnection cn, DateTime day, CancellationToken ct)
    {
        var sql = $"""
            SELECT ProcessLogID, ProcessDate, ProcessName, ProcessStatus,
                   RowsInSource, RowsUpdated, RowsInserted, TotalRowsProcessed,
                   CurrentStateUpdatedDateTime, ErrorMessage
            FROM {Table}
            WHERE ProcessDate = @day
            ORDER BY ProcessName, ProcessLogID;
            """;

        await using var cmd = _db.Cmd(cn, sql);
        cmd.Parameters.Add("@day", SqlDbType.Date).Value = day;
        return await ReadRowsAsync(cmd, ct);
    }

    private async Task<List<UpsertTrend>> GetTrendsAsync(
        Microsoft.Data.SqlClient.SqlConnection cn, CancellationToken ct)
    {
        // One row per dataset per day. Where a day retried, the last attempt wins - that
        // is the state the dataset ended the day in.
        var sql = $"""
            WITH ranked AS (
                SELECT ProcessName, ProcessDate, RowsUpdated, RowsInserted,
                       TotalRowsProcessed, ProcessStatus,
                       ROW_NUMBER() OVER (PARTITION BY ProcessName, ProcessDate
                                          ORDER BY ProcessLogID DESC) AS rn,
                       DENSE_RANK() OVER (ORDER BY ProcessDate DESC) AS dayRank
                FROM {Table}
            )
            SELECT ProcessName, ProcessDate, RowsUpdated, RowsInserted,
                   TotalRowsProcessed, ProcessStatus
            FROM ranked
            WHERE rn = 1 AND dayRank <= @days
            ORDER BY ProcessName, ProcessDate DESC;
            """;

        await using var cmd = _db.Cmd(cn, sql);
        cmd.Parameters.Add("@days", SqlDbType.Int).Value = TrendDays;
        await using var r = await cmd.ExecuteReaderAsync(ct);

        var byProcess = new Dictionary<string, List<UpsertTrendPoint>>(StringComparer.OrdinalIgnoreCase);
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            if (!byProcess.TryGetValue(name, out var points))
            {
                points = [];
                byProcess[name] = points;
            }
            points.Add(new UpsertTrendPoint(
                ProcessDate: r.GetDateTime(1),
                RowsUpdated: Convert.ToInt64(r.GetValue(2)),
                RowsInserted: Convert.ToInt64(r.GetValue(3)),
                TotalRowsProcessed: Convert.ToInt64(r.GetValue(4)),
                Outcome: MapOutcome(r.GetString(5))));
        }

        return [.. byProcess.Select(kv => new UpsertTrend(kv.Key, DatasetKey(kv.Key), kv.Value))
                            .OrderBy(t => t.ProcessName, StringComparer.OrdinalIgnoreCase)];
    }

    private static async Task<List<UpsertLogRow>> ReadRowsAsync(
        Microsoft.Data.SqlClient.SqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<UpsertLogRow>();

        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(2);
            var raw = r.GetString(3);
            rows.Add(new UpsertLogRow(
                ProcessLogId: Convert.ToInt64(r.GetValue(0)),
                ProcessDate: r.GetDateTime(1),
                ProcessName: name,
                DatasetKey: DatasetKey(name),
                Outcome: MapOutcome(raw),
                RawStatus: raw,
                RowsInSource: Convert.ToInt64(r.GetValue(4)),
                RowsUpdated: Convert.ToInt64(r.GetValue(5)),
                RowsInserted: Convert.ToInt64(r.GetValue(6)),
                TotalRowsProcessed: Convert.ToInt64(r.GetValue(7)),
                FinishedLocal: r.GetDateTime(8),
                ErrorMessage: Db.Str(r, 9)));
        }
        return rows;
    }

    private async Task<long> ScalarLongAsync(
        Microsoft.Data.SqlClient.SqlConnection cn, string sql, CancellationToken ct)
    {
        await using var cmd = _db.Cmd(cn, sql);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? 0 : Convert.ToInt64(v);
    }

    /// <summary>Maps SUCCESS/FAILED onto the vocabulary the rest of the UI already styles.</summary>
    private static string MapOutcome(string status) => status.Trim().ToUpperInvariant() switch
    {
        "SUCCESS" => Outcome.Succeeded,
        "FAILED" => Outcome.Failed,
        "RUNNING" => Outcome.Running,
        _ => Outcome.Unknown
    };

    /// <summary>
    /// "HB ICD10DX Recon Current Upsert" -> "HBICD10Dx", so a row can be matched to the
    /// reconciliation panel's key for the same dataset. Compared case-insensitively
    /// against the configured keys rather than reformatted by rule, because the two
    /// naming styles agree on letters and nothing else.
    /// </summary>
    private static string? DatasetKey(string processName)
    {
        var trimmed = SuffixRegex().Replace(processName, string.Empty);
        var squashed = NonAlphaNumRegex().Replace(trimmed, string.Empty);

        return KnownKeys.FirstOrDefault(k =>
            string.Equals(NonAlphaNumRegex().Replace(k, string.Empty), squashed,
                          StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The reconciliation keys as configured. Kept here rather than read from config so a
    /// dataset the log knows about but the recon panel does not is reported with no key
    /// instead of throwing - the two lists are allowed to drift.
    /// </summary>
    private static readonly string[] KnownKeys =
    [
        "HBCharge", "HBCPT", "HBEncounter", "HBICD10Dx", "HBICD10Px", "HBPayments",
        "PBCharge", "PBEncounter", "PBICD10Dx", "PBPayments", "Surgical_Time_Data"
    ];

    [GeneratedRegex(@"\s*Recon\s*Current\s*Upsert\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SuffixRegex();

    [GeneratedRegex(@"[^A-Za-z0-9]")]
    private static partial Regex NonAlphaNumRegex();
}
