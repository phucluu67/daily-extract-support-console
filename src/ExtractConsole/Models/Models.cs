namespace ExtractConsole.Models;

/// <summary>
/// Outcome vocabulary shared by jobs, steps and SSIS executions so the UI has one
/// switch to style. Keep these strings stable - the front end keys off them.
/// </summary>
public static class Outcome
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Retry = "retry";
    public const string Canceled = "canceled";
    public const string Skipped = "skipped";    // run ended before this step was reached
    public const string Pending = "pending";    // run is in flight and has not got here yet
    public const string NoOp = "noop";          // step ran but is commented out - see DisabledSteps
    public const string Unknown = "unknown";
}

public sealed record EnvironmentInfo(
    string Name,
    bool Enabled,
    bool IsDefault,
    bool IsProduction,
    string ControlServer,
    string DataServer,
    string FileShare,
    string ArchiveShare,
    string JobName,
    /// <summary>Where the Recon_Current upsert log lives, for the panel's caption.</summary>
    string UpsertLogLocation,
    /// <summary>Where etl_audit_log lives, for the volume tab's caption.</summary>
    string VolumeLogLocation);

public sealed record JobStatus(
    string Environment,
    string JobName,
    bool JobEnabled,
    string Outcome,
    bool IsRunning,
    DateTime? StartedLocal,
    DateTime? FinishedLocal,
    int? DurationSeconds,
    int? ElapsedSeconds,
    int? CurrentStepId,
    int? FailedStepId,
    string? Message,
    string? InvokedBy,
    long? RunInstanceId,
    DateTime? NextScheduledLocal,
    List<StepStatus> Steps,
    List<ScheduleInfo> Schedules);

public sealed record StepStatus(
    int StepId,
    string StepName,
    string Subsystem,
    string Outcome,
    DateTime? StartedLocal,
    int? DurationSeconds,
    string? Message,
    int RetriesAttempted,
    /// <summary>
    /// True when the step's own body is commented out, so it reports success while doing
    /// nothing. Read from the step definition, not from configuration.
    /// </summary>
    bool IsNoOp,
    string? DisabledNote,
    /// <summary>
    /// Set when configuration disagrees with the step as it actually stands on the
    /// server - the signal that a <c>DisabledSteps</c> entry has gone stale.
    /// </summary>
    string? ConfigWarning,
    /// <summary>
    /// False when following the job's own success transitions from step 1 never arrives
    /// here - an earlier step quits reporting success first. Such a step only ever runs
    /// if someone starts the job at it explicitly.
    /// </summary>
    bool ReachableFromStart,
    /// <summary>
    /// The catalogue's explanation for this step's error, when one matches. Shown on the
    /// step itself rather than in a tab - a diagnosis nobody navigates to is no use.
    /// </summary>
    Diagnosis? Diagnosis,
    List<ChildJobStatus> ChildJobs);

public sealed record ChildJobStatus(
    string JobName,
    bool JobEnabled,
    string Outcome,
    bool IsRunning,
    DateTime? StartedLocal,
    int? DurationSeconds,
    int? FailedStepId,
    string? FailedStepName,
    string? Message);

public sealed record ScheduleInfo(
    string Name,
    bool Enabled,
    string Description,
    DateTime? NextRunLocal);

public sealed record RunHistoryItem(
    long InstanceId,
    DateTime StartedLocal,
    int DurationSeconds,
    string Outcome,
    int? FailedStepId,
    string? FailedStepName,
    string? InvokedBy,
    string? Message);

/// <summary>The slice of time one run of the parent job occupied.</summary>
public sealed record RunWindow(
    long? InstanceId,
    DateTime From,
    DateTime To,
    bool IsRunning);

/// <summary>
/// One SSIS package the extract is built from, paired with how it went in a given run.
/// A package with no execution in the window is reported with Status "not run" rather
/// than omitted - a package that should have run and did not is the interesting case.
/// </summary>
public sealed record SsisRunPackage(
    string JobName,
    int StepId,
    string StepName,
    string Folder,
    string Project,
    string Package,
    long? ExecutionId,
    string Status,
    DateTime? StartLocal,
    DateTime? EndLocal,
    int? DurationSeconds,
    string? CallerName,
    int Attempts);

public sealed record SsisRunReport(
    RunWindow? Window,
    List<SsisRunPackage> Packages,
    int Total,
    int Succeeded,
    int Failed,
    int Running,
    int NotRun);

