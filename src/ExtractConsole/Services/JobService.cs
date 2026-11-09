using ExtractConsole.Config;
using ExtractConsole.Models;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.RegularExpressions;

namespace ExtractConsole.Services;

/// <summary>
/// Everything that reads or drives SQL Agent on the control instance.
/// </summary>
public sealed partial class JobService(EnvironmentConfig env)
{
    private readonly EnvironmentConfig _env = env;
    private readonly Db _db = new(env);

    /// <summary>
    /// run_date (yyyymmdd int) + run_time (hhmmss int) -> datetime, computed inline rather
    /// than via msdb.dbo.agent_datetime so the query needs no EXECUTE grant.
    /// </summary>
    private const string AgentDateTime = """
        CONVERT(datetime,
          STUFF(STUFF(CAST({0}.run_date AS char(8)), 5, 0, '-'), 8, 0, '-') + ' ' +
          STUFF(STUFF(RIGHT('000000' + CAST({0}.run_time AS varchar(6)), 6), 5, 0, ':'), 3, 0, ':'))
        """;

    private static string Dt(string alias) => string.Format(AgentDateTime, alias);

    // =====================================================================
    //  Status
    // =====================================================================

    public async Task<JobStatus> GetStatusAsync(CancellationToken ct)
    {
        await using var cn = _db.Control();
        await cn.OpenAsync(ct);

        var (jobId, jobEnabled) = await GetJobAsync(cn, _env.JobName, ct)
            ?? throw new InvalidOperationException(
                $"Job '{_env.JobName}' was not found on {_env.ControlServer}. " +
                "Check the instance - a default instance on the same host will not have it.");

        var live = await GetLiveActivityAsync(cn, jobId, ct);
        var isRunning = live is { Started: not null, Stopped: null };

        var (lo, hi) = await GetRunBoundsAsync(cn, jobId, isRunning, ct);
        var history = await GetStepHistoryAsync(cn, jobId, lo, hi, ct);
        var defined = await GetDefinedStepsAsync(cn, jobId, ct);

        // Outcome row for the run we are reporting on (absent while in flight).
        OutcomeRow? outcome = isRunning ? null : await GetOutcomeRowAsync(cn, jobId, hi, ct);

        // Only worth the extra round trip while something is actually running.
        var agentStep = isRunning ? await GetAgentCurrentStepAsync(cn, _env.JobName, ct) : null;
        var currentStepId = isRunning
            ? CurrentStep(history, defined, live?.LastExecutedStepId, agentStep)
            : null;
        var failedStepId = history.Where(h => h.RunStatus == 0).Select(h => (int?)h.StepId).LastOrDefault();

        var (childByStep, childWarnings) = MapChildJobs(defined);
        var disabledByStep = _env.DisabledSteps.ToDictionary(d => d.StepId, d => d.Note);
        var reachable = ReachableSteps(defined);

        // Cached and shared; a catalogue that is unreachable must not cost us the status
        // panel, so a failure here just means no diagnoses this time.
        List<FailureRule> rules;
        try { rules = await new DiagnosticsService(_env).GetRulesAsync(forceReload: false, ct); }
        catch (Microsoft.Data.SqlClient.SqlException) { rules = []; }

        var steps = new List<StepStatus>();
        foreach (var d in defined)
        {
            var h = history.LastOrDefault(x => x.StepId == d.StepId);
            var hasNote = disabledByStep.TryGetValue(d.StepId, out var configuredNote);

            // The step body decides; config only supplies the reason and date. A note for
            // a step that is actually live is stale and is reported as such rather than
            // silently mislabelling the step - that is the bug this replaced.
            var note = d.IsNoOp ? configuredNote : null;
            var configWarning = (d.IsNoOp, hasNote) switch
            {
                (false, true) => "appsettings.json lists this step as disabled, but its body is active. "
                               + "Remove it from DisabledSteps.",
                (true, false) => "This step's body is commented out, but appsettings.json has no note "
                               + "explaining why. Consider adding one to DisabledSteps.",
                _ => null
            };
            if (childWarnings.TryGetValue(d.StepId, out var cw))
                configWarning = string.Join(" ", new[] { configWarning }.Concat(cw).Where(x => x is not null));

            var stepOutcome = ResolveStepOutcome(h, d.StepId, currentStepId, isRunning, d.IsNoOp);

            var children = new List<ChildJobStatus>();
            if (childByStep.TryGetValue(d.StepId, out var childNames))
            {
                foreach (var name in childNames)
                {
                    var cs = await GetChildStatusAsync(cn, name, ct);
                    if (cs is not null) children.Add(cs);
                }
            }

            steps.Add(new StepStatus(
                d.StepId, d.StepName, d.Subsystem, stepOutcome,
                h?.StartedLocal,
                h is null ? null : Db.DurationToSeconds(h.RunDuration),
                h?.Message, h?.Retries ?? 0, d.IsNoOp, note, configWarning,
                reachable.Contains(d.StepId),
                // Only where the step actually failed - a diagnosis on a green step is noise.
                h?.RunStatus == 0 ? ToDiagnosis(DiagnosticsService.Match(rules, h?.Message)) : null,
                children));
        }

        var schedules = await GetSchedulesAsync(cn, jobId, ct);

        var jobOutcome = isRunning
            ? Outcome.Running
            : outcome?.RunStatus switch
            {
                1 => Outcome.Succeeded,
                0 => Outcome.Failed,
                2 => Outcome.Retry,
                3 => Outcome.Canceled,
                _ => Outcome.Unknown
            };

        var startedLocal = isRunning ? live?.Started : outcome?.StartedLocal;
        int? elapsed = isRunning && live?.Started is { } s
            ? (int)Math.Max(0, (DateTime.Now - s).TotalSeconds)
            : null;

        return new JobStatus(
            Environment: _env.Name,
            JobName: _env.JobName,
            JobEnabled: jobEnabled,
            Outcome: jobOutcome,
            IsRunning: isRunning,
            StartedLocal: startedLocal,
            FinishedLocal: isRunning ? null : outcome?.StartedLocal?.AddSeconds(Db.DurationToSeconds(outcome.RunDuration)),
            DurationSeconds: isRunning || outcome is null ? null : Db.DurationToSeconds(outcome.RunDuration),
            ElapsedSeconds: elapsed,
            CurrentStepId: currentStepId,
            FailedStepId: isRunning ? null : failedStepId,
            Message: outcome?.Message,
            InvokedBy: isRunning ? DescribeSource(live?.RequestSource) : ParseInvokedBy(outcome?.Message),
            RunInstanceId: outcome?.InstanceId,
            NextScheduledLocal: NextScheduled(live, schedules),
            Steps: steps,
            Schedules: schedules);
    }

