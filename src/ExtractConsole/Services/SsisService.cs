using ExtractConsole.Config;
using ExtractConsole.Models;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.RegularExpressions;

namespace ExtractConsole.Services;

/// <summary>
/// Reports the SSIS side of a run.
/// <para>
/// The extract is almost entirely SSIS, but only one package is launched by the parent
/// job directly - the other ~27 are launched by its child jobs. Rather than name any of
/// them in configuration, the package list is read from the SSIS job steps of the parent
/// and every allowlisted child job, so it follows the jobs as they change.
/// </para>
/// <para>
/// Executions are then matched to that list inside the run's own time window. Filtering
/// to the extract's own packages matters: other SSIS work runs on the same instance, and
/// attributing someone else's failure to this extract would be worse than showing less.
/// </para>
/// </summary>
public sealed partial class SsisService(EnvironmentConfig env)
{
    private readonly EnvironmentConfig _env = env;
    private readonly Db _db = new(env);

    /// <summary>A package referenced by an SSIS job step.</summary>
    public sealed record PackageRef(
        string JobName, int StepId, string StepName, string Folder, string Project, string Package);

    // ---------------------------------------------------------------------

    public async Task<SsisRunReport> GetRunReportAsync(RunWindow? window, CancellationToken ct)
    {
        var refs = await DiscoverPackagesAsync(ct);

        if (window is null || refs.Count == 0)
            return new SsisRunReport(window, [], refs.Count, 0, 0, 0, refs.Count);

        var executions = await GetExecutionsInWindowAsync(window, ct);

        var packages = new List<SsisRunPackage>();
        foreach (var r in refs)
        {
            // Case-insensitive: the same project is spelled both "ExtractCLARITYDataMayo"
            // and "ExtractCLARITYDatamAYO" across different step definitions.
            var matches = executions
                .Where(e => Same(e.Folder, r.Folder) && Same(e.Project, r.Project) && Same(e.Package, r.Package))
                .OrderBy(e => e.ExecutionId)
                .ToList();

            var last = matches.LastOrDefault();

            packages.Add(new SsisRunPackage(
                r.JobName, r.StepId, r.StepName, r.Folder, r.Project, r.Package,
                last?.ExecutionId,
                last is null ? "not run" : StatusText(last.Status),
                last?.StartLocal,
                last?.EndLocal,
                last is { StartLocal: not null, EndLocal: not null }
                    ? (int)(last.EndLocal.Value - last.StartLocal.Value).TotalSeconds
                    : null,
                last?.CallerName,
                matches.Count));
        }

        return new SsisRunReport(
            window,
            packages,
            packages.Count,
            packages.Count(p => p.Status == Outcome.Succeeded),
            packages.Count(p => p.Status is Outcome.Failed or "ended unexpectedly"),
            packages.Count(p => p.Status == Outcome.Running),
            packages.Count(p => p.Status == "not run"));
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------------

    /// <summary>
    /// Every SSIS package referenced by the parent job or one of its child jobs, read
    /// from the <c>/ISSERVER</c> path in each SSIS step's command.
    /// </summary>
    public async Task<List<PackageRef>> DiscoverPackagesAsync(CancellationToken ct)
    {
        var jobNames = _env.AllJobNames().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (jobNames.Count == 0) return [];

        var paramNames = jobNames.Select((_, i) => $"@j{i}").ToList();
        var sql = $"""
            SELECT j.name, s.step_id, s.step_name, s.command
            FROM msdb.dbo.sysjobsteps AS s
            JOIN msdb.dbo.sysjobs AS j ON j.job_id = s.job_id
            WHERE s.subsystem = 'SSIS'
              AND j.name IN ({string.Join(", ", paramNames)})
            ORDER BY j.name, s.step_id;
            """;

        await using var cn = _db.Control();
        await cn.OpenAsync(ct);
        await using var cmd = _db.Cmd(cn, sql);
        for (var i = 0; i < jobNames.Count; i++)
            cmd.Parameters.Add(paramNames[i], SqlDbType.NVarChar, 128).Value = jobNames[i];

        var list = new List<PackageRef>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var command = Db.Str(r, 3);
            if (command is null) continue;

            var m = CatalogPathRegex().Match(command);
            if (!m.Success) continue;      // a file-system package, not a catalog one

            list.Add(new PackageRef(
                r.GetString(0), r.GetInt32(1), r.GetString(2),
                m.Groups["folder"].Value, m.Groups["project"].Value, m.Groups["package"].Value));
        }

        // Parent job first, then children, so the list reads in execution order.
        return list
            .OrderBy(p => string.Equals(p.JobName, _env.JobName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => p.JobName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.StepId)
            .ToList();
    }

    private sealed record ExecutionRow(
        long ExecutionId, string Folder, string Project, string Package,
        int Status, DateTime? StartLocal, DateTime? EndLocal, string? CallerName);

    private async Task<List<ExecutionRow>> GetExecutionsInWindowAsync(RunWindow window, CancellationToken ct)
    {
        await using var cn = _db.Control("SSISDB");
        await cn.OpenAsync(ct);

        // start_time is datetimeoffset; CONVERT to datetime2 compares it as the local wall
        // clock the job's own timestamps use. Comparing it to a plain literal instead
        // silently treats the literal as UTC and shifts the window by the server's offset.
        const string sql = """
            SELECT e.execution_id, e.folder_name, e.project_name, e.package_name,
                   e.status, e.start_time, e.end_time, e.caller_name
            FROM catalog.executions AS e
            WHERE CONVERT(datetime2, e.start_time) >= @from
              AND CONVERT(datetime2, e.start_time) <= @to
            ORDER BY e.execution_id;
            """;

        await using var cmd = _db.Cmd(cn, sql);
        // A little slack either side: a package can start a moment before the parent's
        // recorded start, or right on its recorded end.
        cmd.Parameters.Add("@from", SqlDbType.DateTime2).Value = window.From.AddMinutes(-2);
        cmd.Parameters.Add("@to", SqlDbType.DateTime2).Value = window.To.AddMinutes(5);

        var list = new List<ExecutionRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var start = r.IsDBNull(5) ? (DateTimeOffset?)null : r.GetDateTimeOffset(5);
            var end = r.IsDBNull(6) ? (DateTimeOffset?)null : r.GetDateTimeOffset(6);
            list.Add(new ExecutionRow(
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetInt32(4), start?.LocalDateTime, end?.LocalDateTime, Db.Str(r, 7)));
        }
        return list;
    }