public sealed record SsisMessage(
    DateTime? TimeLocal,
    string MessageType,
    string SourceName,
    string Message);

public sealed record ReconCount(
    string Key,
    string TableName,
    long? TableRows,
    bool TableRowsExact,
    string? FileName,
    long? FileLines,
    /// <summary>
    /// Records counted by reading the file, as opposed to physical lines. Differs from
    /// FileLines when a field value contains an embedded CR/LF and splits a record
    /// across lines. Null unless the caller asked for the file to be read.
    /// </summary>
    long? FileRecords,
    /// <summary>
    /// Physical lines that continue a previous record rather than starting a new one -
    /// the direct measure of how malformed the file is. Null unless records were counted.
    /// </summary>
    long? ContinuationLines,
    long? FileSizeKb,
    DateTime? FileModifiedLocal,
    long? Difference,
    string Status)
{
    /// <summary>
    /// 0 for an unsplit file; otherwise how many numbered parts (_f1, _f2, ...) the file was
    /// split into to stay under the 1 GB limit. Every count above is the sum across parts.
    /// </summary>
    public int FileParts { get; init; }

    /// <summary>Anything off about the files found - a missing part, a disagreement in part counts.</summary>
    public string? FileNote { get; init; }
}

/// <summary>Reconciliation verdicts. Distinct from job outcomes.</summary>
public static class ReconStatus
{
    /// <summary>Records reconcile AND the file is one record per line.</summary>
    public const string Match = "succeeded";

    /// <summary>The record count does not agree with the table. Real data problem.</summary>
    public const string Short = "failed";

    /// <summary>
    /// The table and the file agree, and both are empty. Arithmetically a match, which is
    /// exactly why it must not be reported as one: an extract that produced nothing
    /// reconciles perfectly against a file containing nothing.
    /// </summary>
    public const string Empty = "empty";

    /// <summary>
    /// Records reconcile, but the file contains continuation lines - a field value holds
    /// an embedded CR/LF. No data is lost; the file is still malformed for a line-based
    /// reader, so this stays flagged rather than being counted away.
    /// </summary>
    public const string ExtraLines = "artifact";

    public const string Unknown = "unknown";
}

public sealed record OutputFile(
    /// <summary>The file as CLEO stored it, suffix and all.</summary>
    string Name,
    /// <summary>The name before CLEO's transfer suffix - what Strata actually received.</summary>
    string OriginalName,
    /// <summary>"OUT" sent to Strata, "IN" received from Strata, "" if unrecognised.</summary>
    string Direction,
    long SizeBytes,
    DateTime ModifiedLocal,
    /// <summary>Transfer time parsed from the suffix; null when the name does not carry one.</summary>
    DateTime? TransferredLocal);

// ---- unattended check ----------------------------------------------------

public sealed record CheckFinding(
    /// <summary>Critical, Warning or Info.</summary>
    string Severity,
    /// <summary>Stable code - NoRecentRun, RunFailed, EmptyExtract, ReconShort, SplitRecords, VolumeDrop.</summary>
    string Code,
    string Title,
    string? Detail);

public sealed record CheckReport(
    string Environment,
    long? RunInstanceId,
    DateTime? RunStartedLocal,
    string? RunOutcome,
    List<CheckFinding> Findings,
    List<ReconCount> Counts,
    bool Notified,
    string? NotifyError)
{
    /// <summary>
    /// True when this run has already been mailed about. A repeating schedule has to
    /// notice the same problem every tick without telling anyone twice.
    /// </summary>
    public bool AlreadyReported { get; init; }

    /// <summary>Named in the mail so DEV and PROD cannot be mistaken for each other.</summary>
    public string ControlServer { get; init; } = "";

    public int Critical => Findings.Count(f => f.Severity == "Critical");
    public int Warnings => Findings.Count(f => f.Severity == "Warning");
}

// ---- recon_current upsert log --------------------------------------------

/// <summary>
/// One row of STRATA_AUDIT.dbo.DailyReconCurrentProcessLog - what the extract's
/// "Upsert Process for Recon_Current Tables" step did to one dataset on one day.
/// </summary>
public sealed record UpsertLogRow(
    long ProcessLogId,
    DateTime ProcessDate,
    string ProcessName,
    /// <summary>Dataset key (HBCharge, PBEncounter, ...) derived from ProcessName so this
    /// lines up with the reconciliation panel. Null when the name does not parse.</summary>
    string? DatasetKey,
    /// <summary>Shared outcome vocabulary, not the raw SUCCESS/FAILED text.</summary>
    string Outcome,
    string RawStatus,
    long RowsInSource,
    long RowsUpdated,
    long RowsInserted,
    long TotalRowsProcessed,
    DateTime FinishedLocal,
    string? ErrorMessage);

