<#
.SYNOPSIS
    Unattended check on the CLARITY -> STRATAJAZZ daily extract.

.DESCRIPTION
    Looks for what a green job status cannot express. Every serious problem this
    extract had in October 2026 was a SILENT SUCCESS: the job reported OK while
    shipping header-only files on 10/08, while sending Strata 727 junk rows a day,
    and while steps that had been commented out reported success. The job's own
    notification is failure-only, so none of it reached anyone.

    THIS FILE IS THE SOURCE OF TRUTH, BUT IT IS NOT WHAT RUNS.
    Nothing may be stored on the SQL Server hosts, so the script cannot live on
    disk there. Build-CheckerJobScript.ps1 embeds this file verbatim into
    23_create_checker_job_DEV.sql and _PROD.sql, which create an Agent job whose
    PowerShell step carries the whole script inline - the same arrangement extract
    steps 3, 9, 10, 11 and 14 already use. Edit THIS file, then re-run the builder;
    never edit the copy inside the .sql, and never edit the job step in SSMS.

    The checks and their severities deliberately mirror
    src/ExtractConsole/Services/CheckService.cs, which is the same logic in C# for
    the console's own use and for running by hand (ExtractConsole.exe --check).
    Change one, change the other.

.NOTES
    Targets Windows PowerShell 5.1 - that is what the SQL Agent PowerShell
    subsystem provides. No ternaries, no ??, no PS7-only syntax.

    Every filesystem path is reached through the FileSystem:: provider prefix. An
    Agent PowerShell step starts in SQLSERVER:\, where a bare UNC path does not
    resolve: Test-Path returns $false and it reads exactly like a permission
    denial. It is not one.
#>

