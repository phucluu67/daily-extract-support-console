namespace ExtractConsole.Config;

/// <summary>Root of appsettings.json.</summary>
public sealed class AppConfig
{
    public List<EnvironmentConfig> Environments { get; set; } = [];

    /// <summary>Seconds between dashboard polls. The UI may ask for slower, never faster.</summary>
    public int PollSeconds { get; set; } = 15;

    /// <summary>Where the action audit log is written. Supports %ENVVAR% expansion.</summary>
    public string AuditLogPath { get; set; } = @"%LOCALAPPDATA%\ExtractConsole\audit.log";

    /// <summary>Settings for the unattended checker (--check).</summary>
    public CheckConfig Check { get; set; } = new();

    /// <summary>Who may use the console, and how much of it. See <see cref="AccessConfig"/>.</summary>
    public AccessConfig Access { get; set; } = new();

    public EnvironmentConfig? Find(string name) =>
        Environments.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed class EnvironmentConfig
{
    public string Name { get; set; } = "";

    /// <summary>
    /// False means "declared but not yet configured" - the UI lists it greyed out and the
    /// API refuses to act on it. Lets TEST/PROD ship as visible placeholders rather than
    /// silently-wrong connection strings.
    /// </summary>
    public bool Enabled { get; set; }

    public bool IsDefault { get; set; }

    /// <summary>
    /// Drives a standing warning banner and a louder confirmation wording. Purely a
    /// human safeguard - the server enforces the same rules either way - but the DEV and
    /// PROD instance names differ by one character (roeeps914q / roeeps014q), so "which
    /// environment am I looking at" needs to be unmissable.
    /// </summary>
    public bool IsProduction { get; set; }

    /// <summary>Instance hosting SQL Agent, msdb and SSISDB - the control plane.</summary>
    public string ControlServer { get; set; } = "";

    /// <summary>Instance hosting the recon/ODS data - a different box from ControlServer.</summary>
    public string DataServer { get; set; } = "";

    public string DataDatabase { get; set; } = "EPSI_ODS";
    public string AuditDatabase { get; set; } = "STRATA_AUDIT";

    /// <summary>Holds dbo.etl_audit_log, the per-package rows_extracted the volume tab charts.</summary>
    public string StagingDatabase { get; set; } = "epsi_staging";

    /// <summary>
    /// Instance holding the shared failure catalogue (diag.FailureRule). Blank means the
    /// control instance. Kept as its own setting rather than hard-wired to Control so the
    /// catalogue can be relocated by editing configuration - EpsiSsisHost is scheduled for
    /// decommission in early 2027 and the move will be a settings change plus a re-run of
    /// sql/20_install_diagnostics.sql, not a code change.
    /// </summary>
    public string DiagnosticsServer { get; set; } = "";

    /// <summary>Database on <see cref="DiagnosticsServer"/> holding diag.FailureRule.</summary>
    public string DiagnosticsDatabase { get; set; } = "EpsiSsisHost";

    /// <summary>Falls back to the control instance when no diagnostics server is set.</summary>
    public string EffectiveDiagnosticsServer =>
        string.IsNullOrWhiteSpace(DiagnosticsServer) ? ControlServer : DiagnosticsServer;

    /// <summary>
    /// UNC path the extract writes its output files to. Transient - the CLEO/FTP pickup
    /// collects from here - and used by the reconciliation panel for the counts file and
    /// record counting, not by the Output files tab.
    /// </summary>
    public string FileShare { get; set; } = "";

    /// <summary>
    /// Where CLEO archives what it actually transferred. This is what the Output files
    /// tab lists, because it is the record of what Strata was really sent (and sent back),
    /// rather than whatever happens to be sitting in the staging folder at the moment.
    /// Files here are renamed "<original>.yyyyMMdd_HHmmssfff_OUT" (or _IN).
    /// </summary>
    public string ArchiveShare { get; set; } = "";

    public string JobName { get; set; } = "";

    /// <summary>
    /// Every job this app is allowed to touch. Acts as the authorisation allowlist:
    /// a job name that is not the parent or one of these is rejected server-side,
    /// so the console can never be driven as a general-purpose job runner.
    /// </summary>
    public List<ChildJobMap> ChildJobs { get; set; } = [];

    /// <summary>
    /// Steps that are commented out and short-circuit with RETURN. They report success
    /// without doing anything, so the dashboard must not paint them green.
    /// </summary>
    public List<DisabledStep> DisabledSteps { get; set; } = [];

    // No SSIS package is configured here on purpose: the extract runs ~28 of them across
    // its child jobs, and the list is read from the jobs' own SSIS step definitions so it
    // cannot fall out of step with the server.

    public List<ReconTable> ReconTables { get; set; } = [];

    /// <summary>
    /// What the first field of a data record looks like, used to count records rather
    /// than physical lines. Every extract file starts each record with a 10-digit
    /// EncounterRecordNumber, so a line that does not match is a continuation of the
    /// previous record - the result of an embedded CR/LF in a field value.
    /// </summary>
    public string RecordStartPattern { get; set; } = @"^\d{10}\|";

    /// <summary>Set true only where the instance presents a self-signed certificate.</summary>
    public bool TrustServerCertificate { get; set; } = true;

    public int ConnectTimeoutSeconds { get; set; } = 15;
    public int CommandTimeoutSeconds { get; set; } = 60;

    public IEnumerable<string> AllJobNames()
    {
        yield return JobName;
        foreach (var c in ChildJobs)
            foreach (var n in c.Jobs)
                yield return n;
    }
}

/// <summary>
/// Three roles, each granted to AD groups or to individual accounts ("MFAD\m012345").
/// <para>
/// Operator - everything, including start/stop/restart, AdHoc changes and catalogue rules.
/// Viewer   - every tab, no changes.
/// Finance  - Run history, Extract volume, Counts and reconciliation, Transferred files; no changes.
/// </para>
/// <para>
/// A person in more than one list gets the widest role. Someone in none is refused. If all
/// three lists are empty the section is treated as not configured and everyone is an
/// Operator - the behaviour before roles existed - so an old appsettings.json keeps working.
/// </para>
/// <para>
/// IMPORTANT: in the per-PC version this file sits beside the exe, on the user's own
/// machine, so a determined user could edit it. There the roles are a convenience and the
/// real boundary is still that person's SQL Server and NTFS permissions. They only become
/// a security boundary once the console is hosted and this file lives on the server.
/// </para>
/// </summary>
public sealed class AccessConfig
{
    public List<string> Operators { get; set; } = [];
    public List<string> Viewers { get; set; } = [];
    public List<string> Finance { get; set; } = [];