/// <summary>
/// How one dataset's split moved over the recorded days, newest first. The point of
/// the panel: 274k updated / 187k inserted means nothing on its own, and a great deal
/// against yesterday.
/// </summary>
public sealed record UpsertTrendPoint(
    DateTime ProcessDate,
    long RowsUpdated,
    long RowsInserted,
    long TotalRowsProcessed,
    string Outcome);

public sealed record UpsertTrend(
    string ProcessName,
    string? DatasetKey,
    List<UpsertTrendPoint> Points);

/// <summary>
/// One selectable day, carrying enough for the picker to say which days went wrong
/// without the panel having to show another day's failures to do it.
/// </summary>
public sealed record UpsertDay(DateTime Date, int RowCount, int FailureCount);

public sealed record UpsertLogReport(
    /// <summary>The day being shown. Null when the table holds nothing at all.</summary>
    DateTime? ProcessDate,
    /// <summary>Every day present in the table, newest first, for the picker.</summary>
    List<UpsertDay> AvailableDays,
    List<UpsertLogRow> Rows,
    /// <summary>
    /// Non-success rows for the SELECTED day only. A day that went cleanly reports no
    /// failures, even where earlier days have them - the panel describes the day you
    /// chose. Which other days went wrong is carried by
    /// <see cref="UpsertDay.FailureCount"/> on the picker instead.
    /// </summary>
    List<UpsertLogRow> Failures,
    List<UpsertTrend> Trends,
    int DistinctDays,
    long TotalRows)
{
    /// <summary>
    /// Datasets that logged the same day more than once - a retry. 2026-09-30 has 15 rows
    /// for 11 processes, and nothing else in the panel would say so.
    /// </summary>
    public List<string> RetriedToday =>
        Rows.GroupBy(r => r.ProcessName)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
}

// ---- extract volume (epsi_staging.dbo.etl_audit_log) ---------------------

/// <summary>One dataset on one day.</summary>
public sealed record VolumePoint(
    DateTime Date,
    /// <summary>
    /// Rows from the LAST successful run that day - the load that stuck. Null when no run
    /// succeeded that day, which is a different thing from a run that succeeded with 0.
    /// </summary>
    long? Rows,
    int SucceededRuns,
    int FailedRuns);

public sealed record VolumeSummary(
    long? Latest,
    DateTime? LatestDate,
    /// <summary>
    /// MEDIAN of days with rows - "a typical day". A median, not a mean, so one full reload
    /// (DEV pulled 80M PB ICD10Dx rows on 2026-09-09) does not pass for normal volume; and
    /// zero days excluded, or the weekly empty run drags it down.
    /// </summary>
    long? Typical,
    long? Peak,
    DateTime? PeakDate,
    /// <summary>Mean of the last 7 non-zero days in range, and of the 7 before them.</summary>
    long? Last7Average,
    long? Prior7Average,
    /// <summary>Days a run SUCCEEDED and reported 0 rows.</summary>
    int ZeroDays);

public sealed record VolumeSeries(
    string Key,
    string Label,
    List<VolumePoint> Points,
    VolumeSummary Summary,
    /// <summary>Package names seen for this dataset in range - they changed over time on DEV.</summary>
    List<string> Packages);

public sealed record VolumeReport(
    DateTime From,
    DateTime To,
    /// <summary>The span the log actually covers, for the "All" preset.</summary>
    DateTime? EarliestInLog,
    DateTime? LatestInLog,
    List<VolumeSeries> Series,
    /// <summary>Per day, the sum across datasets that have a value.</summary>
    List<VolumePoint> Total,
    /// <summary>
    /// Days on which every dataset that ran succeeded with 0 rows - the silent empty run.
    /// On PROD that has been every Thursday since 2026-05-28.
    /// </summary>
    List<DateTime> AllZeroDays,
    /// <summary>Package names in range that map to none of the ten datasets (e.g. TEST).</summary>
    List<string> Ignored);

// ---- AdHoc HAR lists -----------------------------------------------------