    // ---------------------------------------------------------------------

    public async Task<List<SsisMessage>> GetMessagesAsync(long executionId, CancellationToken ct)
    {
        await using var cn = _db.Control("SSISDB");
        await cn.OpenAsync(ct);

        // 120 = Error, 130 = TaskFailed, 110 = Warning. Anything else is noise here.
        const string sql = """
            SELECT TOP (300)
                   em.message_time, em.message_type, ISNULL(em.message_source_name, ''), em.message
            FROM catalog.event_messages AS em
            WHERE em.operation_id = @id
              AND em.message_type IN (110, 120, 130)
            ORDER BY em.message_time DESC;
            """;

        await using var cmd = _db.Cmd(cn, sql);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = executionId;

        var list = new List<SsisMessage>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var t = r.IsDBNull(0) ? (DateTimeOffset?)null : r.GetDateTimeOffset(0);
            list.Add(new SsisMessage(
                TimeLocal: t?.LocalDateTime,
                MessageType: r.GetInt16(1) switch
                {
                    110 => "Warning",
                    120 => "Error",
                    130 => "Task failed",
                    _ => "Info"
                },
                SourceName: r.GetString(2),
                Message: Db.Str(r, 3) ?? ""));
        }
        return list;
    }

    private static string StatusText(int status) => status switch
    {
        1 => "created",
        2 => Outcome.Running,
        3 => Outcome.Canceled,
        4 => Outcome.Failed,
        5 => "pending",
        6 => "ended unexpectedly",
        7 => Outcome.Succeeded,
        8 => "stopping",
        9 => "completed",
        _ => Outcome.Unknown
    };

    /// <summary>Pulls folder / project / package out of an /ISSERVER step command.</summary>
    [GeneratedRegex(@"\\SSISDB\\(?<folder>[^\\""]+)\\(?<project>[^\\""]+)\\(?<package>[^\\""]+?\.dtsx)",
                    RegexOptions.IgnoreCase)]
    private static partial Regex CatalogPathRegex();
}