    /// <summary>Environments a Viewer may see. Operators always see all enabled environments.</summary>
    public List<string> ViewerEnvironments { get; set; } = ["PROD"];

    /// <summary>Environments Finance may see.</summary>
    public List<string> FinanceEnvironments { get; set; } = ["PROD"];

    public bool IsConfigured => Operators.Count + Viewers.Count + Finance.Count > 0;
}

public sealed class CheckConfig
{
    /// <summary>Semicolon-separated. Empty means report to the console only.</summary>
    public string Recipients { get; set; } = "";

    /// <summary>Database Mail profile. Empty uses the instance's default profile.</summary>
    public string MailProfile { get; set; } = "";

    /// <summary>
    /// How long without a run before that is itself the finding. 26h by default so a
    /// daily 07:00 job has slack for a long run or a late start without crying wolf.
    /// </summary>
    public int MaxHoursSinceRun { get; set; } = 26;

    /// <summary>
    /// Flag a dataset whose row count falls below this fraction of the same dataset at
    /// the last clean check. 0 disables the comparison.
    /// </summary>
    public double VolumeDropFactor { get; set; } = 0.5;

    /// <summary>
    /// Read the output files to count records rather than trusting line counts. The
    /// checker has time the UI does not, and it is the only way to tell a split record
    /// from a missing one.
    /// </summary>
    public bool CountRecords { get; set; } = true;
}

public sealed class ChildJobMap
{
    /// <summary>Parent step that launches these jobs via sp_start_job.</summary>
    public int StepId { get; set; }
    public List<string> Jobs { get; set; } = [];
}

public sealed class DisabledStep
{
    public int StepId { get; set; }
    public string Note { get; set; } = "";
}

public sealed class ReconTable
{
    /// <summary>Logical name used in the extract's own file map (e.g. HBCharge).</summary>
    public string Key { get; set; } = "";
    public string Schema { get; set; } = "dbo";
    public string Table { get; set; } = "";
    /// <summary>File this table is reconciled against, with {date} as MMddyyyy.</summary>
    public string FilePattern { get; set; } = "";
}