/// <summary>One of the ten AdhocHARList files the staging packages read.</summary>
public sealed record AdhocListInfo(
    string Key,
    string FileName,
    /// <summary>Column names in file order, e.g. HSP_ACCOUNT_ID, TX_ID.</summary>
    List<string> Columns,
    bool Exists,
    int Entries,
    long SizeBytes,
    DateTime? ModifiedLocal,
    /// <summary>The header line exactly as it is in the file - case varies between files.</summary>
    string? Header,
    string? Error);

public sealed record AdhocListDetail(
    AdhocListInfo Info,
    /// <summary>The last entries in the file, newest (bottom) last. Appends land here.</summary>
    List<string> Tail,
    string? Search,
    /// <summary>Entries containing the search text, capped.</summary>
    List<string> Matches,
    int MatchCount);

/// <summary>
/// An append or remove. With Commit false it is a preview: everything is parsed and
/// validated, and nothing is written.
/// </summary>
public sealed record AdhocChangeRequest(
    string? Environment,
    /// <summary>Typed or pasted lines.</summary>
    string? Text,
    /// <summary>Name of an uploaded file, to tell an .xlsx from text.</summary>
    string? FileName,
    /// <summary>An uploaded .xlsx, base64. Text uploads arrive in <see cref="Text"/>.</summary>
    string? FileBase64,
    string? Confirm,
    bool Commit);

public sealed record AdhocRejected(int Line, string Value, string Reason);

public sealed record AdhocChangeResult(
    bool Success,
    string Message,
    string Mode,
    bool Committed,
    /// <summary>Entries that will be (or were) appended or removed.</summary>
    int ChangeCount,
    /// <summary>The first of those entries, for the preview.</summary>
    List<string> Sample,
    /// <summary>Append: already in the list, so skipped.</summary>
    int AlreadyPresent,
    /// <summary>Listed more than once in the input; counted once.</summary>
    int DuplicateInInput,
    /// <summary>Remove: not in the list.</summary>
    int NotFound,
    /// <summary>Line 1 was taken as a header and skipped - said so, never silent.</summary>
    string? HeaderSkipped,
    List<AdhocRejected> Rejected,
    int EntriesBefore,
    int EntriesAfter);

// ---- failure diagnostics -------------------------------------------------

public sealed record FailureRule(
    int RuleId,
    bool IsEnabled,
    string Pattern,
    string MatchType,
    string Title,
    string? Cause,
    int Priority,
    string? AddedBy,
    DateTime? AddedUtc,
    /// <summary>Ordered candidate fixes, lowest Ordinal first.</summary>
    List<FailureSolution> Solutions);

/// <summary>
/// One thing to try for a known failure. A list rather than a single block of prose:
/// most of these have several candidate fixes, they get added by different people at
/// different times, and the order is the advice.
/// </summary>
public sealed record FailureSolution(
    int SolutionId,
    int Ordinal,
    string Solution,
    string? Detail,
    /// <summary>Someone confirmed this actually resolved the failure.</summary>
    bool IsVerified,
    string? AddedBy);

/// <summary>A rule that matched a particular step, as shown on the failing step.</summary>
public sealed record Diagnosis(
    int RuleId,
    string Title,
    string? Cause,
    List<FailureSolution> Solutions);

public sealed record DiagnosedFailure(
    string JobName,
    int StepId,
    string StepName,
    DateTime? WhenLocal,
    string? Message,
    int? RuleId,
    string? Title,
    string? Cause,
    List<FailureSolution> Solutions);

public sealed record DiagnosticsReport(
    List<FailureRule> Rules,
    List<DiagnosedFailure> RecentFailures,
    int Total,
    int Explained,
    int Unexplained);

public sealed record NewFailureRule(
    string? Pattern,
    string? MatchType,
    string? Title,
    string? Cause,
    /// <summary>One candidate fix per line; blank lines ignored.</summary>
    string? Solutions,
    int Priority,
    string? Environment);

/// <summary>A partial result: one panel failing must not blank the whole dashboard.</summary>
public sealed record Panel<T>(T? Data, string? Error)
{
    public static Panel<T> Ok(T data) => new(data, null);
    public static Panel<T> Fail(string error) => new(default, error);
}

public sealed record ActionRequest(string Environment, string? JobName, int? FromStepId, string? Confirm);

public sealed record ActionResult(bool Success, string Message, string? Detail);