[CmdletBinding()]
param(
    [ValidateSet('DEV', 'PROD')]
    [string]$EnvironmentName = 'DEV',

    # Report to the console only. Mails nothing, records nothing.
    [switch]$DryRun,

    # Report again on a run that has already been reported.
    [switch]$Force,

    # Overrides the environment's configured recipients. Empty = mail nobody.
    [string]$Recipients,

    [string]$MailProfile
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
#  Configuration
# ---------------------------------------------------------------------------
#  Mirrors the Environments block of src/ExtractConsole/appsettings.json. No
#  step numbers appear anywhere here on purpose: both jobs gained a step in
#  October 2026 and every hard-coded number went stale within three days.

$AllConfig = @{
    DEV = @{
        ControlServer = 'roeeps914q,57015'
        DataServer    = 'ropeps913l01\epsidev'
        DataDatabase  = 'EPSI_ODS'
        DiagDatabase  = 'EpsiSsisHost'
        FileShare     = '\\ropeps913L01\FTP\Strata'
        JobName       = 'Data Extract From CLARITY for STRATAJAZZ'
        Recipients    = ''
    }
    PROD = @{
        ControlServer = 'roeeps014q'
        DataServer    = 'ropeps013L01'
        DataDatabase  = 'EPSI_ODS'
        DiagDatabase  = 'EpsiSsisHost'
        FileShare     = '\\ropeps013L01\FTP\Strata'
        JobName       = 'Data Extract From CLARITY for STRATAJAZZ'
        Recipients    = ''
    }
}

# Table -> file. Key is the logical dataset name used in the extract's own file map.
$ReconTables = @(
    @{ Key = 'HBCharge';           Table = 'dbo.T_HB_CHARGE_RECON';          File = 'MayoClinic_HBCharge_{date}.txt' }
    @{ Key = 'HBCPT';              Table = 'dbo.T_HB_CPT_RECON';             File = 'MayoClinic_HBCPT_{date}.txt' }
    @{ Key = 'HBEncounter';        Table = 'dbo.T_HB_ENCOUNTER_RECON';       File = 'MayoClinic_HBEncounter_{date}.txt' }
    @{ Key = 'HBICD10Dx';          Table = 'dbo.T_HB_ICD10DX_RECON';         File = 'MayoClinic_HBICD10Dx_{date}.txt' }
    @{ Key = 'HBICD10Px';          Table = 'dbo.T_HB_ICD10PX_RECON';         File = 'MayoClinic_HBICD10Px_{date}.txt' }
    @{ Key = 'HBPayments';         Table = 'dbo.T_HB_PAYMENTS_RECON';        File = 'MayoClinic_HBPayments_{date}.txt' }
    @{ Key = 'PBCharge';           Table = 'dbo.T_PB_CHARGE_RECON';          File = 'MayoClinic_PBCharge_{date}.txt' }
    @{ Key = 'PBEncounter';        Table = 'dbo.T_PB_ENCOUNTER_RECON';       File = 'MayoClinic_PBEncounter_{date}.txt' }
    @{ Key = 'PBICD10Dx';          Table = 'dbo.T_PB_ICD10DX_RECON';         File = 'MayoClinic_PBICD10Dx_{date}.txt' }
    @{ Key = 'PBPayments';         Table = 'dbo.T_PB_PAYMENTS_RECON';        File = 'MayoClinic_PBPayments_{date}.txt' }
    @{ Key = 'Surgical_Time_Data'; Table = 'dbo.T_SURGICAL_TIME_DATA_RECON'; File = 'MayoClinic_Surgical_Time_Data_{date}.txt' }
)

# Hours without a run before that is itself the finding. 26 gives a daily 07:00
# job slack for a long run or a late start without crying wolf.
$MaxHoursSinceRun = 26

# Flag a dataset below this fraction of its own last recorded count. 0 disables.
$VolumeDropFactor = 0.5

# Every record starts with a 10-digit EncounterRecordNumber. A line that does not
# match continues the previous record - an embedded CR/LF in a field value.
$RecordStartPattern = '^\d{10}\|'

$Cfg = $AllConfig[$EnvironmentName]
if (-not $Cfg) { throw "Unknown environment '$EnvironmentName'." }
if ($PSBoundParameters.ContainsKey('Recipients')) { $Cfg.Recipients = $Recipients }

# ---------------------------------------------------------------------------
#  Plumbing
# ---------------------------------------------------------------------------

function New-SqlConnection {
    param([string]$Server, [string]$Database)
    $cn = New-Object System.Data.SqlClient.SqlConnection
    $cn.ConnectionString = "Server=$Server;Database=$Database;Integrated Security=True;" +
                           "TrustServerCertificate=True;Connect Timeout=15;Application Name=ExtractChecker"
    $cn.Open()
    return $cn
}

<#
    Runs a query and returns rows. $Parameters is a hashtable; values are passed
    as real SqlParameters, never concatenated - some of what goes through here is
    a failure message straight out of msdb.
#>
function Invoke-SqlQuery {
    param(
        [string]$Server,
        [string]$Database,
        [string]$Query,
        [hashtable]$Parameters = @{},
        [int]$TimeoutSec = 120
    )
    $cn = New-SqlConnection -Server $Server -Database $Database
    try {
        $cmd = $cn.CreateCommand()
        $cmd.CommandText = $Query
        $cmd.CommandTimeout = $TimeoutSec
        foreach ($k in $Parameters.Keys) {
            $v = $Parameters[$k]
            if ($null -eq $v) { $v = [DBNull]::Value }
            [void]$cmd.Parameters.AddWithValue("@$k", $v)
        }
        $dt = New-Object System.Data.DataTable
        $da = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
        [void]$da.Fill($dt)
        return ,$dt
    }
    finally { $cn.Close() }
}

function Invoke-SqlScalar {
    param([string]$Server, [string]$Database, [string]$Query, [hashtable]$Parameters = @{}, [int]$TimeoutSec = 120)
    $dt = Invoke-SqlQuery -Server $Server -Database $Database -Query $Query -Parameters $Parameters -TimeoutSec $TimeoutSec
    if ($dt.Rows.Count -eq 0) { return $null }
    $v = $dt.Rows[0][0]
    if ($v -is [DBNull]) { return $null }
    return $v
}

# Findings are (Severity, Code, Title, Detail). Codes match CheckService.cs.
$script:Findings = @()
function Add-Finding {
    param([ValidateSet('Critical', 'Warning', 'Info')][string]$Severity,
          [string]$Code, [string]$Title, [string]$Detail)
    $script:Findings += [pscustomobject]@{
        Severity = $Severity; Code = $Code; Title = $Title; Detail = $Detail
    }
}

function Get-DbNull { param($Value) if ($Value -is [DBNull]) { return $null } return $Value }

# ---------------------------------------------------------------------------
#  Is it running, did it run, did it work
# ---------------------------------------------------------------------------

function Get-JobStatus {
    $ctl = $Cfg.ControlServer

    # In flight? sysjobactivity carries a row per Agent session; the newest
    # session's row with a start and no stop is a live run.
    $running = Invoke-SqlQuery -Server $ctl -Database 'msdb' -Parameters @{ job = $Cfg.JobName } -Query @'
SELECT TOP (1) a.start_execution_date, a.stop_execution_date
FROM msdb.dbo.sysjobactivity AS a
JOIN msdb.dbo.syssessions   AS s ON s.session_id = a.session_id
JOIN msdb.dbo.sysjobs       AS j ON j.job_id     = a.job_id
WHERE j.name = @job
ORDER BY s.agent_start_date DESC, a.start_execution_date DESC;
'@

    $isRunning = $false
    $runningSince = $null
    if ($running.Rows.Count -gt 0) {
        $start = Get-DbNull $running.Rows[0]['start_execution_date']
        $stop  = Get-DbNull $running.Rows[0]['stop_execution_date']
        if ($null -ne $start -and $null -eq $stop) { $isRunning = $true; $runningSince = $start }
    }

    # step_id = 0 is the job outcome row. 0 failed, 1 succeeded, 3 cancelled.
    $outcome = Invoke-SqlQuery -Server $ctl -Database 'msdb' -Parameters @{ job = $Cfg.JobName } -Query @'
SELECT TOP (1) h.instance_id, h.run_status,
       msdb.dbo.agent_datetime(h.run_date, h.run_time) AS started
FROM msdb.dbo.sysjobhistory AS h
JOIN msdb.dbo.sysjobs       AS j ON j.job_id = h.job_id
WHERE j.name = @job AND h.step_id = 0
ORDER BY h.instance_id DESC;
'@

    $status = [pscustomobject]@{
        IsRunning      = $isRunning
        RunningSince   = $runningSince
        InstanceId     = $null
        Started        = $null
        Outcome        = 'unknown'
        FailedStepId   = $null
        FailedStepName = $null
        FailedMessage  = $null
        HasEnabledSchedule = $false
    }

    if ($outcome.Rows.Count -gt 0) {
        $r = $outcome.Rows[0]
        $status.InstanceId = [int64]$r['instance_id']
        $status.Started    = Get-DbNull $r['started']
        switch ([int]$r['run_status']) {
            0 { $status.Outcome = 'failed' }
            1 { $status.Outcome = 'succeeded' }
            3 { $status.Outcome = 'canceled' }
            default { $status.Outcome = 'unknown' }
        }

        if ($status.Outcome -eq 'failed') {
            # The failing step's own row sits just below the outcome row.
            $step = Invoke-SqlQuery -Server $ctl -Database 'msdb' `
                -Parameters @{ job = $Cfg.JobName; inst = $status.InstanceId } -Query @'
SELECT TOP (1) h.step_id, h.step_name, h.message
FROM msdb.dbo.sysjobhistory AS h
JOIN msdb.dbo.sysjobs       AS j ON j.job_id = h.job_id
WHERE j.name = @job AND h.step_id > 0 AND h.run_status = 0 AND h.instance_id < @inst
ORDER BY h.instance_id DESC;
'@
            if ($step.Rows.Count -gt 0) {
                $status.FailedStepId   = [int]$step.Rows[0]['step_id']
                $status.FailedStepName = [string]$step.Rows[0]['step_name']
                $status.FailedMessage  = [string](Get-DbNull $step.Rows[0]['message'])
            }
        }
    }

    $n = Invoke-SqlScalar -Server $ctl -Database 'msdb' -Parameters @{ job = $Cfg.JobName } -Query @'
SELECT COUNT(*)
FROM msdb.dbo.sysjobschedules AS js
JOIN msdb.dbo.sysschedules    AS s ON s.schedule_id = js.schedule_id
JOIN msdb.dbo.sysjobs         AS j ON j.job_id      = js.job_id
WHERE j.name = @job AND s.enabled = 1;
'@
    $status.HasEnabledSchedule = ([int]$n -gt 0)

    return $status
}

<#
    Matches a failure message against the shared catalogue and returns the
    highest-priority hit with its first suggested fix. The same catalogue the
    console's Known failures tab uses, so adding a rule there improves this mail.
#>
function Get-Diagnosis {
    param([string]$Message)
    if ([string]::IsNullOrWhiteSpace($Message)) { return $null }

    $rules = Invoke-SqlQuery -Server $Cfg.ControlServer -Database $Cfg.DiagDatabase -Query @'
SELECT RuleId, Pattern, MatchType, Title, Cause
FROM diag.FailureRule
WHERE IsEnabled = 1
ORDER BY Priority, RuleId;
'@

    foreach ($row in $rules.Rows) {
        $pattern = [string]$row['Pattern']
        $hit = $false
        if ([string]$row['MatchType'] -eq 'Regex') {
            try { $hit = [regex]::IsMatch($Message, $pattern, 'IgnoreCase') } catch { $hit = $false }
        }
        else {
            $hit = $Message.IndexOf($pattern, [StringComparison]::OrdinalIgnoreCase) -ge 0
        }
        if (-not $hit) { continue }

        $fix = Invoke-SqlScalar -Server $Cfg.ControlServer -Database $Cfg.DiagDatabase `
            -Parameters @{ id = [int]$row['RuleId'] } -Query @'
SELECT TOP (1) Solution FROM diag.FailureSolution WHERE RuleId = @id ORDER BY Ordinal, SolutionId;
'@
        return [pscustomobject]@{
            Title = [string]$row['Title']
            Cause = [string](Get-DbNull $row['Cause'])
            Fix   = $fix
        }
    }
    return $null
}

# ---------------------------------------------------------------------------
#  Did any data actually move
# ---------------------------------------------------------------------------

<#
    Row counts for every recon table in one round trip, from partition metadata.
    COUNT(*) over these tables scans hundreds of millions of rows; the console
    does the same and only takes an exact count when a human asks.
#>
function Get-TableRows {
    $dt = Invoke-SqlQuery -Server $Cfg.DataServer -Database $Cfg.DataDatabase -Query @'
SELECT s.name + '.' + t.name AS TableName, SUM(p.row_count) AS Rows
FROM sys.dm_db_partition_stats AS p
JOIN sys.tables  AS t ON t.object_id = p.object_id
JOIN sys.schemas AS s ON s.schema_id = t.schema_id
WHERE p.index_id IN (0, 1)
GROUP BY s.name, t.name;
'@
    $map = @{}
    foreach ($row in $dt.Rows) { $map[[string]$row['TableName']] = [int64]$row['Rows'] }
    return $map
}

<#
    Per-file line counts the extract itself wrote (step "Create Load Size and
    Count File"), pipe-delimited File|SizeKB|Lines. Reading this is far cheaper
    than re-counting 300 MB files, and the counts already EXCLUDE the header, so
    they compare directly against the table - verified against MayoClinic_HBCPT:
    14,558 physical lines, counts file 14,557, table 14,557.
#>
function Read-CountsFile {
    param([datetime]$ForDate)
    $map = @{}
    $path = "FileSystem::" + (Join-Path $Cfg.FileShare ("MayoClinic_filecounts_{0}.txt" -f $ForDate.ToString('MMddyyyy')))
    if (-not (Test-Path $path)) { return $map }
    try {
        $first = $true
        foreach ($line in [System.IO.File]::ReadLines(($path -replace '^FileSystem::', ''))) {
            if ($first) { $first = $false; continue }
            $parts = $line.Split('|')
            if ($parts.Count -lt 3) { continue }
            $lines = 0
            if ([int64]::TryParse($parts[2], [ref]$lines)) { $map[$parts[0].Trim()] = [int64]$parts[2] }
        }
    }
    catch { }
    return $map
}

<#
    Counts records by streaming the file and counting lines that START one, which
    is the only way to tell a split record from a missing one. On 2026-10-06 the
    DEV PB Encounter file had 397,071 physical lines but exactly 395,733 records,
    matching its table precisely.

    Returns a hashtable with Records and Lines, or $null if the file is not there.
    The header line is skipped, matching the counts file.
#>
function Measure-FileRecords {
    param([string]$FileName)
    $unc = Join-Path $Cfg.FileShare $FileName
    if (-not (Test-Path ("FileSystem::" + $unc))) { return $null }

    $records = 0
    $lines = 0
    $rx = New-Object System.Text.RegularExpressions.Regex $RecordStartPattern
    $reader = $null
    try {
        $reader = New-Object System.IO.StreamReader $unc
        $first = $true
        while ($null -ne ($line = $reader.ReadLine())) {
            if ($first) { $first = $false; continue }   # header
            $lines++
            if ($rx.IsMatch($line)) { $records++ }
        }
    }
    catch { return $null }
    finally { if ($reader) { $reader.Dispose() } }

    return @{ Records = [int64]$records; Lines = [int64]$lines }
}

<#
    The reconciliation verdict. A direct port of ReconService.Verdict - keep them
    identical.

    Deliberately does NOT normalise embedded CR/LF away. Counting records answers
    whether data was lost; a file whose records span several physical lines is
    still malformed for anything reading it a line at a time, so it stays amber
    even when the record count reconciles exactly.
#>
function Get-Verdict {
    param($Rows, $Lines, $Records, $Continuations, $Diff)

    if ($null -eq $Diff) { return 'unknown' }

    # Zero reconciles against zero, which is exactly why it is checked first: an
    # extract that produced nothing otherwise reports a flawless match. That is
    # what happened on PROD 2026-10-08.
    if ($Rows -eq 0) { return 'empty' }

    if ($null -eq $Records) {
        if ($null -ne $Rows -and $Lines -gt $Rows) { return 'artifact' }
        if ($Diff -eq 0) { return 'succeeded' }
        return 'failed'
    }

    if ($Diff -ne 0) { return 'failed' }            # short beats malformed
    if ($Continuations -gt 0) { return 'artifact' }
    return 'succeeded'
}

<#
    A file too big to send is split into numbered parts - 1 GB is the limit, and HB
    Charge hit it on 2026-10-18. "MayoClinic_HBCharge_10192026.txt" becomes
    "..._10192026_f1.txt" to "..._f6.txt", each a complete file with its own header,
    cut at a line boundary. Any dataset can do this, so every one is resolved the same
    way: the parts if there are any (sorted by number), otherwise the single file.
    A direct port of ReconService.ResolveFiles - keep them identical.
#>
function Resolve-DatasetFiles {
    param([string]$Single, [string[]]$Available)

    $ext  = [System.IO.Path]::GetExtension($Single)
    $stem = $Single.Substring(0, $Single.Length - $ext.Length)
    $pattern = '^' + [regex]::Escape($stem) + '_f(\d+)' + [regex]::Escape($ext) + '$'
    $rx = New-Object System.Text.RegularExpressions.Regex -ArgumentList @(
        $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)

    $parts = @()
    foreach ($n in $Available) {
        $m = $rx.Match($n)
        if ($m.Success) { $parts += [pscustomobject]@{ Name = $n; N = [int]$m.Groups[1].Value } }
    }
    if ($parts.Count -gt 0) { return ,@($parts | Sort-Object N | ForEach-Object { $_.Name }) }

    foreach ($n in $Available) {
        if ([string]::Equals($n, $Single, [StringComparison]::OrdinalIgnoreCase)) { return ,@($n) }
    }
    return ,@()
}

function Get-ReconCounts {
    param([datetime]$ForDate)

    $tableRows  = Get-TableRows
    $declared   = Read-CountsFile -ForDate $ForDate
    $stamp      = $ForDate.ToString('MMddyyyy')
    $results    = @()

    # One listing of the share for the date, not a probe per dataset per possible part.
    $onShare = @()
    try {
        $onShare = @(Get-ChildItem -LiteralPath ("FileSystem::" + $Cfg.FileShare) -File -Filter ('*' + $stamp + '*') -ErrorAction Stop |
                     ForEach-Object { $_.Name })
    } catch { }

    foreach ($t in $ReconTables) {
        $single = $t.File.Replace('{date}', $stamp)

        $rows = $null
        if ($tableRows.ContainsKey($t.Table)) { $rows = $tableRows[$t.Table] }

        # The counts file lists each part on its own line, header excluded per part -
        # so the parts' lines add up to the dataset's rows.
        $dataLines = $null
        $counted = Resolve-DatasetFiles -Single $single -Available @($declared.Keys)
        if ($counted.Count -gt 0) {
            $dataLines = [int64]0
            foreach ($n in $counted) { $dataLines += $declared[$n] }
        }

        # Record counts across every part; one unreadable part makes the total unknown,
        # never a short total that would read as missing records.
        $records = $null
        $physical = $null
        $present = Resolve-DatasetFiles -Single $single -Available $onShare
        if ($present.Count -gt 0) {
            $records = [int64]0
            $physical = [int64]0
            foreach ($n in $present) {
                $measured = Measure-FileRecords -FileName $n
                if (-not $measured) { $records = $null; $physical = $null; break }
                $records += $measured.Records
                $physical += $measured.Lines
            }
        }

        # Prefer the declared count for lines - it is what was sent to Strata -
        # and fall back to what we just counted.
        if ($null -eq $dataLines) { $dataLines = $physical }

        $comparable = $records
        if ($null -eq $comparable) { $comparable = $dataLines }

        $diff = $null
        if ($null -ne $rows -and $null -ne $comparable) { $diff = $rows - $comparable }

        $cont = $null
        if ($null -ne $records -and $null -ne $dataLines) { $cont = $dataLines - $records }

        $results += [pscustomobject]@{
            Key         = $t.Key
            TableRows   = $rows
            FileLines   = $dataLines
            FileRecords = $records
            Continuations = $cont
            Difference  = $diff
            Status      = Get-Verdict -Rows $rows -Lines $dataLines -Records $records -Continuations $cont -Diff $diff
        }
    }
    return $results
}

function Add-CountFindings {
    param($Counts)
    if (-not $Counts) { return }

    $withData = @($Counts | Where-Object { $null -ne $_.TableRows })

    # Everything at zero is the 2026-10-08 signature: every step succeeded and
    # nothing moved. One finding rather than eleven.
    if ($withData.Count -gt 0 -and -not ($withData | Where-Object { $_.TableRows -ne 0 })) {
        Add-Finding -Severity Critical -Code 'EmptyExtract' `
            -Title 'The extract produced no data at all' `
            -Detail ("All {0} recon tables hold 0 rows. The job will have reported success. Check staging and the Epic extract child jobs." -f $withData.Count)
        return   # every other count finding would be noise
    }

    foreach ($c in ($Counts | Where-Object { $_.Status -eq 'failed' })) {
        $fileSide = $c.FileRecords
        if ($null -eq $fileSide) { $fileSide = $c.FileLines }
        Add-Finding -Severity Critical -Code 'ReconShort' `
            -Title ("{0}: fewer records than table rows" -f $c.Key) `
            -Detail ("Table {0:N0} vs file {1:N0} (difference {2:N0})." -f $c.TableRows, $fileSide, $c.Difference)
    }

    foreach ($c in ($Counts | Where-Object { $_.Status -eq 'artifact' })) {
        if ($c.Continuations -gt 0) {
            $detail = "{0:N0} continuation line(s). No data lost, but the file is malformed for a line-based reader and the count sent to Strata is overstated." -f $c.Continuations
        }
        else {
            $detail = "File reports {0:N0} lines against {1:N0} rows. An embedded CR/LF in a field value." -f $c.FileLines, $c.TableRows
        }
        Add-Finding -Severity Warning -Code 'SplitRecords' `
            -Title ("{0}: records split across lines" -f $c.Key) -Detail $detail
    }

    if ($VolumeDropFactor -gt 0) { Add-VolumeFindings -Counts $Counts }
}

<#
    Compares each dataset against the last check that recorded a count for it.
    Catches the partial version of the empty-run failure - a collapse that does
    not reach zero and so passes every other check here.
#>
function Add-VolumeFindings {
    param($Counts)

    $previous = @{}
    $dt = Invoke-SqlQuery -Server $Cfg.ControlServer -Database $Cfg.DiagDatabase `
        -Parameters @{ env = $EnvironmentName } -Query @'
SELECT c.DatasetKey, c.TableRows
FROM diag.CheckCount AS c
WHERE c.CheckCountId IN (
        SELECT MAX(c2.CheckCountId)
        FROM diag.CheckCount AS c2
        JOIN diag.CheckRun   AS r2 ON r2.CheckRunId = c2.CheckRunId
        WHERE r2.EnvironmentName = @env AND c2.TableRows > 0
        GROUP BY c2.DatasetKey);
'@
    foreach ($row in $dt.Rows) { $previous[[string]$row['DatasetKey']] = [int64]$row['TableRows'] }

    foreach ($c in $Counts) {
        if ($null -eq $c.TableRows -or $c.TableRows -lt 0) { continue }

        # Only datasets with a known-good previous count. That is what keeps DEV's
        # permanently empty Surgical table - its step is commented out - quiet.
        if (-not $previous.ContainsKey($c.Key)) { continue }
        $before = $previous[$c.Key]
        if ($before -le 0) { continue }

        # Zero is not "down sharply". A dataset that had data and now has none is
        # the empty-run failure happening to one table, and nothing else catches
        # it: EmptyExtract needs ALL of them at zero, and 0 == 0 reconciles
        # perfectly against an empty file.
        if ($c.TableRows -eq 0) {
            Add-Finding -Severity Critical -Code 'DatasetEmpty' `
                -Title ("{0}: no rows at all" -f $c.Key) `
                -Detail ("0 rows, against {0:N0} at the last check. The file sent to Strata will be header-only and will still reconcile." -f $before)
            continue
        }

        $factor = [double]$c.TableRows / [double]$before
        if ($factor -ge $VolumeDropFactor) { continue }

        Add-Finding -Severity Warning -Code 'VolumeDrop' `
            -Title ("{0}: volume down sharply" -f $c.Key) `
            -Detail ("{0:N0} rows against {1:N0} last time - {2:F0}% lower." -f $c.TableRows, $before, ((1 - $factor) * 100))
    }
}

# ---------------------------------------------------------------------------
#  Reporting
# ---------------------------------------------------------------------------

<#
    Has this run already been dealt with? A schedule that fires every thirty
    minutes has to notice the same problem every tick without telling anyone
    twice.

    Keyed on a SUCCESSFUL notification where mail is configured, so a send that
    failed is retried next tick rather than silently dropped. With no recipients
    there is nothing to retry, so any previous row counts - otherwise a
    console-only install writes a row every thirty minutes forever.
#>
function Test-AlreadyHandled {
    param($InstanceId)

    $notifyConfigured = -not [string]::IsNullOrWhiteSpace($Cfg.Recipients)
    $notifiedClause = ''
    if ($notifyConfigured) { $notifiedClause = 'AND Notified = 1' }

    if ($null -eq $InstanceId) {
        $q = "SELECT TOP (1) 1 FROM diag.CheckRun WHERE EnvironmentName = @env AND RunInstanceId IS NULL $notifiedClause AND CheckedUtc > DATEADD(hour, -12, SYSUTCDATETIME());"
        $p = @{ env = $EnvironmentName }
    }
    else {
        $q = "SELECT TOP (1) 1 FROM diag.CheckRun WHERE EnvironmentName = @env AND RunInstanceId = @id $notifiedClause;"
        $p = @{ env = $EnvironmentName; id = $InstanceId }
    }

    $hit = Invoke-SqlScalar -Server $Cfg.ControlServer -Database $Cfg.DiagDatabase -Query $q -Parameters $p
    return ($null -ne $hit)
}

function Build-Message {
    param($Status, $Counts)

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("CLARITY -> STRATAJAZZ extract check  [$EnvironmentName]")
    [void]$sb.AppendLine(('-' * 62))
    # Spell the instance out: the two host names differ by one character
    # (roeeps914q / roeeps014q) and this is read at 07:30 on a phone.
    [void]$sb.AppendLine("Instance    : " + $Cfg.ControlServer)
    if ($Status.Started) {
        [void]$sb.AppendLine(("Run started : {0:yyyy-MM-dd HH:mm}  ({1})" -f $Status.Started, $Status.Outcome))
    }
    [void]$sb.AppendLine(("Checked     : {0:yyyy-MM-dd HH:mm}" -f (Get-Date)))
    [void]$sb.AppendLine()

    foreach ($sev in @('Critical', 'Warning', 'Info')) {
        $group = @($script:Findings | Where-Object { $_.Severity -eq $sev })
        if ($group.Count -eq 0) { continue }
        [void]$sb.AppendLine($sev.ToUpperInvariant())
        foreach ($f in $group) {
            [void]$sb.AppendLine("  * " + $f.Title)
            if ($f.Detail) { [void]$sb.AppendLine("    " + $f.Detail) }
        }
        [void]$sb.AppendLine()
    }

    if ($Counts -and @($Counts).Count -gt 0) {
        [void]$sb.AppendLine('COUNTS')
        [void]$sb.AppendLine(("  {0,-22} {1,12} {2,12}" -f 'dataset', 'table', 'file'))
        foreach ($c in $Counts) {
            $fileSide = $c.FileRecords
            if ($null -eq $fileSide) { $fileSide = $c.FileLines }
            $rowsText = ''
            if ($null -ne $c.TableRows) { $rowsText = '{0:N0}' -f $c.TableRows }
            $fileText = ''
            if ($null -ne $fileSide) { $fileText = '{0:N0}' -f $fileSide }
            [void]$sb.AppendLine(("  {0,-22} {1,12} {2,12}   {3}" -f $c.Key, $rowsText, $fileText, $c.Status))
        }
        [void]$sb.AppendLine()
    }

    if ($script:Findings | Where-Object { $_.Code -eq 'RunFailed' }) {
        [void]$sb.AppendLine('Open the console for the failing step, its diagnosis and what to try.')
    }
    else {
        [void]$sb.AppendLine('Open the console to see this in context.')
    }
    return $sb.ToString()
}

function Save-CheckRun {
    param($Status, $Counts, [int]$Critical, [int]$Warnings, [bool]$Notified, [string]$NotifyError, [string]$Summary)

    $cn = New-SqlConnection -Server $Cfg.ControlServer -Database $Cfg.DiagDatabase
    try {
        $cmd = $cn.CreateCommand()
        $cmd.CommandText = @'
INSERT INTO diag.CheckRun
    (EnvironmentName, RunInstanceId, RunStartedLocal, RunOutcome,
     CriticalCount, WarningCount, Notified, NotifyError, Summary)
OUTPUT INSERTED.CheckRunId
VALUES (@env, @id, @started, @outcome, @crit, @warn, @notified, @err, @summary);
'@
        $vals = @{
            env      = $EnvironmentName
            id       = $Status.InstanceId
            started  = $Status.Started
            outcome  = $Status.Outcome
            crit     = $Critical
            warn     = $Warnings
            notified = $Notified
            err      = $NotifyError
            summary  = $Summary
        }
        foreach ($k in $vals.Keys) {
            $v = $vals[$k]
            if ($null -eq $v -or $v -eq '') { if ($k -eq 'err') { $v = [DBNull]::Value } }
            if ($null -eq $v) { $v = [DBNull]::Value }
            [void]$cmd.Parameters.AddWithValue("@$k", $v)
        }
        $checkRunId = [int]$cmd.ExecuteScalar()

        foreach ($c in $Counts) {
            $cc = $cn.CreateCommand()
            $cc.CommandText = 'INSERT INTO diag.CheckCount (CheckRunId, DatasetKey, TableRows, FileLines, FileRecords) VALUES (@run, @key, @rows, @lines, @records);'
            [void]$cc.Parameters.AddWithValue('@run', $checkRunId)
            [void]$cc.Parameters.AddWithValue('@key', $c.Key)
            $r = $c.TableRows;   if ($null -eq $r) { $r = [DBNull]::Value }
            $l = $c.FileLines;   if ($null -eq $l) { $l = [DBNull]::Value }
            $f = $c.FileRecords; if ($null -eq $f) { $f = [DBNull]::Value }
            [void]$cc.Parameters.AddWithValue('@rows', $r)
            [void]$cc.Parameters.AddWithValue('@lines', $l)
            [void]$cc.Parameters.AddWithValue('@records', $f)
            [void]$cc.ExecuteNonQuery()
        }
    }
    finally { $cn.Close() }
}

function Send-Report {
    param([string]$Subject, [string]$Body)

    $cn = New-SqlConnection -Server $Cfg.ControlServer -Database 'msdb'
    try {
        $cmd = $cn.CreateCommand()
        $cmd.CommandType = [System.Data.CommandType]::StoredProcedure
        $cmd.CommandText = 'msdb.dbo.sp_send_dbmail'
        $cmd.CommandTimeout = 60
        if (-not [string]::IsNullOrWhiteSpace($MailProfile)) {
            [void]$cmd.Parameters.AddWithValue('@profile_name', $MailProfile)
        }
        [void]$cmd.Parameters.AddWithValue('@recipients', $Cfg.Recipients)
        [void]$cmd.Parameters.AddWithValue('@subject', $Subject)
        [void]$cmd.Parameters.AddWithValue('@body', $Body)
        [void]$cmd.ExecuteNonQuery()
    }
    finally { $cn.Close() }
}

# ---------------------------------------------------------------------------
#  Main
# ---------------------------------------------------------------------------
#  Wrapped in a function, and it NEVER calls exit. Calling exit inside a SQL
#  Agent PowerShell step throws the captured output away - the step goes green
#  and the history reads "The step did not generate any output", which is the
#  opposite of what a checker is for. Returning leaves the output intact; a
#  genuine fault still throws, which is what fails the step.

function Invoke-CheckerMain {

    Write-Output "=== $EnvironmentName ==="

    $status = Get-JobStatus

    if ($status.IsRunning) {
        # Mid-flight: the data checks would be measuring a half-loaded system.
        Write-Output ("  The extract is still running (started {0:g}). Nothing checked." -f $status.RunningSince)
        return
    }

    $hoursSince = [double]::MaxValue
    if ($status.Started) { $hoursSince = ((Get-Date) - $status.Started).TotalHours }

    # Only meaningful where the job runs on its own. DEV is started by hand with
    # every schedule disabled, so "has not run for 29 hours" there is a fact about
    # the week, not a fault - and an alert that always fires is an alert nobody reads.
    if ($status.HasEnabledSchedule -and $hoursSince -gt $MaxHoursSinceRun) {
        if ($status.Started) {
            $d = "Last run started {0:g}, {1:F0}h ago (threshold {2}h)." -f $status.Started, $hoursSince, $MaxHoursSinceRun
        }
        else {
            $d = 'No run found in history at all.'
        }
        Add-Finding -Severity Critical -Code 'NoRecentRun' -Title 'The extract has not run recently' -Detail $d
    }

    if ($status.Outcome -eq 'failed') {
        $detail = "Failed at step {0} ({1})." -f $status.FailedStepId, $status.FailedStepName
        $dx = Get-Diagnosis -Message $status.FailedMessage
        if ($dx) {
            $detail += " Known issue: " + $dx.Title + "."
            if ($dx.Fix) { $detail += " First thing to try: " + $dx.Fix }
        }
        Add-Finding -Severity Critical -Code 'RunFailed' -Title 'The last run failed' -Detail $detail
    }

    # A run someone stopped is not a run that succeeded, and nothing else reports it:
    # the job's own notification is failure-only, and a cancel is not a failure.
    if ($status.Outcome -eq 'canceled') {
        Add-Finding -Severity Warning -Code 'RunCanceled' -Title 'The last run was stopped before it finished' `
            -Detail ("Started {0:g} and was cancelled. Nothing restarted it." -f $status.Started)
    }

    $forDate = (Get-Date).Date
    if ($status.Started) { $forDate = ([datetime]$status.Started).Date }

    $counts = @()
    try {
        $counts = Get-ReconCounts -ForDate $forDate
        Add-CountFindings -Counts $counts
    }
    catch {
        Add-Finding -Severity Warning -Code 'ReconUnavailable' -Title 'Could not reconcile counts' -Detail $_.Exception.Message
    }

    $critical = @($script:Findings | Where-Object { $_.Severity -eq 'Critical' }).Count
    $warnings = @($script:Findings | Where-Object { $_.Severity -eq 'Warning' }).Count

    if ($critical + $warnings -eq 0) {
        Write-Output '  Nothing to report.'
        return
    }

    $body = Build-Message -Status $status -Counts $counts
    Write-Output $body

    if (-not $Force -and (Test-AlreadyHandled -InstanceId $status.InstanceId)) {
        Write-Output '  (already reported for this run - not mailed again)'
        return
    }

    if ($DryRun) {
        Write-Output '  (dry run - nothing mailed, nothing recorded)'
        return
    }

    $notified = $false
    $notifyError = $null

    if (-not [string]::IsNullOrWhiteSpace($Cfg.Recipients)) {
        try {
            $subject = "[$EnvironmentName] CLARITY extract check: "
            if ($critical -gt 0) { $subject += "$critical problem(s)" } else { $subject += 'warnings' }
            $subject += " - " + $script:Findings[0].Title
            Send-Report -Subject $subject -Body $body
            $notified = $true
            Write-Output ("  Mailed to " + $Cfg.Recipients)
        }
        catch {
            # Record the failure rather than let it look like nobody needed telling.
            $notifyError = $_.Exception.Message
            if ($notifyError.Length -gt 1000) { $notifyError = $notifyError.Substring(0, 1000) }
            Write-Output "  MAIL FAILED: $notifyError"
        }
    }
    else {
        Write-Output '  (no recipients configured - job history only)'
    }

    Save-CheckRun -Status $status -Counts $counts -Critical $critical -Warnings $warnings `
                  -Notified $notified -NotifyError $notifyError -Summary $body

}

# Finding something is NOT an error: the mail is the alert, and failing the step
# would make this job red for a DATA problem rather than a CHECKER problem.
# Anything that throws inside lands on the Agent's own failure path instead,
# which is exactly the distinction worth keeping.
Invoke-CheckerMain