    /// <summary>
    /// A step that ran gets its recorded outcome, except where the step body is commented
    /// out - those report success but did nothing, so they are reported as no-ops instead
    /// of green. Steps with no history are pending (run in flight) or skipped (run over).
    /// </summary>
    private static string ResolveStepOutcome(
        StepHistoryRow? h, int stepId, int? currentStepId, bool isRunning, bool isDisabled)
    {
        if (h is null)
        {
            if (isRunning && currentStepId == stepId) return Outcome.Running;
            return isRunning ? Outcome.Pending : Outcome.Skipped;
        }

        if (isDisabled && h.RunStatus == 1) return Outcome.NoOp;

        return h.RunStatus switch
        {
            1 => Outcome.Succeeded,
            0 => Outcome.Failed,
            2 => Outcome.Retry,
            3 => Outcome.Canceled,
            _ => Outcome.Unknown
        };
    }

    /// <summary>
    /// Which step is in flight right now.
    /// <para>
    /// Agent writes a step's history row when the step <em>finishes</em>, and
    /// <c>sysjobactivity.last_executed_step_id</c> names the last step it
    /// <em>executed</em> - which, once that step has finished, is a step that is already
    /// done. Trusting it blindly reported "Running - step 2" while step 2's own history
    /// row said Succeeded. So it is only believed while the step it names has not yet
    /// recorded a finished row; otherwise the step in flight is the one after the highest
    /// finished step.
    /// </para>
    /// <para>
    /// A retry row (run_status 2) is not a finish - the step is about to run again - so
    /// those do not count as done, and a retrying step stays reported as the current one.
    /// </para>
    /// </summary>
    private static int? CurrentStep(
        List<StepHistoryRow> history, List<DefinedStep> defined, int? lastExecuted, int? agentStep)
    {
        // Agent's own live view, when we could get it, beats anything inferred.
        if (agentStep is > 0) return agentStep;

        var finished = history.Where(h => h.RunStatus != 2)
                              .Select(h => h.StepId)
                              .ToHashSet();

        if (lastExecuted is > 0 && !finished.Contains(lastExecuted.Value))
            return lastExecuted;

        if (finished.Count == 0)
        {
            // Nothing has finished and Agent did not tell us. The run may have been
            // started part-way through, so step 1 would be a guess - and a restart at a
            // multi-hour step would show that guess for hours. Report nothing instead.
            return lastExecuted is > 0 ? lastExecuted : null;
        }

        var maxDone = finished.Max();
        return defined.FirstOrDefault(d => d.StepId > maxDone)?.StepId;
    }

