<#
.SYNOPSIS
    Generates the Agent-job script(s) by embedding checker.ps1 into them.

.DESCRIPTION
    Nothing may be stored on the SQL Server hosts, so the checker cannot live on
    disk there - it has to travel inside the Agent job step itself. That means a
    few hundred lines of PowerShell inside a T-SQL string literal, with every
    single quote doubled. Hand-maintaining that is how mistakes get made, so
    checker.ps1 stays readable and this builds the deployable script from it.

    One file is produced PER ENVIRONMENT, with the environment baked in:

        23_create_checker_job_DEV.sql
        23_create_checker_job_PROD.sql

    Deliberately not one file with a variable to edit. The two host names differ
    by a single character (roeeps914q / roeeps014q) and this project has already
    been bitten by that; "run the right file" is safer than "remember to change
    the right line".

    Run after editing checker.ps1, then run the generated .sql on the instance.

.EXAMPLE
    pwsh -File sql\Build-CheckerJobScript.ps1
#>

[CmdletBinding()]
param(
    [string]$Source = "$PSScriptRoot\checker.ps1",
    [string]$OutputDirectory = $PSScriptRoot,
    [ValidateSet('DEV', 'PROD')]
    [string[]]$Environments = @('DEV', 'PROD')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Source)) { throw "Source not found: $Source" }

$original = (Get-Content $Source -Raw) -replace "`r`n", "`n"

# The default in checker.ps1's param block is what the job step ends up running,
# because an inline script block has nothing to bind arguments from. Baking the
# environment in there is how each generated file targets its own instance.
# Single-quoted: the $ here is regex/literal, not interpolation.
$defaultPattern = '(?m)^(\s*\[string\]\$EnvironmentName\s*=\s*)''[A-Z]+'''

<#
    SQL Agent runs TOKEN REPLACEMENT over a job step's command text, where
    $(...) means a job token such as $(ESCAPE_SQUOTE(JOBNAME)). A PowerShell
    subexpression is therefore an unrecognised token and the step will not even
    parse - the job fails before running a single line with:

        Unable to start execution of step 1 (reason: line(NNN): Syntax error)

    which names a line but says nothing about why. Catch it here instead: use
    concatenation or the -f operator in checker.ps1, never "$($x.Y)".
#>
$subexpressions = [regex]::Matches($original, '\$\(')
if ($subexpressions.Count -gt 0) {
    $lines = $original -split "`n"
    $hits = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '\$\(') { $hits += ("    line {0}: {1}" -f ($i + 1), $lines[$i].Trim()) }
    }
    throw ("checker.ps1 contains {0} PowerShell subexpression(s) `$(...). SQL Agent reads those as job tokens and the step will not parse. Use concatenation or -f instead:`n{1}" -f `
           $subexpressions.Count, ($hits -join "`n"))
}
if ($original -notmatch $defaultPattern) {
    throw "Could not find the `$EnvironmentName default in $Source. Did its param block change?"
}

foreach ($envName in $Environments) {

    $ps = [regex]::Replace($original, $defaultPattern, "`${1}'$envName'")

    # The one escape that matters: ' -> '' so the whole script survives as a
    # single N'...' literal.
    $psEscaped = $ps.Replace("'", "''")

    if ($ps.Length -gt 60000) {
        Write-Warning "checker.ps1 is $($ps.Length) characters. That is a lot to carry in a job step."
    }

    $header = @"
/* =============================================================================
   Creates the Agent job that runs the unattended checker, targeting $envName.

   ***  GENERATED FILE - DO NOT EDIT  ***
   Edit sql\checker.ps1, then re-run sql\Build-CheckerJobScript.ps1.
   Editing the step in SSMS loses the change on the next build.

   RUN THIS ON $envName ONLY. There is a separate file for the other
   environment; the environment is baked into the script, not a variable to
   change. The two host names differ by one character.

   HOW TO RUN IT
     SSMS    : just open and execute. Leave SQLCMD Mode OFF (the default).
     sqlcmd  : you MUST pass -x, e.g.
                   sqlcmd -S roeeps914q,57015 -E -C -b -x -i <this file>
               Without -x, sqlcmd tries to expand the PowerShell's `$(...)
               subexpressions as its own scripting variables and fails with
               "Syntax error ... near command '`$'".

   Created DISABLED. Validate it by starting it by hand, then enable it.

   ---------------------------------------------------------------------------
   WHY THE SCRIPT IS CARRIED INLINE
   ---------------------------------------------------------------------------
   Nothing may be stored on the SQL Server hosts. SQL Agent runs ON the SQL
   Server, so "install it on the Agent host" and "put a binary on the SQL box"
   are the same sentence - there is no separate agent machine. Shares belonging
   to other teams are not an option either; the StrataJazz share is CLEO's.

   So the script travels inside the job step, which is exactly what extract
   steps 3, 9, 10, 11 and 14 already do. Nothing is deployed, nothing needs
   approving, and nothing is left on anyone else's disk.

   ---------------------------------------------------------------------------
   BEFORE RUNNING THIS
   ---------------------------------------------------------------------------
   1. Run 20_, 21_ and 22_ on this instance first - the checker reads the
      failure catalogue and writes diag.CheckRun / diag.CheckCount.

   2. Set the recipients. They are EMPTY by default, so until you set them the
      checker reports to the job history and mails nobody. Edit `$AllConfig in
      checker.ps1 and rebuild. DLEPSIITTeam@mayo.edu is the DEV operator; use
      your own address while validating.

   3. The step runs under the PowerShellExecution proxy (MFAD\WA03749 on DEV,
      MFAD\WA03747 on PROD) - there is no CmdExec proxy on either instance.
      That account needs:
        * read on msdb here
        * read on EPSI_ODS on the data instance (sys.dm_db_partition_stats)
        * read AND INSERT on EpsiSsisHost.diag
        * read on the file share
        * DatabaseMailUserRole in msdb, once recipients are set

   ---------------------------------------------------------------------------
   TWO THINGS THAT WILL BITE WHOEVER EDITS checker.ps1
   ---------------------------------------------------------------------------
   * An Agent PowerShell step starts in the SQLSERVER:\ provider, where a bare
     UNC path does NOT resolve: Test-Path returns false and reads exactly like a
     permission denial. checker.ps1 uses the FileSystem:: prefix throughout.
   * The subsystem is Windows PowerShell 5.1. No ternaries, no ??, no PS7-only
     syntax.

   WHY A SEPARATE JOB rather than a step appended to the extract:
     * Both extracts end on a quit-reporting-success step, so an appended step
       would never run - the same trap that leaves PROD's Xifin unreachable.
     * Step numbers move. PROD gained one on 2026-10-08, DEV on 2026-10-06.
     * Only a separate job can notice that the extract did not run at all.
   ========================================================================== */

USE msdb;
GO

DECLARE @ProxyName sysname = N'PowerShellExecution';

IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysproxies WHERE name = @ProxyName)
BEGIN
    RAISERROR('Proxy "%s" does not exist on this instance.', 16, 1, @ProxyName);
    SET NOEXEC ON;
