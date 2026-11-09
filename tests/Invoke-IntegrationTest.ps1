<#
.SYNOPSIS
    End-to-end test of the ExtractConsole API against a disposable SQL Agent job.

.DESCRIPTION
    Creates ZZ_ExtractConsole_SelfTest on the control instance, drives the console's
    real HTTP endpoints against it, and removes the fixture afterwards. The production
    extract is never started, stopped or read for actions - only the fixture is.

.EXAMPLE
    pwsh -File tests\Invoke-IntegrationTest.ps1
#>

[CmdletBinding()]
param(
    [string]$ControlServer = 'roeeps914q,57015',
    [string]$ConfigPath    = "$PSScriptRoot\selftest.config.json",
    [int]$Port             = 5399,
    [switch]$KeepFixture
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

$script:Pass = 0
$script:Fail = 0

function Test-That {
    param([string]$Id, [string]$Name, [scriptblock]$Body)
    try {
        $result = & $Body
        if ($result -eq $true) {
            Write-Host ("  {0}  PASS  {1}" -f $Id, $Name) -ForegroundColor Green
            $script:Pass++
        }
        else {
            Write-Host ("  {0}  FAIL  {1}" -f $Id, $Name) -ForegroundColor Red
            if ($result) { Write-Host ("        -> {0}" -f $result) -ForegroundColor DarkYellow }
            $script:Fail++
        }
    }
    catch {
        Write-Host ("  {0}  FAIL  {1}" -f $Id, $Name) -ForegroundColor Red
        Write-Host ("        -> {0}" -f $_.Exception.Message) -ForegroundColor DarkYellow
        $script:Fail++
    }
}

# -i rather than -Q: a multi-batch script passed as a -Q argument does not run its
# GO-separated batches reliably, and the failure is silent.
function Invoke-SqlFile {
    param([string]$Path)
    $out = sqlcmd -S $ControlServer -E -C -b -i $Path 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed on $(Split-Path $Path -Leaf): $($out -join ' ')" }
    return $out
}

function Get-Status {
    $r = Invoke-RestMethod "$BaseUrl/api/status" -Headers $Headers -TimeoutSec 60
    if ($r.error) { throw $r.error }
    return $r.data
}

# The checker is a separate process, not an HTTP endpoint - run it the way the Agent
# job does. $ProjDir is set once the build has happened.
function Invoke-Checker {
    param([string[]]$Flags = @(), [string]$Env = 'SELFTEST')
    Push-Location $script:ProjDir
    try {
        $text = & dotnet run --no-build -- --check --env $Env --config $ConfigPath @Flags 2>&1 |
                Out-String
        return [pscustomobject]@{ Code = $LASTEXITCODE; Text = $text }
    }
    finally { Pop-Location }
}

function Invoke-Adhoc {
    param([string]$Key, [string]$Mode, [hashtable]$Body)
    Invoke-RestMethod "$BaseUrl/api/adhoc/$Key/$Mode" -Method Post -Headers $Headers `
        -ContentType 'application/json' -Body ($Body | ConvertTo-Json) -TimeoutSec 60
}

# The file exactly as bytes, with CR and LF made visible - layout is part of the contract.
function Get-AdhocBytes {
    param([string]$Name)
    ([IO.File]::ReadAllText((Join-Path $AdhocDir $Name))) -replace "`r", '\r' -replace "`n", '\n'
}

<#
    A minimal .xlsx built by hand, so the suite needs no Excel. Row 1 is a header; the
    TDL_ID cells are NUMBERS written the way Excel stores a 15-digit value, in E notation,
    which is the case worth proving.
#>
function New-TestXlsx {
    param([string]$Path, [string[][]]$Rows)
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    if (Test-Path $Path) { Remove-Item $Path -Force }

    $sheetRows = for ($r = 0; $r -lt $Rows.Count; $r++) {
        $cells = for ($c = 0; $c -lt $Rows[$r].Count; $c++) {
            $ref = [char](65 + $c) + ($r + 1)
            $v = $Rows[$r][$c]
            if ($v -match '^[0-9.E+]+$') { "<c r=""$ref""><v>$v</v></c>" }
            else { "<c r=""$ref"" t=""inlineStr""><is><t>$v</t></is></c>" }
        }
        "<row r=""$($r + 1)"">$($cells -join '')</row>"
    }

    $parts = @{
        '[Content_Types].xml' = '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>'
        '_rels/.rels' = '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>'
        'xl/workbook.xml' = '<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Finance" sheetId="1" r:id="rId1"/></sheets></workbook>'
        'xl/_rels/workbook.xml.rels' = '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>'
        'xl/worksheets/sheet1.xml' = '<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>' + ($sheetRows -join '') + '</sheetData></worksheet>'
    }

    $zip = [System.IO.Compression.ZipFile]::Open($Path, 'Create')
    try {
        foreach ($name in $parts.Keys) {
            $entry = $zip.CreateEntry($name)
            $w = New-Object System.IO.StreamWriter($entry.Open())
            $w.Write($parts[$name]); $w.Dispose()
        }
    }
    finally { $zip.Dispose() }
}

<#
    Starts an extra console whose config carries the given Access section, so a role can
    be tested as the real Windows account running the suite. Nothing is faked: the role
    comes from the same group resolution the shipped console uses.
#>
function Start-RoleConsole {
    param([hashtable]$Access, [int]$Port, [scriptblock]$Mutate)
    $cfg = Get-Content $ConfigPath -Raw | ConvertFrom-Json
    if ($Access) { $cfg | Add-Member -NotePropertyName Access -NotePropertyValue ([pscustomobject]$Access) -Force }
    if ($Mutate) { & $Mutate $cfg }
    $path = Join-Path ([IO.Path]::GetTempPath()) "ec-selftest-role-$Port.json"
    $cfg | ConvertTo-Json -Depth 10 | Set-Content $path -Encoding UTF8
    $out = Join-Path ([IO.Path]::GetTempPath()) "ec-selftest-role-$Port.txt"
    Remove-Item $out, "$out.err" -ErrorAction SilentlyContinue

    Push-Location $script:ProjDir
    $proc = Start-Process -FilePath 'dotnet' -PassThru -WindowStyle Hidden `
        -ArgumentList @('run', '--no-build', '--', '--port', $Port, '--no-browser', '--config', "`"$path`"") `
        -RedirectStandardOutput $out -RedirectStandardError "$out.err"
    Pop-Location

    $tok = $null
    $deadline = (Get-Date).AddSeconds(60)
    while (-not $tok -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        if (Test-Path $out) {
            $m = Select-String -Path $out -Pattern 't=([A-F0-9]+)' -ErrorAction SilentlyContinue
            if ($m) { $tok = $m.Matches.Groups[1].Value }
        }
    }
    if (-not $tok) { throw "Role console on $Port did not start: $(Get-Content "$out.err" -Raw -ErrorAction SilentlyContinue)" }
    [pscustomobject]@{ Proc = $proc; Url = "http://127.0.0.1:$Port"; Headers = @{ 'X-Console-Token' = $tok }; Config = $path }
}

function Stop-RoleConsole {
    param($Console)
    if ($Console.Proc -and -not $Console.Proc.HasExited) { Stop-Process -Id $Console.Proc.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item $Console.Config -ErrorAction SilentlyContinue
}

# Status code and parsed body, without throwing on 4xx - the refusals are the point.
function Invoke-As {
    param($Console, [string]$Path, [string]$Method = 'GET', [hashtable]$Body)
    $p = @{ Uri = "$($Console.Url)$Path"; Method = $Method; Headers = $Console.Headers; TimeoutSec = 120; SkipHttpErrorCheck = $true }
    if ($Body) { $p.ContentType = 'application/json'; $p.Body = ($Body | ConvertTo-Json) }
    $r = Invoke-WebRequest @p
    $json = $null
    try { $json = $r.Content | ConvertFrom-Json } catch { }
    [pscustomobject]@{ Status = [int]$r.StatusCode; Json = $json }
}

function Get-CheckRunCount {
    $n = sqlcmd -S $ControlServer -E -C -h -1 -W -Q `
        "SET NOCOUNT ON; SELECT COUNT(*) FROM EpsiSsisHost.diag.CheckRun WHERE EnvironmentName='SELFTEST';"
    return [int]($n | Select-Object -Last 1).Trim()
}

function Wait-For {
    [CmdletBinding()]
    param([Parameter(Mandatory)][scriptblock]$Condition, [int]$TimeoutSeconds = 70, [string]$Describe = 'condition')
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Seconds 2
    }
    throw "Timed out after ${TimeoutSeconds}s waiting for $Describe."
}

# ---------------------------------------------------------------------------

Write-Host ''
Write-Host 'ExtractConsole integration test' -ForegroundColor Cyan
Write-Host ("  control instance : {0}" -f $ControlServer)
Write-Host ("  config           : {0}" -f $ConfigPath)
Write-Host ''

$proc = $null
try {
    Write-Host 'Creating the fixture job...' -ForegroundColor DarkGray
    Invoke-SqlFile "$PSScriptRoot\10_create_selftest_job.sql" | Out-Null

    $exists = sqlcmd -S $ControlServer -E -C -h -1 -W -Q `
        "SET NOCOUNT ON; SELECT COUNT(*) FROM msdb.dbo.sysjobs WHERE name=N'ZZ_ExtractConsole_SelfTest';"
    if (($exists | Select-Object -Last 1).Trim() -ne '1') { throw 'The fixture job was not created.' }

    # ---- AdHoc list fixtures. The real lists live on the FTP share and are read by
    # the next extract, so the suite must never write to them. Copies of their real
    # quirks go in a temp folder, and the config is rewritten to point there.
    $script:AdhocDir = Join-Path ([IO.Path]::GetTempPath()) 'ec-selftest-adhoc'
    Remove-Item $AdhocDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory $AdhocDir | Out-Null
    $ascii = [System.Text.Encoding]::ASCII
    # No line ending after the header - exactly as DEV's HB Charge is today.
    [IO.File]::WriteAllText((Join-Path $AdhocDir 'AdhocHARList - HB Charge.csv'), 'HSP_ACCOUNT_ID|TX_ID', $ascii)
    # Written the way step 14 writes: CRLF between lines, none after the last.
    [IO.File]::WriteAllText((Join-Path $AdhocDir 'AdhocHARList - PB Encounter.csv'), "HSP_ACCOUNT_ID`r`n1000000001`r`n1000000002", $ascii)
    # Lower-case header with a trailing CRLF, as several real files have.
    [IO.File]::WriteAllText((Join-Path $AdhocDir 'AdhocHARList - PB ICD10Dx.csv'), "hsp_account_id`r`n", $ascii)
    [IO.File]::WriteAllText((Join-Path $AdhocDir 'AdhocHARList - PB Charge.csv'), "HSP_ACCOUNT_ID|TDL_ID`r`n", $ascii)
    # The other six are deliberately absent, to prove a missing file is reported and never created.

    # ---- Split-file fixtures. A file over 1 GB is split into _f1, _f2... parts, each a
    # complete file with its own header (HB Charge, 2026-10-18 onward). No real split
    # file survives on a share for long - CLEO collects them - so these stand in:
    #   HBCPT      clean split f1+f2, one embedded line break in f2
    #   HBICD10Px  split with a MISSING part: f1 and f3, no f2
    #   HBPayments not split at all - the old shape must still work
    $script:ReconDir = Join-Path ([IO.Path]::GetTempPath()) 'ec-selftest-recon'
    Remove-Item $ReconDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory $ReconDir | Out-Null
    $hdr = "EncounterRecordNumber|Value"
    $w = { param($n, $lines) [IO.File]::WriteAllText((Join-Path $ReconDir $n), (($hdr) + "`r`n" + ($lines -join "`r`n") + "`r`n"), $ascii) }
    & $w 'MayoClinic_HBCPT_10192026_f1.txt'      @('1000000001|a', '1000000002|b', '1000000003|c')
    & $w 'MayoClinic_HBCPT_10192026_f2.txt'      @('1000000004|d', '1000000005|first half', 'second half of the same record')
    & $w 'MayoClinic_HBICD10Px_10192026_f1.txt'  @('2000000001|a')
    & $w 'MayoClinic_HBICD10Px_10192026_f3.txt'  @('2000000003|c')
    & $w 'MayoClinic_HBPayments_10192026.txt'    @('3000000001|a', '3000000002|b')
    # The counts file lists each part on its own line, header excluded - as the real one does.
    [IO.File]::WriteAllText((Join-Path $ReconDir 'MayoClinic_filecounts_10192026.txt'), (@(
        'File|SizeKB|Lines',
        'MayoClinic_HBCPT_10192026_f1.txt|1|3',
        'MayoClinic_HBCPT_10192026_f2.txt|1|3',
        'MayoClinic_HBICD10Px_10192026_f1.txt|1|1',
        'MayoClinic_HBICD10Px_10192026_f3.txt|1|1',
        'MayoClinic_HBPayments_10192026.txt|1|2') -join "`r`n"), $ascii)

    $cfg = Get-Content $ConfigPath -Raw | ConvertFrom-Json
    foreach ($e in $cfg.Environments) { $e | Add-Member -NotePropertyName FileShare -NotePropertyValue $AdhocDir -Force }

    # An environment over the split fixtures. Table rows come from the real DEV recon
    # tables (read-only); the assertions are about the FILE side.
    $recon = ($cfg.Environments | Where-Object Name -eq 'SELFTEST') | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $recon.Name = 'SELFTEST_RECON'
    $recon.IsDefault = $false
    $recon.FileShare = $ReconDir
    $recon.ReconTables = @(
        [pscustomobject]@{ Key = 'HBCPT';      Schema = 'dbo'; Table = 'T_HB_CPT_RECON';      FilePattern = 'MayoClinic_HBCPT_{date}.txt' },
        [pscustomobject]@{ Key = 'HBICD10Px';  Schema = 'dbo'; Table = 'T_HB_ICD10PX_RECON';  FilePattern = 'MayoClinic_HBICD10Px_{date}.txt' },
        [pscustomobject]@{ Key = 'HBPayments'; Schema = 'dbo'; Table = 'T_HB_PAYMENTS_RECON'; FilePattern = 'MayoClinic_HBPayments_{date}.txt' })
    $cfg.Environments += $recon
    $script:ConfigPath = Join-Path ([IO.Path]::GetTempPath()) 'ec-selftest.config.json'
    $cfg | ConvertTo-Json -Depth 10 | Set-Content $ConfigPath -Encoding UTF8

    # ---- start the console against the fixture config
    $projDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\ExtractConsole'
    $script:ProjDir = $projDir
    $outFile = Join-Path ([IO.Path]::GetTempPath()) 'ec-selftest-out.txt'
    $errFile = Join-Path ([IO.Path]::GetTempPath()) 'ec-selftest-err.txt'
    Remove-Item $outFile, $errFile -ErrorAction SilentlyContinue

    Push-Location $projDir
    dotnet build -v q --nologo | Out-Null
    $proc = Start-Process -FilePath 'dotnet' -PassThru -WindowStyle Hidden `
        -ArgumentList @('run', '--no-build', '--',
                        '--port', $Port, '--no-browser', '--config', "`"$ConfigPath`"") `
        -RedirectStandardOutput $outFile -RedirectStandardError $errFile
    Pop-Location

    $token = $null
    $deadline = (Get-Date).AddSeconds(60)
    while (-not $token -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        if (Test-Path $outFile) {
            $m = Select-String -Path $outFile -Pattern 't=([A-F0-9]+)' -ErrorAction SilentlyContinue
            if ($m) { $token = $m.Matches.Groups[1].Value }
        }
    }
    if (-not $token) { throw "Console did not start. stderr: $(Get-Content $errFile -Raw -ErrorAction SilentlyContinue)" }

    $script:BaseUrl = "http://127.0.0.1:$Port"
    $script:Headers = @{ 'X-Console-Token' = $token }

    Write-Host "Console up on $BaseUrl" -ForegroundColor DarkGray
    Write-Host ''

    # =======================================================================

    Test-That 'T01' 'an API call without the token is rejected' {
        try {
            Invoke-RestMethod "$BaseUrl/api/status" -TimeoutSec 20 | Out-Null
            'request succeeded without a token'
        }
        catch {
            if ($_.Exception.Response.StatusCode.value__ -eq 401) { $true }
            else { "expected 401, got $($_.Exception.Response.StatusCode.value__)" }
        }
    }

    Test-That 'T02' 'status reports the fixture and its seven steps' {
        $s = Get-Status
        if ($s.jobName -ne 'ZZ_ExtractConsole_SelfTest') { return "job name was $($s.jobName)" }
        if ($s.steps.Count -ne 7) { return "expected 7 steps, got $($s.steps.Count)" }
        $true
    }

    Test-That 'T03' 'the allowlist holds exactly the configured jobs and nothing else' {
        # The parent extract must be refused...
        $body = @{ environment = 'SELFTEST'; jobName = 'Data Extract From CLARITY for STRATAJAZZ' } | ConvertTo-Json
        $r = Invoke-RestMethod "$BaseUrl/api/actions/start" -Method Post -Headers $Headers -ContentType 'application/json' -Body $body -TimeoutSec 30
        if ($r.success) { return 'the real extract was ACCEPTED - the allowlist is broken' }
        if ($r.message -notmatch 'not a job this console manages') { return "unexpected message: $($r.message)" }

        # ...and so must one of its child jobs. Regression: --config used to LAYER on top
        # of appsettings.json instead of replacing it, and because .NET merges config
        # arrays by index, this environment silently inherited the default file's
        # ChildJobs - widening the allowlist to jobs nobody configured here.
        $body2 = @{ environment = 'SELFTEST'; jobName = 'Surgical File Extract' } | ConvertTo-Json
        $r2 = Invoke-RestMethod "$BaseUrl/api/actions/start" -Method Post -Headers $Headers -ContentType 'application/json' -Body $body2 -TimeoutSec 30
        if ($r2.success) { return 'a child job from the DEFAULT settings file was accepted' }
        if ($r2.message -notmatch 'Allowed: ZZ_ExtractConsole_SelfTest\.?\s*$') {
            return "allowlist leaked extra jobs: $($r2.message)"
        }
        $true
    }

    Test-That 'T04' 'start runs the job and status turns to running' {
        $body = @{ environment = 'SELFTEST' } | ConvertTo-Json
        $r = Invoke-RestMethod "$BaseUrl/api/actions/start" -Method Post -Headers $Headers -ContentType 'application/json' -Body $body -TimeoutSec 30
        if (-not $r.success) { return $r.message }
        Wait-For -Describe 'the job to report running' -Condition { (Get-Status).isRunning }
        $true
    }

    Test-That 'T05' 'starting an already-running job is refused' {
        $body = @{ environment = 'SELFTEST' } | ConvertTo-Json
        $r = Invoke-RestMethod "$BaseUrl/api/actions/start" -Method Post -Headers $Headers -ContentType 'application/json' -Body $body -TimeoutSec 30
        if ($r.success) { return 'a second concurrent start was accepted' }
        if ($r.message -notmatch 'already running') { return "unexpected message: $($r.message)" }
        $true
    }

    Test-That 'T06' 'stop without the typed confirmation is refused' {
        $body = @{ environment = 'SELFTEST'; jobName = 'ZZ_ExtractConsole_SelfTest'; confirm = 'nope' } | ConvertTo-Json
        $r = Invoke-RestMethod "$BaseUrl/api/actions/stop" -Method Post -Headers $Headers -ContentType 'application/json' -Body $body -TimeoutSec 30
        if ($r.success) { return 'stop proceeded without a matching confirmation' }
        $true
    }

    Test-That 'T07' 'stop with the typed confirmation stops the job' {
        $body = @{ environment = 'SELFTEST'; jobName = 'ZZ_ExtractConsole_SelfTest'; confirm = 'ZZ_ExtractConsole_SelfTest' } | ConvertTo-Json
        $r = Invoke-RestMethod "$BaseUrl/api/actions/stop" -Method Post -Headers $Headers -ContentType 'application/json' -Body $body -TimeoutSec 30
        if (-not $r.success) { return $r.message }
        Wait-For -Describe 'the job to stop' -Condition { -not (Get-Status).isRunning }
        $true
    }

    Test-That 'T08' 'a run that fails is attributed to the step that broke' {
        # Let it run all the way to the deliberate failure in step 2.
        $body = @{ environment = 'SELFTEST' } | ConvertTo-Json
        Invoke-RestMethod "$BaseUrl/api/actions/start" -Method Post -Headers $Headers -ContentType 'application/json' -Body $body -TimeoutSec 30 | Out-Null
        Wait-For -Describe 'the job to start' -Condition { (Get-Status).isRunning }
        Wait-For -TimeoutSeconds 180 -Describe 'the job to fail' -Condition {
            $s = Get-Status
            (-not $s.isRunning) -and $s.outcome -eq 'failed'
        }
        $s = Get-Status
        if ($s.failedStepId -ne 2) { return "expected step 2, got $($s.failedStepId)" }
        $true
    }

    Test-That 'T09' 'steps after the failure are reported as not reached, not succeeded' {
        $s = Get-Status
        $step3 = $s.steps | Where-Object stepId -eq 3
        if ($step3.outcome -ne 'skipped') { return "step 3 reported '$($step3.outcome)'" }
        $true
    }

    Test-That 'T10' 'restart from a step starts there and reports that step immediately' {
        $body = @{ environment = 'SELFTEST'; fromStepId = 3 } | ConvertTo-Json
        $r = Invoke-RestMethod "$BaseUrl/api/actions/start" -Method Post -Headers $Headers -ContentType 'application/json' -Body $body -TimeoutSec 30
        if (-not $r.success) { return $r.message }
        if ($r.detail -ne 'Selftest step three') { return "started at '$($r.detail)'" }

        # The restarted step must be reported straight away. Neither history nor
        # last_executed_step_id knows anything this early, so inferring would name
        # step 1 - and on the real extract a restart at a multi-hour step would show
        # that wrong number for hours.
        Wait-For -Describe 'the restarted run to report running' -Condition { (Get-Status).isRunning }
        $early = Get-Status
        if ($early.currentStepId -ne 3) {
            return "immediately after restarting at step 3 the current step was reported as $($early.currentStepId)"
        }

        Wait-For -TimeoutSeconds 120 -Describe 'the restarted run to finish' -Condition {
            $s = Get-Status
            (-not $s.isRunning) -and $s.outcome -eq 'succeeded'
        }
        # Steps 1 and 2 must NOT have run in this slice.
        $s = Get-Status
        $step1 = $s.steps | Where-Object stepId -eq 1
        if ($step1.outcome -eq 'succeeded') { return 'step 1 ran even though the restart began at step 3' }
        $true
    }

    Test-That 'T11' 'history lists the runs with their durations' {
        $r = Invoke-RestMethod "$BaseUrl/api/history?top=10" -Headers $Headers -TimeoutSec 60
        if ($r.error) { return $r.error }
        if ($r.data.Count -lt 3) { return "expected at least 3 runs, got $($r.data.Count)" }
        if (($r.data | Where-Object outcome -eq 'failed').Count -lt 1) { return 'no failed run recorded' }
        $true
    }

    Test-That 'T12' 'every action was written to the audit log' {
        $r = Invoke-RestMethod "$BaseUrl/api/audit" -Headers $Headers -TimeoutSec 30
        $lines = $r.data
        if (-not $lines) { return 'audit log is empty' }
        if (($lines | Where-Object { $_ -match 'START' }).Count -lt 1) { return 'no START entry' }
        if (($lines | Where-Object { $_ -match 'STOP' }).Count -lt 1) { return 'no STOP entry' }
        if (($lines | Where-Object { $_ -match [Regex]::Escape($env:USERNAME) }).Count -lt 1) { return 'entries do not name the user' }
        $true
    }

    Test-That 'T13' 'a step the job flow never reaches is flagged, not reported as skipped' {
        $s = Get-Status
        $step3 = $s.steps | Where-Object stepId -eq 3
        $step4 = $s.steps | Where-Object stepId -eq 4
        if (-not $step3.reachableFromStart) { return 'step 3 was wrongly flagged unreachable' }
        if ($step4.reachableFromStart) { return 'step 4 is after a quit-on-success step but was reported reachable' }
        $true
    }

    Test-That 'T14' 'on a production environment, start without the environment name is refused' {
        $body = @{ environment = 'SELFTEST_PROD' } | ConvertTo-Json
        $r = Invoke-RestMethod "$BaseUrl/api/actions/start" -Method Post -Headers $Headers -ContentType 'application/json' -Body $body -TimeoutSec 30
        if ($r.success) { return 'a production start proceeded with no confirmation' }
        if ($r.message -notmatch 'requires echoing the environment name') { return "unexpected message: $($r.message)" }

        # ...and a wrong one is refused too.
        $body = @{ environment = 'SELFTEST_PROD'; confirm = 'SELFTEST' } | ConvertTo-Json
        $r2 = Invoke-RestMethod "$BaseUrl/api/actions/start" -Method Post -Headers $Headers -ContentType 'application/json' -Body $body -TimeoutSec 30
        if ($r2.success) { return 'a production start proceeded with the wrong confirmation' }
        $true
    }

    Test-That 'T15' 'on a production environment, start with the environment name is accepted' {
        $body = @{ environment = 'SELFTEST_PROD'; confirm = 'SELFTEST_PROD' } | ConvertTo-Json
        $r = Invoke-RestMethod "$BaseUrl/api/actions/start" -Method Post -Headers $Headers -ContentType 'application/json' -Body $body -TimeoutSec 30
        if (-not $r.success) { return $r.message }
        Wait-For -Describe 'the confirmed production start to run' -Condition { (Get-Status).isRunning }

        # Leave the fixture idle for the teardown.
        $stop = @{ environment = 'SELFTEST'; jobName = 'ZZ_ExtractConsole_SelfTest'; confirm = 'ZZ_ExtractConsole_SelfTest' } | ConvertTo-Json
        Invoke-RestMethod "$BaseUrl/api/actions/stop" -Method Post -Headers $Headers -ContentType 'application/json' -Body $stop -TimeoutSec 30 | Out-Null
        Wait-For -Describe 'the fixture to stop' -Condition { -not (Get-Status).isRunning }
        $true
    }

    Test-That 'T16' 'a step that has finished is never reported as the running step' {
        # Regression: sysjobactivity.last_executed_step_id names the last step Agent
        # EXECUTED, so once that step finishes it points at a completed step. The console
        # used to show "Running - step 2" while step 2's own row said Succeeded.
        $body = @{ environment = 'SELFTEST' } | ConvertTo-Json
        Invoke-RestMethod "$BaseUrl/api/actions/start" -Method Post -Headers $Headers -ContentType 'application/json' -Body $body -TimeoutSec 30 | Out-Null

        # Each fixture step waits 20s, so there is a wide window in which step 1 has
        # finished and step 2 is still going.
        Wait-For -TimeoutSeconds 90 -Describe 'step 1 to finish while the job is still running' -Condition {
            $s = Get-Status
            $one = $s.steps | Where-Object stepId -eq 1
            $s.isRunning -and $one.outcome -eq 'succeeded'
        }

        $s = Get-Status
        $finished = @('succeeded', 'failed', 'canceled', 'noop')
        $current = $s.steps | Where-Object stepId -eq $s.currentStepId

        $result = $true
        if ($s.currentStepId -eq 1) { $result = 'step 1 had finished but was still reported as the current step' }
        elseif ($current -and $current.outcome -in $finished) {
            $result = "current step $($s.currentStepId) reports a finished outcome '$($current.outcome)'"
        }
        elseif (($s.steps | Where-Object { $_.outcome -eq 'running' }).Count -gt 1) {
            $result = 'more than one step reported running at once'
        }

        # Leave the fixture idle regardless of the assertion outcome.
        $stop = @{ environment = 'SELFTEST'; jobName = 'ZZ_ExtractConsole_SelfTest'; confirm = 'ZZ_ExtractConsole_SelfTest' } | ConvertTo-Json
        Invoke-RestMethod "$BaseUrl/api/actions/stop" -Method Post -Headers $Headers -ContentType 'application/json' -Body $stop -TimeoutSec 30 | Out-Null
        Wait-For -Describe 'the fixture to stop' -Condition { -not (Get-Status).isRunning }

        $result
    }

    Test-That 'T17' 'the next scheduled run is in the future, not the run that already happened' {
        # Regression: sysjobschedules.next_run_date is not refreshed the moment a run
        # finishes, so it keeps pointing at the run that just went - which displayed a
        # "Next scheduled" identical to "Started".
        $s = Get-Status

        if (-not $s.nextScheduledLocal) { return 'no next scheduled run was reported for a job that has an enabled schedule' }

        $next = [datetime]$s.nextScheduledLocal
        if ($next -le (Get-Date)) { return "next scheduled run $next is not in the future" }

        if ($s.startedLocal -and ([datetime]$s.startedLocal) -eq $next) {
            return 'next scheduled run is identical to the start of the last run'
        }

        # Every per-schedule value must obey the same rule.
        foreach ($sch in $s.schedules) {
            if ($sch.nextRunLocal -and ([datetime]$sch.nextRunLocal) -le (Get-Date)) {
                return "schedule '$($sch.name)' reports a next run in the past: $($sch.nextRunLocal)"
            }
        }
        $true
    }

    Test-That 'T18' 'a commented-out step is detected, and a re-enabled one is not' {
        # Regression: PROD step 9 had its disable block wrapped in /* */, making the step
        # active, but the console called it disabled because that came from static config.
        $s = Get-Status
        $five = $s.steps | Where-Object stepId -eq 5   # body in /* */, live tautology guard
        $six  = $s.steps | Where-Object stepId -eq 6   # disable block itself commented out

        if (-not $five.isNoOp) { return 'step 5 is commented out but was not detected as a no-op' }
        if ($six.isNoOp) { return 'step 6 is active (its disable block is commented out) but was reported as a no-op' }

        # The steps that do real work must not be swept up - step 2's body contains a
        # RAISERROR, and the real job's orchestrators contain `WHILE 1 = 1` loops.
        foreach ($id in 1, 2, 3, 4) {
            $st = $s.steps | Where-Object stepId -eq $id
            if ($st.isNoOp) { return "step $id does real work but was detected as a no-op" }
        }
        $true
    }

    Test-That 'T19' 'SSIS packages are discovered from the job step definitions' {
        # The tab used to show one hard-coded package while the real extract runs ~28
        # across its child jobs. The list is now read from the SSIS steps themselves.
        $r = Invoke-RestMethod "$BaseUrl/api/ssis" -Headers $Headers -TimeoutSec 120
        if ($r.error) { return $r.error }

        $pkgs = $r.data.packages
        if ($pkgs.Count -ne 1) { return "expected 1 discovered package, got $($pkgs.Count)" }

        $p = $pkgs[0]
        if ($p.folder -ne 'ZZTestFolder') { return "folder parsed as '$($p.folder)'" }
        if ($p.project -ne 'ZZTestProject') { return "project parsed as '$($p.project)'" }
        if ($p.package -ne 'ZZTestPackage.dtsx') { return "package parsed as '$($p.package)'" }
        if ($p.stepId -ne 7) { return "attributed to step $($p.stepId)" }

        # It never executes, so it must be reported as not run rather than omitted.
        if ($p.status -ne 'not run') { return "status was '$($p.status)', expected 'not run'" }
        if ($r.data.notRun -ne 1) { return "notRun count was $($r.data.notRun)" }
        $true
    }

    Test-That 'T20' 'a catalogue rule diagnoses a failing step in place' {
        # The point of the catalogue is that the explanation reaches the failing step,
        # not that it sits in a tab. Added and removed inside the test so the shared
        # catalogue is left exactly as it was found.
        $pattern = 'selftest deliberate failure'
        $title = 'ZZ selftest - deliberate failure'
        try {
            $body = @{
                environment = 'SELFTEST'; pattern = $pattern; matchType = 'Contains'
                title = $title; cause = 'The fixture fails on purpose at step 2.'
                suggestedAction = 'Nothing - this is a test fixture.'; priority = 5
            } | ConvertTo-Json

            $add = Invoke-RestMethod "$BaseUrl/api/diagnostics/rules" -Method Post -Headers $Headers -ContentType 'application/json' -Body $body -TimeoutSec 60
            if (-not $add.success) { return "could not add the rule: $($add.message)" }

            # Produce the failure here rather than relying on an earlier test's leftovers:
            # the stop tests leave step 2 CANCELLED, which carries no error text to match.
            $start = @{ environment = 'SELFTEST' } | ConvertTo-Json
            Invoke-RestMethod "$BaseUrl/api/actions/start" -Method Post -Headers $Headers -ContentType 'application/json' -Body $start -TimeoutSec 30 | Out-Null
            Wait-For -Describe 'the fixture to start' -Condition { (Get-Status).isRunning }
            Wait-For -TimeoutSeconds 180 -Describe 'the fixture to fail at step 2' -Condition {
                $x = Get-Status
                (-not $x.isRunning) -and $x.outcome -eq 'failed'
            }

            $s = Get-Status
            $step2 = $s.steps | Where-Object stepId -eq 2
            if ($step2.outcome -ne 'failed') { return "step 2 is '$($step2.outcome)', expected failed" }
            if (-not $step2.diagnosis) { return 'the failing step carried no diagnosis' }
            if ($step2.diagnosis.title -ne $title) { return "diagnosis was '$($step2.diagnosis.title)'" }

            # A step that succeeded must NOT carry one.
            $step1 = $s.steps | Where-Object stepId -eq 1
            if ($step1.diagnosis) { return 'a succeeded step was given a diagnosis' }

            # And the report should now count it as explained.
            $rep = Invoke-RestMethod "$BaseUrl/api/diagnostics" -Headers $Headers -TimeoutSec 120
            if ($rep.error) { return $rep.error }
            if (-not ($rep.data.recentFailures | Where-Object { $_.title -eq $title })) {
                return 'the report did not attribute any failure to the new rule'
            }
            $true
        }
        finally {
            sqlcmd -S $ControlServer -E -C -b -Q "DELETE FROM EpsiSsisHost.diag.FailureRule WHERE Pattern = N'$pattern';" | Out-Null
        }
    }

    Test-That 'T21' 'an invalid rule is refused rather than stored' {
        $bad = @{ environment = 'SELFTEST'; pattern = '([unclosed'; matchType = 'Regex'; title = 'bad'; priority = 100 } | ConvertTo-Json
        $r = Invoke-RestMethod "$BaseUrl/api/diagnostics/rules" -Method Post -Headers $Headers -ContentType 'application/json' -Body $bad -TimeoutSec 60
        if ($r.success) { return 'an invalid regular expression was accepted' }

        $noTitle = @{ environment = 'SELFTEST'; pattern = 'something'; matchType = 'Contains'; title = ''; priority = 100 } | ConvertTo-Json
        $r2 = Invoke-RestMethod "$BaseUrl/api/diagnostics/rules" -Method Post -Headers $Headers -ContentType 'application/json' -Body $noTitle -TimeoutSec 60
        if ($r2.success) { return 'a rule with no title was accepted' }
        $true
    }

    Test-That 'T22' 'the production extract was never touched by this run' {
        $audit = (Invoke-RestMethod "$BaseUrl/api/audit" -Headers $Headers -TimeoutSec 30).data
        if ($audit | Where-Object { $_ -match 'Data Extract From CLARITY' -and $_ -match '\| OK \|' }) {
            return 'an action against the real extract succeeded'
        }
        $true
    }

    # ---- the unattended checker (--check) ---------------------------------
    # Runs as a separate process rather than over HTTP: that is how the Agent job
    # invokes it, so that is what gets tested.

    # Both of these must wait for THEIR OWN run to finish, not merely for the job to be
    # idle in the wanted state: the previous test leaves the fixture idle and failed, so
    # "wait until it is failed" is satisfied before the new run has even registered as
    # running. Getting that wrong made T26-T28 check a job that was still in flight.
    # CmdletBinding matters here: without it a mistyped -Parameter is silently swallowed
    # into $args and the scriptblock arrives as $null.
    function Start-FixtureAndWait {
        [CmdletBinding()]
        param(
            [Parameter(Mandatory)][scriptblock]$Condition,
            [Parameter(Mandatory)][string]$Describe,
            [int]$TimeoutSeconds = 150
        )
        $body = @{ environment = 'SELFTEST' } | ConvertTo-Json
        Invoke-RestMethod "$BaseUrl/api/actions/start" -Method Post -Headers $Headers `
            -ContentType 'application/json' -Body $body -TimeoutSec 30 | Out-Null
        Wait-For -TimeoutSeconds 60 -Describe 'the fixture to start' -Condition { (Get-Status).isRunning }
        Wait-For -TimeoutSeconds $TimeoutSeconds -Describe $Describe -Condition $Condition
    }

    # A cancelled run is not a failure, so the job's own failure-only notification says
    # nothing about it. Stopped during step 1, which waits 20s - a wide enough window.
    Test-That 'T23' 'a run that was stopped and never restarted is reported' {
        Start-FixtureAndWait -Describe 'step 1 to be under way' -Condition {
            ((Get-Status).steps | Where-Object stepId -eq 1).outcome -eq 'running'
        }

        $stop = @{ environment = 'SELFTEST'; jobName = 'ZZ_ExtractConsole_SelfTest'; confirm = 'ZZ_ExtractConsole_SelfTest' } | ConvertTo-Json
        Invoke-RestMethod "$BaseUrl/api/actions/stop" -Method Post -Headers $Headers -ContentType 'application/json' -Body $stop -TimeoutSec 30 | Out-Null
        Wait-For -Describe 'the fixture to report cancelled' -Condition {
            $s = Get-Status
            -not $s.isRunning -and $s.outcome -eq 'canceled'
        }

        $out = Invoke-Checker '--dry-run'
        if ($out.Code -ne 0) { return "expected exit 0, got $($out.Code): $($out.Text)" }
        if ($out.Text -notmatch 'stopped before it finished') {
            return "a cancelled run produced no finding: $($out.Text)"
        }
        $true
    }

    Test-That 'T24' 'a failed run is reported as Critical, naming the step' {
        # Step 1 waits 20s and succeeds, step 2 waits 20s and fails.
        Start-FixtureAndWait -Describe 'the fixture to fail at step 2' -Condition {
            $s = Get-Status
            -not $s.isRunning -and $s.outcome -eq 'failed'
        }

        $out = Invoke-Checker '--dry-run'
        if ($out.Code -ne 0)                  { return "expected exit 0 for a finding, got $($out.Code)" }
        if ($out.Text -notmatch 'CRITICAL')   { return "no CRITICAL in output: $($out.Text)" }
        if ($out.Text -notmatch 'step 2')     { return "the failing step was not named: $($out.Text)" }
        $true
    }

    Test-That 'T25' 'a dry run records nothing' {
        $before = Get-CheckRunCount
        Invoke-Checker '--dry-run' | Out-Null
        if ((Get-CheckRunCount) -ne $before) { return 'a dry run wrote a CheckRun row' }
        $true
    }

    Test-That 'T26' 'the same run is recorded once and not reported twice' {
        $before = Get-CheckRunCount
        $first = Invoke-Checker
        if ($first.Text -match 'already reported') { return 'the first pass thought it had already reported' }
        $afterFirst = Get-CheckRunCount
        if ($afterFirst -ne $before + 1) { return "expected one new row, got $($afterFirst - $before)" }

        $second = Invoke-Checker
        if ($second.Text -notmatch 'already reported') { return "the second pass reported again: $($second.Text)" }
        if ((Get-CheckRunCount) -ne $afterFirst) { return 'the second pass wrote another row' }
        $true
    }

    Test-That 'T27' '--force overrides the suppression' {
        $before = Get-CheckRunCount
        $out = Invoke-Checker '--force'
        if ($out.Text -match 'already reported') { return '--force was ignored' }
        if ((Get-CheckRunCount) -ne $before + 1) { return '--force did not record a row' }
        $true
    }

    Test-That 'T28' '--fail-on-critical turns a finding into a non-zero exit' {
        $out = Invoke-Checker '--dry-run', '--fail-on-critical'
        if ($out.Code -ne 2) { return "expected exit 2, got $($out.Code)" }
        $true
    }

    # An environment the checker cannot act on must fail loudly. A scheduled job that
    # silently checks nothing is the worst outcome: green every tick, nobody watching.
    Test-That 'T29' 'an unknown or disabled environment fails rather than passing quietly' {
        $unknown = Invoke-Checker '--dry-run' -Env 'NO_SUCH_ENV'
        if ($unknown.Code -ne 3)                   { return "unknown env exited $($unknown.Code), expected 3" }
        if ($unknown.Text -notmatch 'SELFTEST')    { return 'the error did not list the environments it does know' }

        $off = Invoke-Checker '--dry-run' -Env 'SELFTEST_DISABLED'
        if ($off.Code -ne 3)                       { return "disabled env exited $($off.Code), expected 3" }
        if ($off.Text -notmatch 'Enabled=false')   { return "the error did not explain why: $($off.Text)" }
        $true
    }

    # The upsert log is read from the real STRATA_AUDIT on the data instance - there is
    # no fixture for it. Read-only, so pointing at the real table is safe, and the
    # assertions are about SHAPE rather than particular values.
    Test-That 'T30' 'the upsert log reports a day, its datasets and the whole history' {
        $r = Invoke-RestMethod "$BaseUrl/api/upserts?env=SELFTEST" -Headers $Headers -TimeoutSec 90
        if ($r.error) { return $r.error }
        $d = $r.data

        if ($null -eq $d.availableDays) { return 'availableDays missing' }
        if ($d.distinctDays -lt 1)      { return 'no days reported' }
        if ($d.totalRows -lt $d.rows.Count) { return 'totalRows is smaller than the day it returned' }

        # Defaults to the NEWEST day present, not to today - DEV runs ad hoc.
        $newest = ($d.availableDays | Sort-Object { [datetime]$_.date } -Descending)[0]
        if ([datetime]$d.processDate -ne [datetime]$newest.date) {
            return "defaulted to $($d.processDate), expected newest $($newest.date)"
        }
        if ($newest.rowCount -ne $d.rows.Count) {
            return "picker says $($newest.rowCount) rows, payload returned $($d.rows.Count)"
        }
        if (-not $d.rows.Count) { return 'the newest day returned no rows' }
        $true
    }

    Test-That 'T31' 'rows carry the update/insert split and map to recon dataset keys' {
        $d = (Invoke-RestMethod "$BaseUrl/api/upserts?env=SELFTEST" -Headers $Headers -TimeoutSec 90).data
        foreach ($row in $d.rows) {
            $sum = $row.rowsUpdated + $row.rowsInserted
            if ($sum -ne $row.totalRowsProcessed) {
                return "$($row.processName): updated+inserted=$sum but total=$($row.totalRowsProcessed)"
            }
            if ($row.outcome -notin @('succeeded','failed','running','unknown')) {
                return "$($row.processName): unmapped outcome '$($row.outcome)'"
            }
        }
        # The point of the key: a row here can be lined up with the recon panel.
        if (-not ($d.rows | Where-Object { $_.datasetKey })) {
            return 'no row resolved a dataset key - the ProcessName mapping is broken'
        }
        $true
    }

    # Failures belong to the day you picked. A clean day must report none even though
    # earlier days failed; the picker's own failureCount is what keeps those findable.
    Test-That 'T32' 'failures are scoped to the selected day' {
        $d = (Invoke-RestMethod "$BaseUrl/api/upserts?env=SELFTEST" -Headers $Headers -TimeoutSec 90).data

        foreach ($f in $d.failures) {
            if ($f.outcome -eq 'succeeded') { return 'a successful row appeared in failures' }
            if (([datetime]$f.processDate) -ne ([datetime]$d.processDate)) {
                return "a failure from $($f.processDate) leaked into the view of $($d.processDate)"
            }
        }

        # Every day the picker marks as failed must actually report those failures when
        # selected, and every day it marks clean must report none.
        foreach ($day in $d.availableDays) {
            $iso = ([datetime]$day.date).ToString('yyyy-MM-dd')
            $one = (Invoke-RestMethod "$BaseUrl/api/upserts?env=SELFTEST&date=$iso" -Headers $Headers -TimeoutSec 90).data
            if ($one.failures.Count -ne $day.failureCount) {
                return "$iso - picker says $($day.failureCount) failure(s), day returned $($one.failures.Count)"
            }
        }
        $true
    }

    Test-That 'T33' 'asking for a specific day returns only that day' {
        $d = (Invoke-RestMethod "$BaseUrl/api/upserts?env=SELFTEST" -Headers $Headers -TimeoutSec 90).data
        if ($d.availableDays.Count -lt 2) {
            Write-Host '        (skipped: only one day recorded)' -ForegroundColor DarkGray
            return $true
        }
        $target = ([datetime](($d.availableDays | Sort-Object { [datetime]$_.date } -Descending)[1].date)).ToString('yyyy-MM-dd')
        $one = (Invoke-RestMethod "$BaseUrl/api/upserts?env=SELFTEST&date=$target" -Headers $Headers -TimeoutSec 90).data
        if (([datetime]$one.processDate).ToString('yyyy-MM-dd') -ne $target) {
            return "asked for $target, got $($one.processDate)"
        }
        foreach ($row in $one.rows) {
            if (([datetime]$row.processDate).ToString('yyyy-MM-dd') -ne $target) {
                return "a row from $($row.processDate) leaked into $target"
            }
        }
        $true
    }

    # The environments do not record the same dates. Asking for a day one environment
    # has and the other does not must report THAT day as empty rather than quietly
    # showing another one - and the payload must stay self-consistent, because the UI
    # builds its day picker from availableDays and its table from processDate.
    Test-That 'T34' 'a day the environment has no rows for is reported as that day' {
        $d = (Invoke-RestMethod "$BaseUrl/api/upserts?env=SELFTEST" -Headers $Headers -TimeoutSec 90).data
        $known = @($d.availableDays | ForEach-Object { ([datetime]$_.date).ToString('yyyy-MM-dd') })

        # A date the log certainly does not hold.
        $absent = '1999-01-04'
        if ($known -contains $absent) { return "fixture date clash on $absent" }

        $x = (Invoke-RestMethod "$BaseUrl/api/upserts?env=SELFTEST&date=$absent" -Headers $Headers -TimeoutSec 90).data
        if (([datetime]$x.processDate).ToString('yyyy-MM-dd') -ne $absent) {
            return "asked for $absent, got back $($x.processDate) - a silent switch to another day"
        }
        if ($x.rows.Count -ne 0)     { return 'rows leaked into a day with no data' }
        if ($x.failures.Count -ne 0) { return 'failures leaked into a day with no data' }

        # The picker still has to list the real days, so the panel can say what it does hold.
        if ($x.availableDays.Count -ne $d.availableDays.Count) {
            return 'availableDays changed when an empty day was requested'
        }
        $true
    }

    # ---- AdHoc lists. Every one of these runs against the fixture copies in
    # $AdhocDir - never the FTP share the next extract reads.

    Test-That 'T35' 'all ten AdHoc lists are reported, and a missing file is flagged not created' {
        $lists = (Invoke-RestMethod "$BaseUrl/api/adhoc?env=SELFTEST" -Headers $Headers -TimeoutSec 60).data
        if ($lists.Count -ne 10) { return "expected 10 lists, got $($lists.Count)" }

        $pe = $lists | Where-Object key -eq 'PBEncounter'
        if (-not $pe.exists -or $pe.entries -ne 2) { return "PB Encounter: exists=$($pe.exists) entries=$($pe.entries), expected 2" }
        $hc = $lists | Where-Object key -eq 'HBCharge'
        if ($hc.entries -ne 0 -or ($hc.columns -join '|') -ne 'HSP_ACCOUNT_ID|TX_ID') { return 'HB Charge reported wrongly' }
        $pc = $lists | Where-Object key -eq 'PBCharge'
        if (($pc.columns -join '|') -ne 'HSP_ACCOUNT_ID|TDL_ID') { return 'PB Charge columns wrong' }
        $dx = $lists | Where-Object key -eq 'PBICD10Dx'
        if ($dx.header -ne 'hsp_account_id') { return "header not reported as it is in the file: '$($dx.header)'" }

        $cpt = $lists | Where-Object key -eq 'HBCPT'
        if ($cpt.exists) { return 'HB CPT reported as existing when the fixture omits it' }
        if (Test-Path (Join-Path $AdhocDir 'AdhocHARList - HB CPT.csv')) { return 'listing created a file' }
        $true
    }

    Test-That 'T36' 'a preview validates every line and writes nothing' {
        $before = Get-AdhocBytes 'AdhocHARList - HB Charge.csv'
        # Line 4 is an encounter with no TX_ID - valid now, and written with its trailing pipe.
        $text = "HSP_ACCOUNT_ID|TX_ID`n1234567890|123456789`n1234567890|12345678`n1234567890`n123456789|123456789`n1234567890|123456789`n2234567890`t223456789`n1234567890|123456789|5"
        $r = Invoke-Adhoc 'HBCharge' 'append' @{ environment = 'SELFTEST'; text = $text; commit = $false }

        if ($r.success)                         { return 'a preview with invalid lines reported success' }
        if ($r.headerSkipped -ne 'HSP_ACCOUNT_ID|TX_ID') { return "header not detected: '$($r.headerSkipped)'" }
        if ($r.rejected.Count -ne 3)            { return "expected 3 rejected lines, got $($r.rejected.Count): $(($r.rejected | ForEach-Object { "$($_.line) $($_.reason)" }) -join '; ')" }
        if (-not ($r.rejected | Where-Object { $_.line -eq 3 -and $_.reason -match 'TX_ID must be 9 digits' })) { return 'short TX_ID not rejected with its reason' }
        if (-not ($r.rejected | Where-Object { $_.line -eq 5 -and $_.reason -match 'HSP_ACCOUNT_ID must be 10 digits' })) { return 'short HSP_ACCOUNT_ID not rejected' }
        if (-not ($r.rejected | Where-Object { $_.line -eq 8 -and $_.reason -match 'Expected HSP_ACCOUNT_ID\|\[TX_ID\]' })) { return 'a third value was not rejected' }
        if ($r.rejected | Where-Object { $_.line -eq 4 }) { return 'an encounter with no TX_ID was rejected' }
        if ($r.duplicateInInput -ne 1)          { return "a repeated line was not counted once: $($r.duplicateInInput)" }
        if ($r.changeCount -ne 3)               { return "expected 3 valid entries (one encounter-only, one via a tab), got $($r.changeCount)" }
        if ($r.sample -notcontains '1234567890|') { return "the encounter on its own was not shown as '1234567890|': $($r.sample -join ', ')" }
        if ((Get-AdhocBytes 'AdhocHARList - HB Charge.csv') -ne $before) { return 'a preview changed the file' }

        # An invalid batch must not be committable either - all lines valid, or nothing.
        $c = Invoke-Adhoc 'HBCharge' 'append' @{ environment = 'SELFTEST'; text = $text; commit = $true }
        if ($c.committed) { return 'a batch with invalid lines was committed' }
        if ((Get-AdhocBytes 'AdhocHARList - HB Charge.csv') -ne $before) { return 'a refused commit changed the file' }
        $true
    }

    Test-That 'T37' 'an append adds at the end, keeps the header, and skips what is already there' {
        $r = Invoke-Adhoc 'HBCharge' 'append' @{ environment = 'SELFTEST'; text = "1234567890|123456789`n2234567890|223456789"; commit = $true }
        if (-not $r.committed) { return "not committed: $($r.message)" }
        $want = 'HSP_ACCOUNT_ID|TX_ID\r\n1234567890|123456789\r\n2234567890|223456789'
        if ((Get-AdhocBytes 'AdhocHARList - HB Charge.csv') -ne $want) {
            return "HB Charge is '$(Get-AdhocBytes 'AdhocHARList - HB Charge.csv')', expected '$want'"
        }

        # Encounters without a transaction - the common case. Typed with or without the
        # pipe they are one entry, written "3234567890|"; a pair already present is skipped.
        $r = Invoke-Adhoc 'HBCharge' 'append' @{ environment = 'SELFTEST'; text = "3234567890`n3234567890|`n1234567890|123456789"; commit = $true }
        if (-not $r.committed) { return "encounter-only append not committed: $($r.message)" }
        if ($r.changeCount -ne 1 -or $r.duplicateInInput -ne 1 -or $r.alreadyPresent -ne 1) {
            return "expected 1 appended / 1 repeat / 1 already there, got $($r.changeCount)/$($r.duplicateInInput)/$($r.alreadyPresent)"
        }
        $want = 'HSP_ACCOUNT_ID|TX_ID\r\n1234567890|123456789\r\n2234567890|223456789\r\n3234567890|'
        if ((Get-AdhocBytes 'AdhocHARList - HB Charge.csv') -ne $want) {
            return "HB Charge is '$(Get-AdhocBytes 'AdhocHARList - HB Charge.csv')', expected '$want'"
        }

        # PB: 15-digit TDL_ID, mixed with an encounter on its own.
        $r = Invoke-Adhoc 'PBCharge' 'append' @{ environment = 'SELFTEST'; text = "4234567890|123456789012345`n4234567891"; commit = $true }
        $want = 'HSP_ACCOUNT_ID|TDL_ID\r\n4234567890|123456789012345\r\n4234567891|'
        if ((Get-AdhocBytes 'AdhocHARList - PB Charge.csv') -ne $want) {
            return "PB Charge is '$(Get-AdhocBytes 'AdhocHARList - PB Charge.csv')', expected '$want'"
        }

        $r = Invoke-Adhoc 'PBEncounter' 'append' @{ environment = 'SELFTEST'; text = "1000000002`n1000000003"; commit = $true }
        if ($r.alreadyPresent -ne 1 -or $r.changeCount -ne 1) { return "expected 1 skipped and 1 appended, got $($r.alreadyPresent)/$($r.changeCount)" }
        $want = 'HSP_ACCOUNT_ID\r\n1000000001\r\n1000000002\r\n1000000003'
        if ((Get-AdhocBytes 'AdhocHARList - PB Encounter.csv') -ne $want) { return "PB Encounter is '$(Get-AdhocBytes 'AdhocHARList - PB Encounter.csv')'" }

        # A file that already ends in CRLF must not gain a blank line; its header stays lower-case.
        $r = Invoke-Adhoc 'PBICD10Dx' 'append' @{ environment = 'SELFTEST'; text = '3000000001'; commit = $true }
        $want = 'hsp_account_id\r\n3000000001'
        if ((Get-AdhocBytes 'AdhocHARList - PB ICD10Dx.csv') -ne $want) { return "PB ICD10Dx is '$(Get-AdhocBytes 'AdhocHARList - PB ICD10Dx.csv')'" }

        $r = Invoke-Adhoc 'HBCPT' 'append' @{ environment = 'SELFTEST'; text = '1234567890'; commit = $true }
        if ($r.committed) { return 'appended to a file that does not exist' }
        if (Test-Path (Join-Path $AdhocDir 'AdhocHARList - HB CPT.csv')) { return 'an append created a missing file' }
        $true
    }

    Test-That 'T38' 'a remove takes out only the listed entries and keeps the rest in order' {
        $r = Invoke-Adhoc 'PBEncounter' 'remove' @{ environment = 'SELFTEST'; text = "1000000001`n1999999999"; commit = $true }
        if (-not $r.committed) { return "not committed: $($r.message)" }
        if ($r.notFound -ne 1)  { return "the absent entry was not reported: notFound=$($r.notFound)" }
        $want = 'HSP_ACCOUNT_ID\r\n1000000002\r\n1000000003'
        if ((Get-AdhocBytes 'AdhocHARList - PB Encounter.csv') -ne $want) { return "PB Encounter is '$(Get-AdhocBytes 'AdhocHARList - PB Encounter.csv')'" }

        # An encounter-only entry sits in the file as "3234567890|"; typing it without the
        # pipe must still find it.
        $r = Invoke-Adhoc 'HBCharge' 'remove' @{ environment = 'SELFTEST'; text = '3234567890'; commit = $true }
        if (-not $r.committed) { return "could not remove an encounter-only entry typed without its pipe: $($r.message)" }
        $want = 'HSP_ACCOUNT_ID|TX_ID\r\n1234567890|123456789\r\n2234567890|223456789'
        if ((Get-AdhocBytes 'AdhocHARList - HB Charge.csv') -ne $want) { return "HB Charge is '$(Get-AdhocBytes 'AdhocHARList - HB Charge.csv')'" }
        $true
    }

    Test-That 'T39' 'an Excel upload is read, including a 15-digit TDL_ID stored as a number' {
        $xlsx = Join-Path ([IO.Path]::GetTempPath()) 'ec-selftest-finance.xlsx'
        # 123456789012345 as Excel stores it; the second TDL_ID lost a digit.
        # Row 4 has no TDL_ID cell at all - an encounter on its own, as Finance often sends.
        New-TestXlsx -Path $xlsx -Rows @(
            @('HSP_ACCOUNT_ID', 'TDL_ID'),
            @('1234567890', '1.23456789012345E+14'),
            @('2234567890', '22345678901234'),
            @(,'5234567890'))
        $b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($xlsx))

        $r = Invoke-Adhoc 'PBCharge' 'append' @{ environment = 'SELFTEST'; fileName = 'finance.xlsx'; fileBase64 = $b64; commit = $false }
        if ($r.headerSkipped -ne 'HSP_ACCOUNT_ID|TDL_ID') { return "header row not skipped: '$($r.headerSkipped)'" }
        if ($r.rejected.Count -ne 1 -or $r.rejected[0].line -ne 3) { return 'the 14-digit TDL_ID was not rejected at Excel row 3' }
        if (($r.sample -join ',') -ne '1234567890|123456789012345,5234567890|') {
            return "expected the E-notation number restored and the encounter-only row given its pipe, got: $($r.sample -join ',')"
        }

        $old = Invoke-Adhoc 'PBCharge' 'append' @{ environment = 'SELFTEST'; fileName = 'finance.xls'; fileBase64 = $b64; commit = $false }
        if (-not ($old.rejected | Where-Object { $_.reason -match '\.xls is not supported' })) { return 'old .xls not refused with a clear reason' }
        Remove-Item $xlsx -Force -ErrorAction SilentlyContinue
        $true
    }

    Test-That 'T40' 'a change is refused while the extract is running' {
        $before = Get-AdhocBytes 'AdhocHARList - PB ICD10Dx.csv'
        $body = @{ environment = 'SELFTEST' } | ConvertTo-Json
        Invoke-RestMethod "$BaseUrl/api/actions/start" -Method Post -Headers $Headers -ContentType 'application/json' -Body $body -TimeoutSec 30 | Out-Null
        Wait-For -TimeoutSeconds 60 -Describe 'the fixture to start' -Condition { (Get-Status).isRunning }

        $r = Invoke-Adhoc 'PBICD10Dx' 'append' @{ environment = 'SELFTEST'; text = '3000000009'; commit = $true }

        $stop = @{ environment = 'SELFTEST'; jobName = 'ZZ_ExtractConsole_SelfTest'; confirm = 'ZZ_ExtractConsole_SelfTest' } | ConvertTo-Json
        Invoke-RestMethod "$BaseUrl/api/actions/stop" -Method Post -Headers $Headers -ContentType 'application/json' -Body $stop -TimeoutSec 30 | Out-Null
        Wait-For -Describe 'the fixture to stop' -Condition { -not (Get-Status).isRunning }

        if ($r.committed)                       { return 'appended while the extract was running' }
        if ($r.message -notmatch 'is running')  { return "refused, but not saying why: $($r.message)" }
        if ((Get-AdhocBytes 'AdhocHARList - PB ICD10Dx.csv') -ne $before) { return 'the file changed while the job ran' }
        $true
    }

    Test-That 'T41' 'on production a change needs the environment name typed' {
        $before = Get-AdhocBytes 'AdhocHARList - PB ICD10Dx.csv'
        $r = Invoke-Adhoc 'PBICD10Dx' 'append' @{ environment = 'SELFTEST_PROD'; text = '3000000002'; commit = $true }
        if ($r.committed) { return 'a production change went through without confirmation' }
        $r = Invoke-Adhoc 'PBICD10Dx' 'append' @{ environment = 'SELFTEST_PROD'; text = '3000000002'; commit = $true; confirm = 'SELFTEST' }
        if ($r.committed) { return 'the wrong environment name was accepted' }
        if ((Get-AdhocBytes 'AdhocHARList - PB ICD10Dx.csv') -ne $before) { return 'an unconfirmed change touched the file' }

        $r = Invoke-Adhoc 'PBICD10Dx' 'append' @{ environment = 'SELFTEST_PROD'; text = '3000000002'; commit = $true; confirm = 'SELFTEST_PROD' }
        if (-not $r.committed) { return "a confirmed production change was refused: $($r.message)" }
        $true
    }

    # ---- extract volume. Reads the real epsi_staging.dbo.etl_audit_log on the DEV data
    # instance - read-only, so no fixture. Assertions are about shape and rules, plus two
    # facts DEV's history fixes for good: the renamed packages and the TEST row.

    Test-That 'T42' 'the volume report has the ten datasets, one point per day in range' {
        $d = (Invoke-RestMethod "$BaseUrl/api/volumes?env=SELFTEST&from=2026-09-01&to=2026-09-30" -Headers $Headers -TimeoutSec 120).data
        $want = 'HBEncounter,HBCharge,HBPayments,HBCPT,HBICD10Dx,HBICD10Px,PBEncounter,PBCharge,PBPayments,PBICD10Dx'
        if (($d.series.key -join ',') -ne $want) { return "datasets/order wrong: $($d.series.key -join ',')" }
        foreach ($s in $d.series) {
            if ($s.points.Count -ne 30) { return "$($s.key) has $($s.points.Count) points for a 30-day range" }
        }
        if ($d.total.Count -ne 30) { return "total has $($d.total.Count) points" }
        $true
    }

    Test-That 'T43' 'no successful run is a gap, and a 0-row success is kept as 0' {
        $d = (Invoke-RestMethod "$BaseUrl/api/volumes?env=SELFTEST&from=2026-07-01&to=2026-10-15" -Headers $Headers -TimeoutSec 120).data
        foreach ($s in $d.series) {
            foreach ($p in $s.points) {
                if ($p.succeededRuns -eq 0 -and $null -ne $p.rows) { return "$($s.key) $($p.date): value with no successful run" }
                if ($p.succeededRuns -gt 0 -and $null -eq $p.rows) { return "$($s.key) $($p.date): successful run but no value" }
            }
            $zeros = @($s.points | Where-Object { $_.rows -eq 0 }).Count
            if ($zeros -ne $s.summary.zeroDays) { return "$($s.key): $zeros zero points but zeroDays=$($s.summary.zeroDays)" }
        }
        # Every all-zero day really is all zero across the datasets that ran.
        foreach ($z in $d.allZeroDays) {
            $i = [array]::IndexOf(@($d.total.date), $z)
            foreach ($s in $d.series) {
                $v = $s.points[$i].rows
                if ($null -ne $v -and $v -ne 0) { return "$z flagged all-zero but $($s.key) loaded $v" }
            }
        }
        $true
    }

    Test-That 'T44' 'renamed packages map to one dataset, and non-staging packages are ignored' {
        $d = (Invoke-RestMethod "$BaseUrl/api/volumes?env=SELFTEST&from=2000-01-01" -Headers $Headers -TimeoutSec 180).data
        # "All" starts where the log does, not in 2000.
        if (([datetime]$d.from) -ne ([datetime]$d.earliestInLog)) { return "range starts $($d.from), log starts $($d.earliestInLog)" }

        $hb = $d.series | Where-Object key -eq 'HBCharge'
        foreach ($pkg in 'LoadStagingHBChargeTable', 'LoadStagingHBChargeTableOptimize') {
            if ($hb.packages -notcontains $pkg) { return "HB Charge did not pick up ${pkg}: $($hb.packages -join ', ')" }
        }
        $pe = $d.series | Where-Object key -eq 'PBEncounter'
        if ($pe.packages -notcontains 'LoadStagingPBEncounterTableUsingScript') { return 'the UsingScript variant was not mapped to PB Encounter' }
        if ($d.ignored -notcontains 'TEST') { return "TEST not reported as ignored: $($d.ignored -join ', ')" }
        $true
    }

    Test-That 'T45' 'a range given backwards is read forwards, and the summary is computed from data days' {
        $d = (Invoke-RestMethod "$BaseUrl/api/volumes?env=SELFTEST&from=2026-09-30&to=2026-09-01" -Headers $Headers -TimeoutSec 120).data
        if (([datetime]$d.from).ToString('yyyy-MM-dd') -ne '2026-09-01' -or ([datetime]$d.to).ToString('yyyy-MM-dd') -ne '2026-09-30') {
            return "backwards range came back as $($d.from)..$($d.to)"
        }
        foreach ($s in $d.series) {
            $loads = @($s.points | Where-Object { $_.rows -gt 0 } | ForEach-Object { [long]$_.rows })
            if ($loads.Count -eq 0) { continue }
            $peak = ($loads | Measure-Object -Maximum).Maximum
            if ([long]$s.summary.peak -ne $peak) { return "$($s.key): peak $($s.summary.peak), data says $peak" }
            $sorted = $loads | Sort-Object
            $mid = [math]::Floor($sorted.Count / 2)
            $median = if ($sorted.Count % 2) { $sorted[$mid] } else { [long][math]::Floor(($sorted[$mid - 1] + $sorted[$mid]) / 2) }
            if ([long]$s.summary.typical -ne $median) { return "$($s.key): typical $($s.summary.typical), median of data days is $median" }
        }
        $true
    }

    Test-That 'T46' 'a file split into _f1, _f2 parts is counted as one dataset' {
        $r = (Invoke-RestMethod "$BaseUrl/api/recon?env=SELFTEST_RECON&date=2026-10-19&records=true" -Headers $Headers -TimeoutSec 120).data
        $cpt = $r | Where-Object key -eq 'HBCPT'
        if ($cpt.fileParts -ne 2)            { return "HB CPT parts=$($cpt.fileParts), expected 2" }
        if ($cpt.fileName -ne 'MayoClinic_HBCPT_10192026_f1-f2.txt') { return "HB CPT shown as '$($cpt.fileName)'" }
        if ($cpt.fileLines -ne 6)            { return "declared lines $($cpt.fileLines), expected 3+3 = 6" }
        if ($cpt.fileRecords -ne 5)          { return "records $($cpt.fileRecords), expected 3+2 = 5 (each part's header skipped)" }
        if ($cpt.continuationLines -ne 1)    { return "continuations $($cpt.continuationLines), expected 1" }
        if (-not $cpt.fileModifiedLocal)     { return 'split parts reported as not found' }

        $px = $r | Where-Object key -eq 'HBICD10Px'
        if ($px.fileParts -ne 2 -or $px.fileRecords -ne 2) { return "HB ICD10Px parts/records $($px.fileParts)/$($px.fileRecords)" }
        if ($px.fileNote -notmatch 'f2 not found') { return "the missing part was not called out: '$($px.fileNote)'" }

        $pay = $r | Where-Object key -eq 'HBPayments'
        if ($pay.fileParts -ne 0 -or $pay.fileName -ne 'MayoClinic_HBPayments_10192026.txt') { return 'an unsplit file is no longer resolved as before' }
        if ($pay.fileLines -ne 2 -or $pay.fileRecords -ne 2) { return "HB Payments lines/records $($pay.fileLines)/$($pay.fileRecords)" }
        $true
    }

    # checker.ps1 is the same logic in PowerShell, run by the Agent job. Load its functions
    # (not its main body) under Windows PowerShell 5.1, point it at the fixtures, and it
    # must reach the same numbers as the console.
    Test-That 'T47' 'the scheduled checker counts split files the same way as the console' {
        $src = Join-Path (Split-Path $PSScriptRoot -Parent) 'sql\checker.ps1'
        $body = (Get-Content $src -Raw) -replace '(?m)^Invoke-CheckerMain\s*$', ''
        $harness = Join-Path ([IO.Path]::GetTempPath()) 'ec-selftest-checker-harness.ps1'
        $dir = $ReconDir.Replace("'", "''")
        $body + @"

`$Cfg.FileShare = '$dir'
`$ReconTables = @(
    @{ Key = 'HBCPT';      Table = 'dbo.T_HB_CPT_RECON';      File = 'MayoClinic_HBCPT_{date}.txt' }
    @{ Key = 'HBICD10Px';  Table = 'dbo.T_HB_ICD10PX_RECON';  File = 'MayoClinic_HBICD10Px_{date}.txt' }
    @{ Key = 'HBPayments'; Table = 'dbo.T_HB_PAYMENTS_RECON'; File = 'MayoClinic_HBPayments_{date}.txt' })
Get-ReconCounts -ForDate ([datetime]'2026-10-19') | ConvertTo-Json -Compress
"@ | Set-Content $harness -Encoding UTF8

        $json = powershell.exe -NoProfile -ExecutionPolicy Bypass -File $harness -EnvironmentName DEV 2>&1 | Out-String
        Remove-Item $harness -ErrorAction SilentlyContinue
        try { $rows = $json | ConvertFrom-Json } catch { return "checker output was not JSON: $json" }

        $cpt = $rows | Where-Object Key -eq 'HBCPT'
        if ($cpt.FileLines -ne 6 -or $cpt.FileRecords -ne 5 -or $cpt.Continuations -ne 1) {
            return "checker HB CPT lines/records/cont = $($cpt.FileLines)/$($cpt.FileRecords)/$($cpt.Continuations), console says 6/5/1"
        }
        $px = $rows | Where-Object Key -eq 'HBICD10Px'
        if ($px.FileLines -ne 2 -or $px.FileRecords -ne 2) { return "checker HB ICD10Px = $($px.FileLines)/$($px.FileRecords)" }
        $pay = $rows | Where-Object Key -eq 'HBPayments'
        if ($pay.FileLines -ne 2 -or $pay.FileRecords -ne 2) { return "checker HB Payments = $($pay.FileLines)/$($pay.FileRecords)" }
        $true
    }

    # ---- roles. The suite's account becomes each role in turn through a config
    # entry naming it, or a group it is genuinely in (BUILTIN\Users, which every
    # account has). A group that does not exist stands in for "not a member".
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $noGroup = 'MFAD\ZZ_ExtractConsole_NoSuchGroup'

    Test-That 'T48' 'with no Access section configured, everyone is an operator' {
        $b = Invoke-RestMethod "$BaseUrl/api/bootstrap" -Headers $Headers -TimeoutSec 30
        if ($b.access.roleKey -ne 'operator')                         { return "role was $($b.access.roleKey)" }
        if ($b.access.grantedBy -ne 'access control not configured')  { return "grantedBy was '$($b.access.grantedBy)'" }
        if (-not $b.access.canChange)                                 { return 'operator cannot change' }
        $true
    }

    Test-That 'T49' 'an AD group in the list grants its role' {
        $c = Start-RoleConsole -Port 5411 -Access @{ Operators = @('BUILTIN\Users') }
        try {
            $b = (Invoke-As $c '/api/bootstrap').Json
            if ($b.access.roleKey -ne 'operator')        { return "role was $($b.access.roleKey)" }
            if ($b.access.grantedBy -ne 'BUILTIN\Users') { return "granted by '$($b.access.grantedBy)', expected the group" }
            $true
        } finally { Stop-RoleConsole $c }
    }

    Test-That 'T50' 'a viewer sees every read, in its environments only, and can change nothing' {
        $c = Start-RoleConsole -Port 5412 -Access @{
            Operators = @($noGroup); Viewers = @($me); ViewerEnvironments = @('SELFTEST_PROD') }
        try {
            $b = (Invoke-As $c '/api/bootstrap').Json
            if ($b.access.roleKey -ne 'viewer')    { return "role was $($b.access.roleKey)" }
            if ($b.access.canChange)               { return 'a viewer can change things' }
            if (-not $b.access.statusPanel)        { return 'a viewer was denied the status panel' }
            if ($b.access.tabs.Count -ne 8)        { return "a viewer sees $($b.access.tabs.Count) tabs, not all 8" }
            if (($b.environments.name -join ',') -ne 'SELFTEST_PROD') { return "environments offered: $($b.environments.name -join ',')" }

            $s = Invoke-As $c '/api/status?env=SELFTEST_PROD'
            if ($s.Status -ne 200 -or $s.Json.error) { return "status read refused: $($s.Status) $($s.Json.error)" }
            $d = Invoke-As $c '/api/status?env=SELFTEST'
            if ($d.Json.error -notmatch 'not available') { return 'a viewer read an environment outside its list' }

            # Every kind of change, refused - and the job demonstrably not started.
            # Refused at the role gate, before any endpoint runs.
            $start = Invoke-As $c '/api/actions/start' 'POST' @{ environment = 'SELFTEST_PROD'; confirm = 'SELFTEST_PROD' }
            if ($start.Status -ne 403 -or $start.Json.error -notmatch 'Viewer role') { return "start was not refused by role: $($start.Status) $($start.Json.error)" }
            Start-Sleep -Seconds 3
            if ((Get-Status).isRunning) { return 'a viewer''s start request started the job' }

            $add = Invoke-As $c '/api/adhoc/PBICD10Dx/append' 'POST' @{ environment = 'SELFTEST_PROD'; text = '3000000077'; commit = $true; confirm = 'SELFTEST_PROD' }
            if ($add.Status -ne 403) { return "a viewer's AdHoc append got $($add.Status)" }
            if ((Get-AdhocBytes 'AdhocHARList - PB ICD10Dx.csv') -match '3000000077') { return 'the list file changed' }

            $rule = Invoke-As $c '/api/diagnostics/rules' 'POST' @{ environment = 'SELFTEST_PROD'; pattern = 'x'; matchType = 'Contains'; title = 'viewer test'; priority = 100 }
            if ($rule.Status -ne 403) { return "a viewer's new catalogue rule got $($rule.Status)" }
            $true
        } finally { Stop-RoleConsole $c }
    }

    Test-That 'T51' 'finance gets its four tabs, nothing above them, and every other endpoint is refused' {
        $c = Start-RoleConsole -Port 5413 -Access @{
            Operators = @($noGroup); Finance = @($me); FinanceEnvironments = @('SELFTEST_PROD') }
        try {
            $b = (Invoke-As $c '/api/bootstrap').Json
            if ($b.access.roleKey -ne 'finance')  { return "role was $($b.access.roleKey)" }
            if (($b.access.tabs -join ',') -ne 'history,volume,recon,files') { return "tabs were $($b.access.tabs -join ',')" }
            if ($b.access.statusPanel)            { return 'finance was given the status panel' }
            if ($b.access.canChange)              { return 'finance can change things' }

            foreach ($ok in '/api/history?env=SELFTEST_PROD', '/api/volumes?env=SELFTEST_PROD',
                            '/api/recon?env=SELFTEST_PROD', '/api/files?env=SELFTEST_PROD') {
                $r = Invoke-As $c $ok
                if ($r.Status -ne 200) { return "finance refused its own tab: $ok -> $($r.Status)" }
            }
            # Hidden in the browser is not enough - each of these must refuse outright.
            foreach ($no in '/api/status?env=SELFTEST_PROD', '/api/ssis?env=SELFTEST_PROD', '/api/upserts?env=SELFTEST_PROD',
                            '/api/adhoc?env=SELFTEST_PROD', '/api/adhoc/HBCharge?env=SELFTEST_PROD',
                            '/api/diagnostics?env=SELFTEST_PROD', '/api/audit') {
                $r = Invoke-As $c $no
                if ($r.Status -ne 403) { return "finance reached $no -> $($r.Status)" }
            }
            $start = Invoke-As $c '/api/actions/start' 'POST' @{ environment = 'SELFTEST_PROD'; confirm = 'SELFTEST_PROD' }
            if ($start.Status -ne 403) { return "finance's start request got $($start.Status)" }

            $dev = Invoke-As $c '/api/history?env=SELFTEST'
            if ($dev.Json.error -notmatch 'not available') { return 'finance read an environment outside its list' }
            $true
        } finally { Stop-RoleConsole $c }
    }

    Test-That 'T52' 'an account in no group is refused everything except the page explaining why' {
        $c = Start-RoleConsole -Port 5414 -Access @{ Operators = @($noGroup) }
        try {
            $b = Invoke-As $c '/api/bootstrap'
            if ($b.Status -ne 200 -or $b.Json.access.roleKey -ne 'none') { return "bootstrap: $($b.Status) role $($b.Json.access.roleKey)" }
            if ($b.Json.environments.Count -ne 0) { return 'environments were offered to a refused account' }
            $h = Invoke-As $c '/api/history?env=SELFTEST'
            if ($h.Status -ne 403)                         { return "history got $($h.Status)" }
            if ($h.Json.error -notmatch [regex]::Escape($me)) { return "the refusal did not name the account: $($h.Json.error)" }
            $true
        } finally { Stop-RoleConsole $c }
    }

    Test-That 'T53' 'someone in two lists gets the wider role' {
        $c = Start-RoleConsole -Port 5415 -Access @{ Viewers = @('BUILTIN\Users'); Finance = @($me) }
        try {
            $b = (Invoke-As $c '/api/bootstrap').Json
            if ($b.access.roleKey -ne 'viewer') { return "role was $($b.access.roleKey), expected viewer over finance" }
            $true
        } finally { Stop-RoleConsole $c }
    }

    # Step numbers move whenever a step is inserted; PROD gained two in October 2026 and
    # each time the console attached child jobs to the wrong step. Step 3 of the fixture
    # names the job; step 2 only mentions it in a comment; config claims step 1.
    Test-That 'T54' 'a child job is shown under the step that names it, not the step number in config' {
        $c = Start-RoleConsole -Port 5416 -Mutate {
            param($cfg)
            $st = $cfg.Environments | Where-Object Name -eq 'SELFTEST'
            $st.ChildJobs = @([pscustomobject]@{ StepId = 1; Jobs = @('ZZ_ExtractConsole_SelfTest') })
        }
        try {
            $s = (Invoke-As $c '/api/status?env=SELFTEST').Json.data
            $where = @($s.steps | Where-Object { $_.childJobs.jobName -contains 'ZZ_ExtractConsole_SelfTest' } | ForEach-Object { $_.stepId })
            if (($where -join ',') -ne '3') { return "child job shown under step(s) '$($where -join ',')', expected 3" }

            $three = $s.steps | Where-Object stepId -eq 3
            if ($three.configWarning -notmatch 'to step 1, but this step is the one that starts it') {
                return "the stale step number was not reported: '$($three.configWarning)'"
            }
            $two = $s.steps | Where-Object stepId -eq 2
            if ($two.childJobs.Count -gt 0) { return 'a call that is commented out was treated as launching the job' }
            $true
        } finally { Stop-RoleConsole $c }
    }
}
finally {
    if ($proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
    Get-Process dotnet -ErrorAction SilentlyContinue |
        Where-Object { $_.StartTime -gt (Get-Date).AddMinutes(-30) } |
        ForEach-Object { try { $_.CloseMainWindow() | Out-Null } catch {} }

    if (-not $KeepFixture) {
        Write-Host ''
        Write-Host 'Removing the fixture job...' -ForegroundColor DarkGray
        try { Invoke-SqlFile "$PSScriptRoot\20_remove_selftest_job.sql" | Out-Null }
        catch { Write-Warning "Could not remove the fixture: $($_.Exception.Message)" }

        # The checker tests write real rows to the shared diag tables. Take them back
        # out, or SELFTEST noise accumulates alongside genuine DEV/PROD check history.
        try {
            sqlcmd -S $ControlServer -E -C -b -Q @'
SET NOCOUNT ON;
DELETE c FROM EpsiSsisHost.diag.CheckCount c
  JOIN EpsiSsisHost.diag.CheckRun r ON r.CheckRunId = c.CheckRunId
 WHERE r.EnvironmentName = 'SELFTEST';
DELETE FROM EpsiSsisHost.diag.CheckRun WHERE EnvironmentName = 'SELFTEST';
'@ | Out-Null
        }
        catch { Write-Warning "Could not clear the SELFTEST check rows: $($_.Exception.Message)" }

        foreach ($d in @($script:AdhocDir, $script:ReconDir)) {
            if ($d -and (Test-Path $d)) { Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
}

Write-Host ''
Write-Host ("  {0} passed, {1} failed" -f $script:Pass, $script:Fail) `
    -ForegroundColor $(if ($script:Fail -eq 0) { 'Green' } else { 'Red' })
Write-Host ''

exit $(if ($script:Fail -eq 0) { 0 } else { 1 })
