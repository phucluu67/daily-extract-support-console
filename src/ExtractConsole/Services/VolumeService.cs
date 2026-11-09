using ExtractConsole.Config;
using ExtractConsole.Models;
using System.Data;
using System.Text.RegularExpressions;

namespace ExtractConsole.Services;

/// <summary>
/// Daily rows_extracted per dataset, from epsi_staging.dbo.etl_audit_log on the data
/// instance. Each staging package writes one row per run: package, start/end, status,
/// rows_extracted.
/// <para>
/// Two things the raw log gets wrong for a trend, both handled here:
/// </para>
/// <list type="bullet">
/// <item>It is keyed by PACKAGE, and package names changed over time on DEV
/// (LoadStagingHBChargeTable -> ...Optimize, ...UsingScript, a lower-case "optimize").
/// Names are mapped to the dataset they load, so a rename does not break the line.</item>
/// <item>A day can hold several runs - a failure and a re-run, or two attempts. The value
/// for the day is the LAST SUCCESSFUL run: the load that actually stuck. A day with no
/// successful run has no value, which is kept distinct from a run that succeeded with
/// 0 rows - the second is the silent empty run, and the point of showing it.</item>
/// </list>
/// </summary>
public sealed partial class VolumeService(EnvironmentConfig env)
{
    private readonly EnvironmentConfig _env = env;
    private readonly Db _db = new(env);

    /// <summary>
    /// The ten datasets, in the order the extract's own file map uses. Keys match the
    /// reconciliation and AdHoc panels.
    /// </summary>
    private static readonly (string Key, string Label)[] Datasets =
    [
        ("HBEncounter", "HB Encounter"), ("HBCharge", "HB Charge"), ("HBPayments", "HB Payments"),
        ("HBCPT", "HB CPT"), ("HBICD10Dx", "HB ICD10Dx"), ("HBICD10Px", "HB ICD10Px"),
        ("PBEncounter", "PB Encounter"), ("PBCharge", "PB Charge"), ("PBPayments", "PB Payments"),
        ("PBICD10Dx", "PB ICD10Dx"),
    ];

    public async Task<VolumeReport> GetAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        from = from.Date;
        to = to.Date;
        if (to < from) (from, to) = (to, from);

        await using var cn = _db.Data(_env.StagingDatabase);
        await cn.OpenAsync(ct);

        DateTime? earliest = null, latest = null;
        await using (var span = _db.Cmd(cn,
            "SELECT MIN(execution_start_time), MAX(execution_start_time) FROM dbo.etl_audit_log;"))
        await using (var r = await span.ExecuteReaderAsync(ct))
        {
            if (await r.ReadAsync(ct))
            {
                earliest = Db.Dt(r, 0)?.Date;
                latest = Db.Dt(r, 1)?.Date;
            }
        }

        // "All" asks from the beginning of time; start the axis where the log does, or the
        // chart opens on years of nothing.
        if (earliest is not null && from < earliest.Value) from = earliest.Value;
        if (to < from) to = from;

        const string sql = """
            SELECT package_name, execution_start_time, status, rows_extracted
            FROM dbo.etl_audit_log
            WHERE execution_start_time >= @from AND execution_start_time < @toExclusive
            ORDER BY execution_start_time;
            """;