    /// <summary>
    /// The step Agent says it is executing right now, or null if it is not executing or
    /// the call is unavailable.
    /// <para>
    /// <c>sp_help_job</c> reports <c>current_execution_step</c> as "3 (step name)", and it
    /// is correct the instant a run starts - unlike history and
    /// <c>last_executed_step_id</c>, which say nothing until the first step finishes.
    /// That is what makes a restart begun at step 5 report step 5 straight away.
    /// </para>
    /// <para>
    /// Only trusted while <c>current_execution_status</c> is 1 (executing) or 3 (between
    /// retries); when the job is idle the column reads "0 (unknown)". Any failure here -
    /// most likely a missing SQLAgentReaderRole - returns null and leaves the caller on
    /// the inferred value, so this can never break the status panel.
    /// </para>
    /// </summary>
    private async Task<int?> GetAgentCurrentStepAsync(SqlConnection cn, string jobName, CancellationToken ct)
    {
        try
        {
            await using var cmd = _db.Proc(cn, "msdb.dbo.sp_help_job");
            cmd.Parameters.Add("@job_name", SqlDbType.NVarChar, 128).Value = jobName;
            cmd.Parameters.Add("@job_aspect", SqlDbType.VarChar, 9).Value = "JOB";

            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;

            var statusOrdinal = r.GetOrdinal("current_execution_status");
            var stepOrdinal = r.GetOrdinal("current_execution_step");

            if (r.IsDBNull(statusOrdinal)) return null;
            var status = Convert.ToInt32(r.GetValue(statusOrdinal));
            if (status is not (1 or 3)) return null;

            if (r.IsDBNull(stepOrdinal)) return null;
            var text = r.GetString(stepOrdinal);

            var m = LeadingStepNumberRegex().Match(text);
            if (!m.Success) return null;

            return int.TryParse(m.Groups[1].Value, out var id) && id > 0 ? id : null;
        }
        catch (Microsoft.Data.SqlClient.SqlException) { return null; }
        catch (IndexOutOfRangeException) { return null; }   // column absent on some versions
    }

    [GeneratedRegex(@"^\s*(\d+)")]
    private static partial Regex LeadingStepNumberRegex();

    // =====================================================================
    //  History
    // =====================================================================