END
GO

DECLARE @JobName   sysname = N'DBA - CLARITY Extract Checker';
DECLARE @ProxyName sysname = N'PowerShellExecution';

IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = @JobName)
BEGIN
    EXEC msdb.dbo.sp_delete_job @job_name = @JobName, @delete_unused_schedule = 1;
    PRINT 'Removed the previous version of the job.';
END

EXEC msdb.dbo.sp_add_job
     @job_name    = @JobName,
     @enabled     = 0,                                  -- validate by hand first
     @description = N'Runs the unattended extract checker for $envName. Nothing is installed anywhere - the script is carried in the job step. Looks for what a green job status cannot express: the extract not running, running but moving no data, a recon shortfall, records split across lines, or a collapse in volume. Source of truth is sql\checker.ps1 in the web-app-to-manage-daily-extract project; do not edit this step by hand.',
     @category_name = N'[Uncategorized (Local)]';

/* The script exits 0 when it merely FINDS something - the e-mail is the alert.
   Anything that throws lands on the Agent's own failure path instead, so a red
   job means the CHECKER broke, not that the extract did. Keeping those two
   apart is the whole point. */
DECLARE @Cmd nvarchar(max) = N'
"@

    $footer = @"
';

EXEC msdb.dbo.sp_add_jobstep
     @job_name   = @JobName,
     @step_id    = 1,
     @step_name  = N'Run the extract checker',
     @subsystem  = N'PowerShell',
     @command    = @Cmd,
     @proxy_name = @ProxyName,
     @on_success_action = 1,
     @on_fail_action    = 2,
     @retry_attempts    = 0;

/* Every 30 minutes across the window the extract normally finishes in. Runs have
   taken 9m, 1h48m and 5h49m, so a single fixed time either fires mid-run or
   hours late. The checker reports once per extract run, so repeating is free. */
EXEC msdb.dbo.sp_add_jobschedule
     @job_name = @JobName,
     @name     = N'Every 30 min, 07:00-14:00',
     @enabled  = 1,
     @freq_type = 4,                 -- daily
     @freq_interval = 1,
     @freq_subday_type = 4,          -- minutes
     @freq_subday_interval = 30,
     @active_start_time = 070000,
     @active_end_time   = 140000;

EXEC msdb.dbo.sp_add_jobserver @job_name = @JobName, @server_name = N'(local)';

PRINT '';
PRINT 'Created "' + @JobName + '" for $envName - DISABLED.';
PRINT 'Validate:  EXEC msdb.dbo.sp_start_job @job_name = N''' + @JobName + ''';';
PRINT 'Then read the step output in Job Activity Monitor, and:';
PRINT '  SELECT TOP 10 * FROM EpsiSsisHost.diag.CheckRun ORDER BY CheckRunId DESC;';
PRINT 'Enable when happy:';
PRINT '  EXEC msdb.dbo.sp_update_job @job_name = N''' + @JobName + ''', @enabled = 1;';
GO
"@

    $outFile = Join-Path $OutputDirectory "23_create_checker_job_$envName.sql"
    Set-Content -Path $outFile -Value ($header + $psEscaped + $footer) -Encoding UTF8

    Write-Host ("  {0}" -f $outFile) -ForegroundColor Green
}

Write-Host ("  {0:N0} characters of PowerShell embedded in each" -f $original.Length) -ForegroundColor DarkGray
Write-Host '  Run the one matching the instance. Each creates the job DISABLED.' -ForegroundColor DarkGray
