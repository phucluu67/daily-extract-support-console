using ExtractConsole.Config;
using ExtractConsole.Models;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Text;

namespace ExtractConsole.Services;

/// <summary>
/// The unattended check. Looks for the failures a green job status cannot express.
/// <para>
/// Every serious problem found in this extract during October 2026 was a silent
/// success: the job reported OK while shipping empty files, while sending Strata 727
/// fragments a day, and while a step that had been commented out reported success. The
/// job's own notification is failure-only, so none of it reached anyone. That is what
/// this exists to catch.
/// </para>
/// </summary>
public sealed class CheckService(AppConfig app, EnvironmentConfig env)
{
    private readonly AppConfig _app = app;
    private readonly EnvironmentConfig _env = env;
    private readonly Db _db = new(env);

    public async Task<CheckReport> RunAsync(bool force, CancellationToken ct)
    {
        var findings = new List<CheckFinding>();
        var counts = new List<ReconCount>();

        var job = new JobService(_env);
        JobStatus status;
        try
        {
            status = await job.GetStatusAsync(ct);
        }
        catch (Exception ex)
        {
            // Being unable to look is itself worth telling someone about.
            findings.Add(new CheckFinding("Critical", "CheckFailed",
                "The checker could not read the job's status",
                $"{_env.ControlServer}: {ex.Message}"));
            return new CheckReport(_env.Name, null, null, null, findings, counts, false, null)
                   { ControlServer = _env.ControlServer };
        }

        // ---- is it even running / did it run at all ------------------------

        if (status.IsRunning)
        {
            // Mid-flight: the data checks would be measuring a half-loaded system.
            return new CheckReport(_env.Name, null, status.StartedLocal, "running",
                [new CheckFinding("Info", "StillRunning",
                    "The extract is still running", $"Started {status.StartedLocal:g}.")],
                counts, false, null) { ControlServer = _env.ControlServer };
        }

        var hoursSince = status.StartedLocal is { } started
            ? (DateTime.Now - started).TotalHours
            : double.MaxValue;

        // Only meaningful where the job is supposed to run on its own. DEV is started by
        // hand with every schedule disabled, so "has not run for 29 hours" there is a
        // fact about the week, not a fault - and an alert that always fires is an alert
        // nobody reads.
        var isScheduled = status.Schedules.Any(s => s.Enabled);

        if (isScheduled && hoursSince > _app.Check.MaxHoursSinceRun)
        {
            findings.Add(new CheckFinding("Critical", "NoRecentRun",
                "The extract has not run recently",
                status.StartedLocal is null
                    ? "No run found in history at all."
                    : $"Last run started {status.StartedLocal:g}, "
                      + $"{hoursSince:F0}h ago (threshold {_app.Check.MaxHoursSinceRun}h)."));
        }

        if (status.Outcome == Outcome.Failed)
        {
            var step = status.Steps.FirstOrDefault(s => s.StepId == status.FailedStepId);
            var detail = $"Failed at step {status.FailedStepId} ({step?.StepName}).";
            if (step?.Diagnosis is { } dx)
            {
                detail += $" Known issue: {dx.Title}.";
                if (dx.Solutions.Count > 0) detail += $" First thing to try: {dx.Solutions[0].Solution}";
            }
            findings.Add(new CheckFinding("Critical", "RunFailed", "The last run failed", detail));
        }

        // A run someone stopped is not a run that succeeded, and nothing else reports it:
        // the job's own notification is failure-only, and a cancel is not a failure. Left
        // alone it reads as "no alerts" while the day's data never arrived.
        if (status.Outcome == Outcome.Canceled)
        {
            findings.Add(new CheckFinding("Warning", "RunCanceled", "The last run was stopped before it finished",
                $"Started {status.StartedLocal:g} and was cancelled"
                + (status.CurrentStepId is { } at ? $" around step {at}." : ".")
                + " Nothing restarted it."));
        }

        // ---- did any data actually move ------------------------------------

        if (_env.ReconTables.Count > 0)
        {
            try
            {
                counts = await new ReconService(_env)
                    .GetCountsAsync(status.StartedLocal?.Date ?? DateTime.Today,
                                    exact: false, countRecords: _app.Check.CountRecords, ct);
            }
            catch (Exception ex)
            {
                findings.Add(new CheckFinding("Warning", "ReconUnavailable",
                    "Could not reconcile counts", ex.Message));
            }

            findings.AddRange(await EvaluateCountsAsync(counts, ct));
        }

        var critical = findings.Count(f => f.Severity == "Critical");
        var warnings = findings.Count(f => f.Severity == "Warning");

        var report = new CheckReport(_env.Name, status.RunInstanceId, status.StartedLocal,
            status.Outcome, findings, counts, false, null) { ControlServer = _env.ControlServer };

        if (critical + warnings == 0) return report;

        // ---- has this run already been reported ----------------------------

        if (!force && await AlreadyHandledAsync(status.RunInstanceId, ct))
            return report with { AlreadyReported = true };

        return report;
    }