    public async Task<List<RunHistoryItem>> GetHistoryAsync(int top, CancellationToken ct)
    {
        await using var cn = _db.Control();
        await cn.OpenAsync(ct);

        var sql = $"""
            SET NOCOUNT ON;
            WITH outcomes AS (
                SELECT h.instance_id, h.run_date, h.run_time, h.run_duration, h.run_status, h.message,
                       LAG(h.instance_id) OVER (ORDER BY h.instance_id) AS prev_instance_id
                FROM msdb.dbo.sysjobhistory AS h
                WHERE h.job_id = @jobId AND h.step_id = 0
            ),
            recent AS (
                SELECT TOP (@top) * FROM outcomes ORDER BY instance_id DESC
            )
            SELECT  r.instance_id,
                    {Dt("r")} AS started,
                    r.run_duration,
                    r.run_status,
                    r.message,
                    f.step_id,
                    f.step_name
            FROM recent AS r
            OUTER APPLY (
                SELECT TOP (1) s.step_id, s.step_name
                FROM msdb.dbo.sysjobhistory AS s
                WHERE s.job_id = @jobId
                  AND s.step_id > 0
                  AND s.run_status = 0
                  AND s.instance_id < r.instance_id
                  AND (r.prev_instance_id IS NULL OR s.instance_id > r.prev_instance_id)
                ORDER BY s.instance_id DESC
            ) AS f
            ORDER BY r.instance_id DESC;
            """;

        await using var cmd = _db.Cmd(cn, sql);
        cmd.Parameters.Add("@jobId", SqlDbType.UniqueIdentifier).Value = await JobIdAsync(cn, _env.JobName, ct);
        cmd.Parameters.Add("@top", SqlDbType.Int).Value = Math.Clamp(top, 1, 500);

        var list = new List<RunHistoryItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var message = Db.Str(r, 4);
            list.Add(new RunHistoryItem(
                InstanceId: r.GetInt32(0),
                StartedLocal: r.GetDateTime(1),
                DurationSeconds: Db.DurationToSeconds(r.GetInt32(2)),
                Outcome: r.GetInt32(3) switch
                {
                    1 => Outcome.Succeeded,
                    0 => Outcome.Failed,
                    2 => Outcome.Retry,
                    3 => Outcome.Canceled,
                    _ => Outcome.Unknown
                },
                FailedStepId: Db.Int(r, 5),
                FailedStepName: Db.Str(r, 6),
                InvokedBy: ParseInvokedBy(message),
                Message: message));
        }
        return list;
    }

    /// <summary>
    /// The time slice one run occupied, used to correlate SSIS catalog executions to it.
    /// Pass an instance_id for a historical run, or null for the current/last one.
    /// </summary>
    public async Task<RunWindow?> GetRunWindowAsync(long? instanceId, CancellationToken ct)
    {
        await using var cn = _db.Control();
        await cn.OpenAsync(ct);

        var jobId = await JobIdAsync(cn, _env.JobName, ct);

        if (instanceId is { } id)
        {
            var row = await GetOutcomeRowAsync(cn, jobId, id, ct);
            if (row?.StartedLocal is not { } s) return null;
            return new RunWindow(id, s, s.AddSeconds(Db.DurationToSeconds(row.RunDuration)), false);
        }

        var live = await GetLiveActivityAsync(cn, jobId, ct);
        if (live is { Started: not null, Stopped: null })
            return new RunWindow(null, live.Started.Value, DateTime.Now, true);

        var (_, hi) = await GetRunBoundsAsync(cn, jobId, false, ct);
        var last = await GetOutcomeRowAsync(cn, jobId, hi, ct);
        if (last?.StartedLocal is not { } start) return null;

        return new RunWindow(last.InstanceId, start,
            start.AddSeconds(Db.DurationToSeconds(last.RunDuration)), false);
    }

    /// <summary>Per-step detail for one historical run, for the drill-down.</summary>
    public async Task<List<StepStatus>> GetRunStepsAsync(long instanceId, CancellationToken ct)
    {
        await using var cn = _db.Control();
        await cn.OpenAsync(ct);
        var jobId = await JobIdAsync(cn, _env.JobName, ct);

        var prevSql = """
            SELECT MAX(instance_id) FROM msdb.dbo.sysjobhistory
            WHERE job_id = @jobId AND step_id = 0 AND instance_id < @id;
            """;
        await using var prevCmd = _db.Cmd(cn, prevSql);
        prevCmd.Parameters.Add("@jobId", SqlDbType.UniqueIdentifier).Value = jobId;
        prevCmd.Parameters.Add("@id", SqlDbType.BigInt).Value = instanceId;
        var prevObj = await prevCmd.ExecuteScalarAsync(ct);
        var lo = prevObj is null or DBNull ? 0L : Convert.ToInt64(prevObj);

        var history = await GetStepHistoryAsync(cn, jobId, lo, instanceId, ct);
        var defined = await GetDefinedStepsAsync(cn, jobId, ct);
        var disabledByStep = _env.DisabledSteps.ToDictionary(d => d.StepId, d => d.Note);
        var reachable = ReachableSteps(defined);

        List<FailureRule> histRules;
        try { histRules = await new DiagnosticsService(_env).GetRulesAsync(forceReload: false, ct); }
        catch (Microsoft.Data.SqlClient.SqlException) { histRules = []; }

        return defined.Select(d =>
        {
            var h = history.LastOrDefault(x => x.StepId == d.StepId);
            disabledByStep.TryGetValue(d.StepId, out var configuredNote);
            return new StepStatus(
                d.StepId, d.StepName, d.Subsystem,
                ResolveStepOutcome(h, d.StepId, null, false, d.IsNoOp),
                h?.StartedLocal,
                h is null ? null : Db.DurationToSeconds(h.RunDuration),
                h?.Message, h?.Retries ?? 0, d.IsNoOp, d.IsNoOp ? configuredNote : null, null,
                reachable.Contains(d.StepId),
                h?.RunStatus == 0 ? ToDiagnosis(DiagnosticsService.Match(histRules, h?.Message)) : null,
                []);
        }).ToList();
    }

    // =====================================================================
    //  Actions
    // =====================================================================

    /// <summary>
    /// Starts the job, optionally at a given step. The job name is checked against the
    /// configured allowlist first, so this endpoint cannot be used to run arbitrary jobs
    /// even if someone crafts their own request.
    /// </summary>
    public async Task<ActionResult> StartAsync(string? jobName, int? fromStepId, CancellationToken ct)
    {
        var job = ResolveAllowedJob(jobName);

        await using var cn = _db.Control();
        await cn.OpenAsync(ct);

        var (jobId, enabled) = await GetJobAsync(cn, job, ct)
            ?? throw new InvalidOperationException($"Job '{job}' was not found on {_env.ControlServer}.");

        if (!enabled)
            return new ActionResult(false, $"'{job}' is disabled in SQL Agent and cannot be started.", null);

        var live = await GetLiveActivityAsync(cn, jobId, ct);
        if (live is { Started: not null, Stopped: null })
            return new ActionResult(false, $"'{job}' is already running - started {live.Started:HH:mm:ss}.", null);

        string? stepName = null;
        if (fromStepId is { } sid)
        {
            var defined = await GetDefinedStepsAsync(cn, jobId, ct);
            stepName = defined.FirstOrDefault(d => d.StepId == sid)?.StepName
                ?? throw new InvalidOperationException($"'{job}' has no step {sid}.");
        }

        await using var cmd = _db.Proc(cn, "msdb.dbo.sp_start_job");
        cmd.Parameters.Add("@job_name", SqlDbType.NVarChar, 128).Value = job;
        if (stepName is not null)
            cmd.Parameters.Add("@step_name", SqlDbType.NVarChar, 128).Value = stepName;

        await cmd.ExecuteNonQueryAsync(ct);

        var what = stepName is null ? "from the beginning" : $"from step {fromStepId} ({stepName})";
        return new ActionResult(true, $"Started '{job}' {what}.", stepName);
    }

    public async Task<ActionResult> StopAsync(string? jobName, CancellationToken ct)
    {
        var job = ResolveAllowedJob(jobName);

        await using var cn = _db.Control();
        await cn.OpenAsync(ct);

        var (jobId, _) = await GetJobAsync(cn, job, ct)
            ?? throw new InvalidOperationException($"Job '{job}' was not found on {_env.ControlServer}.");

        var live = await GetLiveActivityAsync(cn, jobId, ct);
        if (live is not { Started: not null, Stopped: null })
            return new ActionResult(false, $"'{job}' is not running.", null);

        await using var cmd = _db.Proc(cn, "msdb.dbo.sp_stop_job");
        cmd.Parameters.Add("@job_name", SqlDbType.NVarChar, 128).Value = job;
        await cmd.ExecuteNonQueryAsync(ct);

        return new ActionResult(true,
            $"Stop requested for '{job}'. Agent stops the job after the running step yields.", null);
    }

    /// <summary>
    /// The authorisation gate for every action. Only the configured parent job and the
    /// child jobs it orchestrates may be started or stopped.
    /// </summary>
    private string ResolveAllowedJob(string? jobName)
    {
        if (string.IsNullOrWhiteSpace(jobName)) return _env.JobName;

        var match = _env.AllJobNames()
            .FirstOrDefault(n => string.Equals(n, jobName, StringComparison.OrdinalIgnoreCase));

        return match ?? throw new UnauthorizedAccessException(
            $"'{jobName}' is not a job this console manages. Allowed: {string.Join(", ", _env.AllJobNames())}.");
    }

    // =====================================================================
    //  Internals
    // =====================================================================

    /// <summary>
    /// RequestSource is a string because sysjobactivity.run_requested_source is declared
    /// sysname even though it holds the numeric source codes - reading it as an int throws.
    /// </summary>
    private sealed record LiveActivity(
        DateTime? Started, DateTime? Stopped, int? LastExecutedStepId, string? RequestSource,
        DateTime? NextScheduledRun);
    private sealed record OutcomeRow(long InstanceId, DateTime? StartedLocal, int RunDuration, int RunStatus, string? Message);
    private sealed record StepHistoryRow(int StepId, string StepName, int RunStatus, int RunDuration, DateTime? StartedLocal, string? Message, int Retries);
    private sealed record DefinedStep(
        int StepId, string StepName, string Subsystem,
        int OnSuccessAction, int OnSuccessStepId, bool IsNoOp,
        /// <summary>The command with comments stripped - what the step actually runs.</summary>
        string Body);

    /// <summary>
    /// Walks the job's success transitions from step 1 and returns the steps that walk
    /// actually reaches. In PROD the "Extract Complete Email" step quits reporting
    /// success, so "Xifin File Extract" sits after it and never runs on a normal pass -
    /// the UI has to say so rather than report it as merely "not reached".
    /// </summary>
    private static HashSet<int> ReachableSteps(List<DefinedStep> defined)
    {
        var reached = new HashSet<int>();
        if (defined.Count == 0) return reached;

        var byId = defined.ToDictionary(d => d.StepId);
        var current = defined[0].StepId;

        while (byId.TryGetValue(current, out var step) && reached.Add(current))
        {
            // 1 = quit success, 2 = quit failure, 3 = next step, 4 = go to step
            switch (step.OnSuccessAction)
            {
                case 3:
                    var next = defined.FirstOrDefault(d => d.StepId > step.StepId);
                    if (next is null) return reached;
                    current = next.StepId;
                    break;
                case 4:
                    current = step.OnSuccessStepId;
                    break;
                default:
                    return reached;
            }
        }
        return reached;
    }

    private async Task<Guid> JobIdAsync(SqlConnection cn, string name, CancellationToken ct)
        => (await GetJobAsync(cn, name, ct))?.JobId
           ?? throw new InvalidOperationException($"Job '{name}' was not found on {_env.ControlServer}.");

    private async Task<(Guid JobId, bool Enabled)?> GetJobAsync(SqlConnection cn, string name, CancellationToken ct)
    {
        await using var cmd = _db.Cmd(cn, "SELECT job_id, enabled FROM msdb.dbo.sysjobs WHERE name = @name;");
        cmd.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = name;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (r.GetGuid(0), r.GetByte(1) == 1);
    }

    private async Task<LiveActivity?> GetLiveActivityAsync(SqlConnection cn, Guid jobId, CancellationToken ct)
    {
        const string sql = """
            SELECT TOP (1) ja.start_execution_date, ja.stop_execution_date,
                           ja.last_executed_step_id, ja.run_requested_source,
                           ja.next_scheduled_run_date
            FROM msdb.dbo.sysjobactivity AS ja
            WHERE ja.job_id = @jobId
              AND ja.session_id = (SELECT MAX(session_id) FROM msdb.dbo.syssessions)
            ORDER BY ja.start_execution_date DESC;
            """;
        await using var cmd = _db.Cmd(cn, sql);
        cmd.Parameters.Add("@jobId", SqlDbType.UniqueIdentifier).Value = jobId;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new LiveActivity(Db.Dt(r, 0), Db.Dt(r, 1), Db.Int(r, 2), Db.Str(r, 3), Db.Dt(r, 4));
    }

    /// <summary>
    /// Bounds the instance_id window belonging to one run. Agent interleaves every job's
    /// rows in sysjobhistory, so without this the step detail of one run bleeds into another.
    /// </summary>
    private async Task<(long Lo, long Hi)> GetRunBoundsAsync(SqlConnection cn, Guid jobId, bool isRunning, CancellationToken ct)
    {
        // Two rows rather than one row of aggregates: nesting a subquery inside MAX() is
        // not legal T-SQL, and TOP (2) over the clustered key is cheaper anyway.
        const string sql = """
            SELECT TOP (2) instance_id
            FROM msdb.dbo.sysjobhistory
            WHERE job_id = @jobId AND step_id = 0
            ORDER BY instance_id DESC;
            """;
        await using var cmd = _db.Cmd(cn, sql);
        cmd.Parameters.Add("@jobId", SqlDbType.UniqueIdentifier).Value = jobId;
        await using var r = await cmd.ExecuteReaderAsync(ct);

        long last = 0, prev = 0;
        if (await r.ReadAsync(ct)) last = Convert.ToInt64(r.GetValue(0));
        if (await r.ReadAsync(ct)) prev = Convert.ToInt64(r.GetValue(0));

        // In flight: everything after the last completed run. Finished: the last run's slice.
        return isRunning ? (last, long.MaxValue) : (prev, last);
    }

    private async Task<OutcomeRow?> GetOutcomeRowAsync(SqlConnection cn, Guid jobId, long instanceId, CancellationToken ct)
    {
        if (instanceId <= 0) return null;
        var sql = $"""
            SELECT h.instance_id, {Dt("h")}, h.run_duration, h.run_status, h.message
            FROM msdb.dbo.sysjobhistory AS h
            WHERE h.job_id = @jobId AND h.step_id = 0 AND h.instance_id = @id;
            """;
        await using var cmd = _db.Cmd(cn, sql);
        cmd.Parameters.Add("@jobId", SqlDbType.UniqueIdentifier).Value = jobId;
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = instanceId;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new OutcomeRow(Convert.ToInt64(r.GetValue(0)), Db.Dt(r, 1), r.GetInt32(2), r.GetInt32(3), Db.Str(r, 4));
    }

    private async Task<List<StepHistoryRow>> GetStepHistoryAsync(
        SqlConnection cn, Guid jobId, long lo, long hi, CancellationToken ct)
    {
        var sql = $"""
            SELECT h.step_id, h.step_name, h.run_status, h.run_duration,
                   {Dt("h")} AS started, h.message, h.retries_attempted
            FROM msdb.dbo.sysjobhistory AS h
            WHERE h.job_id = @jobId
              AND h.step_id > 0
              AND h.instance_id > @lo
              AND h.instance_id < @hi
            ORDER BY h.instance_id;
            """;
        await using var cmd = _db.Cmd(cn, sql);
        cmd.Parameters.Add("@jobId", SqlDbType.UniqueIdentifier).Value = jobId;
        cmd.Parameters.Add("@lo", SqlDbType.BigInt).Value = lo;
        cmd.Parameters.Add("@hi", SqlDbType.BigInt).Value = hi == long.MaxValue ? long.MaxValue : hi;

        var list = new List<StepHistoryRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new StepHistoryRow(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3),
                                        Db.Dt(r, 4), Db.Str(r, 5), r.GetInt32(6)));
        return list;
    }

    private async Task<List<DefinedStep>> GetDefinedStepsAsync(SqlConnection cn, Guid jobId, CancellationToken ct)
    {
        // Whole bodies, not a prefix: a child job's name can be declared anywhere in the
        // step. The whole job is ~60,000 characters on PROD - about 4 KB a second per open
        // browser at the 15-second poll, which is nothing.
        const string sql = """
            SELECT s.step_id, s.step_name, s.subsystem, s.on_success_action, s.on_success_step_id,
                   s.command
            FROM msdb.dbo.sysjobsteps AS s
            WHERE s.job_id = @jobId
            ORDER BY s.step_id;
            """;
        await using var cmd = _db.Cmd(cn, sql);
        cmd.Parameters.Add("@jobId", SqlDbType.UniqueIdentifier).Value = jobId;
        var list = new List<DefinedStep>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var command = Db.Str(r, 5);
            list.Add(new DefinedStep(
                r.GetInt32(0), r.GetString(1), r.GetString(2).Trim(),
                Convert.ToInt32(r.GetValue(3)), Convert.ToInt32(r.GetValue(4)),
                StepBody.IsNoOp(command),
                command is null ? "" : StepBody.StripComments(command)));
        }
        return list;
    }

    /// <summary>
    /// Which step launches each allowlisted child job, read from the steps themselves.
    /// <para>
    /// <c>ChildJobs</c> in configuration is the ALLOWLIST - the only jobs the console may
    /// show or start. Its StepId used to decide where each job was shown too, and went
    /// wrong every time a step was inserted above it. Now the step whose body names the job
    /// wins; the configured number is used only for a job no step names (so nothing
    /// vanishes), and a disagreement is reported on the step rather than believed.
    /// </para>
    /// </summary>
    private (Dictionary<int, List<string>> ByStep, Dictionary<int, List<string>> Warnings)
        MapChildJobs(List<DefinedStep> defined)
    {
        var byStep = new Dictionary<int, List<string>>();
        var warnings = new Dictionary<int, List<string>>();
        var moved = new Dictionary<(int Configured, int Actual), List<string>>();

        void Add(Dictionary<int, List<string>> d, int step, string value)
        {
            if (!d.TryGetValue(step, out var list)) d[step] = list = [];
            if (!list.Contains(value, StringComparer.OrdinalIgnoreCase)) list.Add(value);
        }

        foreach (var map in _env.ChildJobs)
        {
            foreach (var name in map.Jobs)
            {
                var found = defined.FirstOrDefault(d => StepBody.NamesJob(d.Body, name));
                if (found is not null)
                {
                    Add(byStep, found.StepId, name);
                    if (map.StepId != 0 && map.StepId != found.StepId)
                    {
                        if (!moved.TryGetValue((map.StepId, found.StepId), out var names))
                            moved[(map.StepId, found.StepId)] = names = [];
                        names.Add(name);
                    }
                }
                else if (defined.Any(d => d.StepId == map.StepId))
                {
                    // No step names it: fall back to config rather than dropping it.
                    Add(byStep, map.StepId, name);
                }
            }
        }
        // One sentence per step, however many of its jobs moved.
        foreach (var ((configured, actual), names) in moved)
        {
            var list = string.Join(", ", names.Select(n => $"'{n}'"));
            Add(warnings, actual,
                $"appsettings.json maps {list} to step {configured}, but this step is the one that starts "
                + $"{(names.Count == 1 ? "it" : "them")} - the console is following the step. Update ChildJobs.");
        }
        return (byStep, warnings);
    }

    private async Task<ChildJobStatus?> GetChildStatusAsync(SqlConnection cn, string name, CancellationToken ct)
    {
        var job = await GetJobAsync(cn, name, ct);
        if (job is null)
            return new ChildJobStatus(name, false, Outcome.Unknown, false, null, null, null, null,
                                      "Job not found on this instance.");

        var (jobId, enabled) = job.Value;
        var live = await GetLiveActivityAsync(cn, jobId, ct);
        var isRunning = live is { Started: not null, Stopped: null };

        var (lo, hi) = await GetRunBoundsAsync(cn, jobId, isRunning, ct);
        var outcome = isRunning ? null : await GetOutcomeRowAsync(cn, jobId, hi, ct);

        int? failedStepId = null;
        string? failedStepName = null;
        if (outcome is { RunStatus: 0 })
        {
            var steps = await GetStepHistoryAsync(cn, jobId, lo, hi, ct);
            var failed = steps.LastOrDefault(s => s.RunStatus == 0);
            failedStepId = failed?.StepId;
            failedStepName = failed?.StepName;
        }

        return new ChildJobStatus(
            JobName: name,
            JobEnabled: enabled,
            Outcome: isRunning ? Outcome.Running : outcome?.RunStatus switch
            {
                1 => Outcome.Succeeded,
                0 => Outcome.Failed,
                2 => Outcome.Retry,
                3 => Outcome.Canceled,
                _ => Outcome.Unknown
            },
            IsRunning: isRunning,
            StartedLocal: isRunning ? live?.Started : outcome?.StartedLocal,
            DurationSeconds: isRunning || outcome is null ? null : Db.DurationToSeconds(outcome.RunDuration),
            FailedStepId: failedStepId,
            FailedStepName: failedStepName,
            Message: outcome?.Message);
    }

    private async Task<List<ScheduleInfo>> GetSchedulesAsync(SqlConnection cn, Guid jobId, CancellationToken ct)
    {
        const string sql = """
            SELECT sch.name, sch.enabled,
                   js.next_run_date, js.next_run_time
            FROM msdb.dbo.sysjobschedules AS js
            JOIN msdb.dbo.sysschedules AS sch ON sch.schedule_id = js.schedule_id
            WHERE js.job_id = @jobId
            ORDER BY sch.name;
            """;
        await using var cmd = _db.Cmd(cn, sql);
        cmd.Parameters.Add("@jobId", SqlDbType.UniqueIdentifier).Value = jobId;

        var list = new List<ScheduleInfo>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var nextDate = Db.Int(r, 2) ?? 0;
            var nextTime = Db.Int(r, 3) ?? 0;
            var raw = ToDateTime(nextDate, nextTime);

            // Agent leaves this column pointing at a run that has already happened until
            // it next recalculates, so a past value is reported as unknown, not as a date.
            var isStale = raw is not null && raw <= DateTime.Now;
            var enabled = r.GetInt32(1) == 1;

            list.Add(new ScheduleInfo(
                r.GetString(0),
                enabled,
                // Disabled first: a disabled schedule keeps whatever next-run value it had
                // when it was switched off, so "stale" would be a misleading reason.
                !enabled ? "disabled - will not fire"
                    : nextDate == 0 ? "no next run"
                    : isStale ? "next run not yet recalculated by Agent"
                    : "scheduled",
                enabled && !isStale ? raw : null));
        }
        return list;
    }

    /// <summary>
    /// When the job will next fire.
    /// <para>
    /// <c>sysjobschedules.next_run_date</c> is the obvious column and the wrong one to
    /// trust: Agent does not refresh it as soon as a run finishes, so for the rest of the
    /// day it still points at the run that already happened - which showed up as a "Next
    /// scheduled" identical to "Started". <c>sysjobactivity.next_scheduled_run_date</c> is
    /// Agent's live calculation and is already correct.
    /// </para>
    /// <para>
    /// Either way, a next run in the past is by definition stale, so it is discarded
    /// rather than displayed. Showing nothing beats showing a date that has been.
    /// </para>
    /// </summary>
    private static DateTime? NextScheduled(LiveActivity? live, List<ScheduleInfo> schedules)
    {
        var now = DateTime.Now;

        if (live?.NextScheduledRun is { } fromAgent && fromAgent > now)
            return fromAgent;

        return schedules
            .Where(s => s.Enabled && s.NextRunLocal > now)
            .Select(s => s.NextRunLocal)
            .DefaultIfEmpty(null)
            .Min();
    }

    private static Diagnosis? ToDiagnosis(FailureRule? rule) =>
        rule is null ? null : new Diagnosis(rule.RuleId, rule.Title, rule.Cause, rule.Solutions);

    private static DateTime? ToDateTime(int yyyymmdd, int hhmmss)
    {
        if (yyyymmdd <= 0) return null;
        try
        {
            return new DateTime(yyyymmdd / 10000, yyyymmdd / 100 % 100, yyyymmdd % 100,
                                hhmmss / 10000, hhmmss / 100 % 100, hhmmss % 100);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static string? DescribeSource(string? source) => source?.Trim() switch
    {
        "1" => "Scheduler",
        "2" => "Alerter",
        "3" => "Boot",
        "4" => "User",
        "6" => "On idle schedule",
        null or "" => null,
        var other => other
    };

    /// <summary>Agent writes "The Job was invoked by User DOMAIN\name." into the outcome message.</summary>
    private static string? ParseInvokedBy(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var m = InvokedByRegex().Match(message);
        return m.Success ? m.Groups[1].Value.Trim().TrimEnd('.') : null;
    }

    [GeneratedRegex(@"invoked by\s+(?:User\s+)?([^\.]+?)(?:\.|\s*The last step)", RegexOptions.IgnoreCase)]
    private static partial Regex InvokedByRegex();
}