        var runs = new List<(string Key, string Package, DateTime Start, string Status, long? Rows)>();
        var ignored = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        await using (var cmd = _db.Cmd(cn, sql))
        {
            cmd.Parameters.Add("@from", SqlDbType.DateTime).Value = from;
            cmd.Parameters.Add("@toExclusive", SqlDbType.DateTime).Value = to.AddDays(1);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var package = Db.Str(r, 0) ?? "";
                var start = Db.Dt(r, 1);
                if (start is null) continue;

                var key = DatasetKey(package);
                if (key is null) { ignored.Add(package); continue; }

                runs.Add((key, package, start.Value, Db.Str(r, 2) ?? "", Db.Long(r, 3)));
            }
        }

        var days = Enumerable.Range(0, (to - from).Days + 1).Select(i => from.AddDays(i)).ToList();
        var series = new List<VolumeSeries>();

        foreach (var (key, label) in Datasets)
        {
            var mine = runs.Where(x => x.Key == key).ToList();
            var byDay = mine.GroupBy(x => x.Start.Date).ToDictionary(g => g.Key, g => g.ToList());

            var points = days.Select(d =>
            {
                if (!byDay.TryGetValue(d, out var dayRuns))
                    return new VolumePoint(d, null, 0, 0);

                var ok = dayRuns.Where(x => IsSucceeded(x.Status)).OrderBy(x => x.Start).ToList();
                var failed = dayRuns.Count(x => IsFailed(x.Status));
                return new VolumePoint(d, ok.Count > 0 ? ok[^1].Rows ?? 0 : null, ok.Count, failed);
            }).ToList();

            series.Add(new VolumeSeries(key, label, points, Summarise(points),
                [.. mine.Select(x => x.Package).Distinct(StringComparer.OrdinalIgnoreCase).Order()]));
        }

        var total = days.Select((d, i) =>
        {
            var values = series.Select(s => s.Points[i].Rows).Where(v => v is not null).ToList();
            return new VolumePoint(d, values.Count == 0 ? null : values.Sum(v => v!.Value),
                series.Sum(s => s.Points[i].SucceededRuns), series.Sum(s => s.Points[i].FailedRuns));
        }).ToList();

        // Every dataset that ran succeeded, and all of them with nothing.
        var allZero = days.Where((d, i) =>
        {
            var ran = series.Select(s => s.Points[i]).Where(p => p.Rows is not null).ToList();
            return ran.Count > 0 && ran.All(p => p.Rows == 0);
        }).ToList();

        return new VolumeReport(from, to, earliest, latest, series, total, allZero, [.. ignored]);
    }

    private static VolumeSummary Summarise(List<VolumePoint> points)
    {
        var withValue = points.Where(p => p.Rows is not null).ToList();
        var nonZero = withValue.Where(p => p.Rows > 0).ToList();
        var last = withValue.LastOrDefault();
        var peak = nonZero.OrderByDescending(p => p.Rows).ThenByDescending(p => p.Date).FirstOrDefault();

        long? Mean(IEnumerable<VolumePoint> ps)
        {
            var list = ps.ToList();
            return list.Count == 0 ? null : (long)Math.Round(list.Average(p => (double)p.Rows!.Value));
        }

        long? Median(List<VolumePoint> ps)
        {
            if (ps.Count == 0) return null;
            var v = ps.Select(p => p.Rows!.Value).Order().ToList();
            return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
        }

        // Compared on days that loaded something: the weekly zero day would otherwise land
        // in one window or the other and fake a 15% swing.
        var last7 = nonZero.TakeLast(7).ToList();
        var prior7 = nonZero.SkipLast(7).TakeLast(7).ToList();

        return new VolumeSummary(
            Latest: last?.Rows,
            LatestDate: last?.Date,
            Typical: Median(nonZero),
            Peak: peak?.Rows,
            PeakDate: peak?.Date,
            Last7Average: last7.Count == 7 ? Mean(last7) : null,
            Prior7Average: prior7.Count == 7 ? Mean(prior7) : null,
            ZeroDays: withValue.Count(p => p.Rows == 0));
    }

    private static bool IsSucceeded(string status) =>
        status.Equals("Succeeded", StringComparison.OrdinalIgnoreCase)
        || status.Equals("Success", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailed(string status) =>
        status.Equals("Failed", StringComparison.OrdinalIgnoreCase)
        || status.Equals("Failure", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// "LoadStagingHBChargeTableOptimize" -> "HBCharge". Matches whatever suffix the package
    /// has grown; anything that is not a staging load for one of the ten (TEST) is ignored
    /// and reported as ignored.
    /// </summary>
    private static string? DatasetKey(string package)
    {
        var m = PackageRegex().Match(package);
        if (!m.Success) return null;

        var prefix = m.Groups[1].Value.ToUpperInvariant();
        var entity = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "ENCOUNTER" => "Encounter",
            "CHARGE" => "Charge",
            "PAYMENTS" => "Payments",
            "CPT" => "CPT",
            "ICD10DX" => "ICD10Dx",
            "ICD10PX" => "ICD10Px",
            _ => null
        };
        if (entity is null) return null;

        var key = prefix + entity;
        return Datasets.Any(d => d.Key == key) ? key : null;
    }

    [GeneratedRegex(@"^LoadStaging(HB|PB)(Encounter|Charge|Payments|CPT|ICD10Dx|ICD10Px)Table", RegexOptions.IgnoreCase)]
    private static partial Regex PackageRegex();
}