    private async Task<List<CheckFinding>> EvaluateCountsAsync(List<ReconCount> counts, CancellationToken ct)
    {
        var findings = new List<CheckFinding>();
        if (counts.Count == 0) return findings;

        var withData = counts.Where(c => c.TableRows is not null).ToList();

        // Everything at zero is the signature of 2026-10-08: every step succeeded and
        // nothing moved. Reported as one finding rather than eleven.
        if (withData.Count > 0 && withData.All(c => c.TableRows == 0))
        {
            findings.Add(new CheckFinding("Critical", "EmptyExtract",
                "The extract produced no data at all",
                $"All {withData.Count} recon tables hold 0 rows. The job will have reported success. "
                + "Check staging and the Epic extract child jobs."));
            return findings;   // every other count finding would be noise
        }

        foreach (var c in counts.Where(c => c.Status == ReconStatus.Short))
        {
            findings.Add(new CheckFinding("Critical", "ReconShort",
                $"{c.Key}: fewer records than table rows",
                $"Table {c.TableRows:N0} vs file {(c.FileRecords ?? c.FileLines):N0} "
                + $"(difference {c.Difference:N0})."));
        }

        foreach (var c in counts.Where(c => c.Status == ReconStatus.ExtraLines))
        {
            findings.Add(new CheckFinding("Warning", "SplitRecords",
                $"{c.Key}: records split across lines",
                c.ContinuationLines is > 0
                    ? $"{c.ContinuationLines:N0} continuation line(s). No data lost, but the file is "
                      + "malformed for a line-based reader and the count sent to Strata is overstated."
                    : $"File reports {c.FileLines:N0} lines against {c.TableRows:N0} rows. "
                      + "An embedded CR/LF in a field value."));
        }

        if (_app.Check.VolumeDropFactor > 0)
            findings.AddRange(await DetectVolumeDropsAsync(counts, ct));

        return findings;
    }

    /// <summary>
    /// Compares each dataset against the last check that recorded a count for it. Catches
    /// the partial version of the empty-run failure - a collapse that is not all the way
    /// to zero and so passes every other check here.
    /// </summary>
    private async Task<List<CheckFinding>> DetectVolumeDropsAsync(List<ReconCount> counts, CancellationToken ct)
    {
        var findings = new List<CheckFinding>();

        await using var cn = _db.Diagnostics();
        await cn.OpenAsync(ct);

        const string sql = """
            SELECT c.DatasetKey, c.TableRows
            FROM diag.CheckCount AS c
            JOIN diag.CheckRun AS r ON r.CheckRunId = c.CheckRunId
            WHERE r.EnvironmentName = @env
              AND c.TableRows > 0
              AND c.CheckCountId = (
                    SELECT MAX(c2.CheckCountId)
                    FROM diag.CheckCount AS c2
                    JOIN diag.CheckRun AS r2 ON r2.CheckRunId = c2.CheckRunId
                    WHERE r2.EnvironmentName = @env AND c2.DatasetKey = c.DatasetKey AND c2.TableRows > 0);
            """;

        var previous = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = _db.Cmd(cn, sql))
        {
            cmd.Parameters.Add("@env", SqlDbType.NVarChar, 50).Value = _env.Name;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) previous[r.GetString(0)] = r.GetInt64(1);
        }

        foreach (var c in counts)
        {
            if (c.TableRows is not { } now || now < 0) continue;

            // Only datasets with a known-good previous count. That is what keeps DEV's
            // permanently empty Surgical table - step 7 is commented out - from firing
            // every single day.
            if (!previous.TryGetValue(c.Key, out var before) || before <= 0) continue;

            // Zero is not a 100% drop to be filed under "down sharply". A dataset that
            // used to have data and now has none is the empty-run failure happening to
            // one table, and nothing else here catches it: EmptyExtract needs ALL of
            // them at zero, and 0 == 0 reconciles perfectly against an empty file.
            if (now == 0)
            {
                findings.Add(new CheckFinding("Critical", "DatasetEmpty",
                    $"{c.Key}: no rows at all",
                    $"0 rows, against {before:N0} at the last check. The file sent to Strata "
                    + "will be header-only and will still reconcile."));
                continue;
            }

            var factor = (double)now / before;
            if (factor >= _app.Check.VolumeDropFactor) continue;

            findings.Add(new CheckFinding("Warning", "VolumeDrop",
                $"{c.Key}: volume down sharply",
                $"{now:N0} rows against {before:N0} last time - {(1 - factor) * 100:F0}% lower."));
        }

        return findings;
    }

    /// <summary>
    /// Has this run already been dealt with?
    /// <para>
    /// Keyed on a successful notification where mail is configured, so a send that
    /// failed is retried on the next tick rather than silently dropped. Where no
    /// recipients are set there is nothing to retry, so any previous row counts -
    /// otherwise a console-only install would write a row every thirty minutes forever.
    /// </para>
    /// </summary>
    private async Task<bool> AlreadyHandledAsync(long? runInstanceId, CancellationToken ct)
    {
        var notifyConfigured = !string.IsNullOrWhiteSpace(_app.Check.Recipients);

        await using var cn = _db.Diagnostics();
        await cn.OpenAsync(ct);

        var notifiedClause = notifyConfigured ? "AND Notified = 1" : "";

        // No instance id means nothing has ever run; dedupe on a time window instead.
        var sql = runInstanceId is null
            ? $"""
               SELECT TOP (1) 1 FROM diag.CheckRun
               WHERE EnvironmentName = @env AND RunInstanceId IS NULL {notifiedClause}
                 AND CheckedUtc > DATEADD(hour, -12, SYSUTCDATETIME());
               """
            : $"""
               SELECT TOP (1) 1 FROM diag.CheckRun
               WHERE EnvironmentName = @env AND RunInstanceId = @id {notifiedClause};
               """;

        await using var cmd = _db.Cmd(cn, sql);
        cmd.Parameters.Add("@env", SqlDbType.NVarChar, 50).Value = _env.Name;
        if (runInstanceId is not null)
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = runInstanceId.Value;

        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>Records the check and, when there is something to say, sends the mail.</summary>
    public async Task<CheckReport> RecordAndNotifyAsync(CheckReport report, bool dryRun, CancellationToken ct)
    {
        var critical = report.Findings.Count(f => f.Severity == "Critical");
        var warnings = report.Findings.Count(f => f.Severity == "Warning");
        if (critical + warnings == 0 || report.AlreadyReported) return report;

        var body = BuildMessage(report);
        string? notifyError = null;
        var notified = false;

        if (!dryRun && !string.IsNullOrWhiteSpace(_app.Check.Recipients))
        {
            try
            {
                await SendMailAsync(report, body, ct);
                notified = true;
            }
            catch (Exception ex)
            {
                // Record the failure rather than let it look like nobody needed telling.
                notifyError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            }
        }

        if (!dryRun) await RecordAsync(report, critical, warnings, notified, notifyError, body, ct);

        return report with { Notified = notified, NotifyError = notifyError };
    }

    private async Task RecordAsync(CheckReport report, int critical, int warnings,
        bool notified, string? notifyError, string summary, CancellationToken ct)
    {
        await using var cn = _db.Diagnostics();
        await cn.OpenAsync(ct);

        const string sql = """
            INSERT INTO diag.CheckRun
                (EnvironmentName, RunInstanceId, RunStartedLocal, RunOutcome,
                 CriticalCount, WarningCount, Notified, NotifyError, Summary)
            OUTPUT INSERTED.CheckRunId
            VALUES (@env, @id, @started, @outcome, @crit, @warn, @notified, @err, @summary);
            """;

        await using var cmd = _db.Cmd(cn, sql);
        cmd.Parameters.Add("@env", SqlDbType.NVarChar, 50).Value = report.Environment;
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = (object?)report.RunInstanceId ?? DBNull.Value;
        cmd.Parameters.Add("@started", SqlDbType.DateTime2).Value = (object?)report.RunStartedLocal ?? DBNull.Value;
        cmd.Parameters.Add("@outcome", SqlDbType.VarChar, 20).Value = (object?)report.RunOutcome ?? DBNull.Value;
        cmd.Parameters.Add("@crit", SqlDbType.Int).Value = critical;
        cmd.Parameters.Add("@warn", SqlDbType.Int).Value = warnings;
        cmd.Parameters.Add("@notified", SqlDbType.Bit).Value = notified;
        cmd.Parameters.Add("@err", SqlDbType.NVarChar, 1000).Value = (object?)notifyError ?? DBNull.Value;
        cmd.Parameters.Add("@summary", SqlDbType.NVarChar, -1).Value = summary;

        var checkRunId = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));

        foreach (var c in report.Counts)
        {
            await using var cc = _db.Cmd(cn, """
                INSERT INTO diag.CheckCount (CheckRunId, DatasetKey, TableRows, FileLines, FileRecords)
                VALUES (@run, @key, @rows, @lines, @records);
                """);
            cc.Parameters.Add("@run", SqlDbType.Int).Value = checkRunId;
            cc.Parameters.Add("@key", SqlDbType.NVarChar, 100).Value = c.Key;
            cc.Parameters.Add("@rows", SqlDbType.BigInt).Value = (object?)c.TableRows ?? DBNull.Value;
            cc.Parameters.Add("@lines", SqlDbType.BigInt).Value = (object?)c.FileLines ?? DBNull.Value;
            cc.Parameters.Add("@records", SqlDbType.BigInt).Value = (object?)c.FileRecords ?? DBNull.Value;
            await cc.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task SendMailAsync(CheckReport report, string body, CancellationToken ct)
    {
        var critical = report.Findings.Count(f => f.Severity == "Critical");
        var subject = $"[{report.Environment}] CLARITY extract check: "
                    + (critical > 0 ? $"{critical} problem(s)" : "warnings")
                    + $" - {report.Findings[0].Title}";

        await using var cn = _db.Control("msdb");
        await cn.OpenAsync(ct);

        await using var cmd = _db.Proc(cn, "msdb.dbo.sp_send_dbmail");
        if (!string.IsNullOrWhiteSpace(_app.Check.MailProfile))
            cmd.Parameters.Add("@profile_name", SqlDbType.NVarChar, 128).Value = _app.Check.MailProfile;
        cmd.Parameters.Add("@recipients", SqlDbType.VarChar, 8000).Value = _app.Check.Recipients;
        cmd.Parameters.Add("@subject", SqlDbType.NVarChar, 255).Value = subject;
        cmd.Parameters.Add("@body", SqlDbType.NVarChar, -1).Value = body;
        cmd.CommandTimeout = 60;

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static string BuildMessage(CheckReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CLARITY -> STRATAJAZZ extract check  [{report.Environment}]");
        sb.AppendLine(new string('-', 62));
        // Spell the instance out. The two host names differ by one character
        // (roeeps914q / roeeps014q) and this mail is read at 07:30 on a phone.
        if (!string.IsNullOrWhiteSpace(report.ControlServer))
            sb.AppendLine($"Instance    : {report.ControlServer}");
        sb.AppendLine($"Run started : {report.RunStartedLocal:yyyy-MM-dd HH:mm}  ({report.RunOutcome})");
        sb.AppendLine($"Checked     : {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        foreach (var group in report.Findings.GroupBy(f => f.Severity)
                                             .OrderBy(g => g.Key == "Critical" ? 0 : g.Key == "Warning" ? 1 : 2))
        {
            sb.AppendLine($"{group.Key.ToUpperInvariant()}");
            foreach (var f in group)
            {
                sb.AppendLine($"  * {f.Title}");
                if (!string.IsNullOrWhiteSpace(f.Detail)) sb.AppendLine($"    {f.Detail}");
            }
            sb.AppendLine();
        }

        if (report.Counts.Count > 0)
        {
            sb.AppendLine("COUNTS");
            sb.AppendLine($"  {"dataset",-22} {"table",12} {"file",12}");
            foreach (var c in report.Counts)
            {
                var file = c.FileRecords ?? c.FileLines;
                sb.AppendLine($"  {c.Key,-22} {c.TableRows,12:N0} {file,12:N0}   {c.Status}");
            }
            sb.AppendLine();
        }

        sb.AppendLine(report.Findings.Any(f => f.Code == "RunFailed")
            ? "Open the console for the failing step, its diagnosis and what to try."
            : "Open the console to see this in context.");
        return sb.ToString();
    }
}
