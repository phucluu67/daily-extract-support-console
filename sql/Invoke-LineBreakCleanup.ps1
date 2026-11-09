<#
.SYNOPSIS
    One-time removal of embedded CR/LF/TAB from the ODS columns that feed the
    extract files.

.DESCRIPTION
    Wraps the two SQL scripts so the cleanup is always bracketed by a scan:

        scan (before)  ->  dry run or apply  ->  scan (after)

    Without -Apply it changes nothing; it shows what would change. With -Apply it
    prompts for confirmation first, because it modifies ODS data.

.PARAMETER Server
    DEV   ropeps913l01\epsidev
    PROD  ropeps013L01

.EXAMPLE
    # See what would change - safe, changes nothing
    pwsh -File sql\Invoke-LineBreakCleanup.ps1 -Server 'ropeps913l01\epsidev'

.EXAMPLE
    # Actually clean DEV (asks for confirmation)
    pwsh -File sql\Invoke-LineBreakCleanup.ps1 -Server 'ropeps913l01\epsidev' -Apply

.EXAMPLE
    # Then PROD
    pwsh -File sql\Invoke-LineBreakCleanup.ps1 -Server 'ropeps013L01' -Apply
#>

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [string]$Server,

    [switch]$Apply
)

$ErrorActionPreference = 'Stop'

$findScript = Join-Path $PSScriptRoot '10_find_embedded_line_breaks.sql'
$fixScript  = Join-Path $PSScriptRoot '11_fix_embedded_line_breaks.sql'

foreach ($p in $findScript, $fixScript) {
    if (-not (Test-Path $p)) { throw "Missing script: $p" }
}

function Invoke-SqlFile {
    param([string]$Path, [string]$Label)
    $out = sqlcmd -S $Server -E -C -b -i $Path -W -s '|' 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed on ${Server}:`n$($out -join [Environment]::NewLine)"
    }
    return $out
}

function Write-Section {
    param([string]$Text)
    Write-Host ''
    Write-Host $Text -ForegroundColor Cyan
    Write-Host ('-' * $Text.Length) -ForegroundColor DarkGray
}

Write-Host ''
Write-Host "Embedded line-break cleanup" -ForegroundColor White
Write-Host "  server : $Server"
Write-Host "  mode   : $(if ($Apply) { 'APPLY - will modify EPSI_ODS data' } else { 'dry run - nothing will change' })" `
    -ForegroundColor $(if ($Apply) { 'Yellow' } else { 'Green' })

# ---- before -----------------------------------------------------------------

Write-Section 'Before'
$before = Invoke-SqlFile -Path $findScript -Label 'Scan (before)'
$before | ForEach-Object { "  $_" }

$nothingToDo = $before -match 'Clean - no embedded'
if ($nothingToDo) {
    Write-Host ''
    Write-Host '  Nothing to clean on this instance.' -ForegroundColor Green
    Write-Host ''
    return
}

# ---- dry run or apply -------------------------------------------------------

if (-not $Apply) {
    Write-Section 'What would change (dry run)'
    Invoke-SqlFile -Path $fixScript -Label 'Dry run' | ForEach-Object { "  $_" }

    Write-Host ''
    Write-Host '  Nothing was changed. Re-run with -Apply to clean.' -ForegroundColor Green
    Write-Host ''
    return
}

if (-not $PSCmdlet.ShouldProcess("EPSI_ODS on $Server", 'Strip CR/LF/TAB from the UDF columns listed above')) {
    Write-Host ''
    Write-Host '  Cancelled - nothing was changed.' -ForegroundColor Yellow
    Write-Host ''
    return
}

# The .sql defaults to a dry run so it is safe to open and run by hand in SSMS.
# Flip it in memory rather than editing the file, so the file never sits on disk
# in a state where opening and running it would modify data by accident.
$applySql = (Get-Content $fixScript -Raw) -replace 'DECLARE\s+@Apply\s+bit\s*=\s*0\s*;', 'DECLARE @Apply bit = 1;'
if ($applySql -notmatch 'DECLARE\s+@Apply\s+bit\s*=\s*1\s*;') {
    throw 'Could not switch the script into apply mode - check the @Apply declaration in 11_fix_embedded_line_breaks.sql.'
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("linebreak-apply-{0}.sql" -f [guid]::NewGuid())
try {
    Set-Content -Path $tmp -Value $applySql -Encoding UTF8
    Write-Section 'Applying'
    Invoke-SqlFile -Path $tmp -Label 'Apply' | ForEach-Object { "  $_" }
}
finally {
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
}

# ---- after ------------------------------------------------------------------

Write-Section 'After'
$after = Invoke-SqlFile -Path $findScript -Label 'Scan (after)'
$after | ForEach-Object { "  $_" }

Write-Host ''
if ($after -match 'Clean - no embedded') {
    Write-Host '  Clean. Re-check the console: Counts & reconciliation -> Count records in files.' -ForegroundColor Green
}
else {
    Write-Host '  Some columns still report characters - see the scan above.' -ForegroundColor Yellow
    Write-Host '  Columns outside the @Targets list in 11_fix_embedded_line_breaks.sql are not touched.' -ForegroundColor Yellow
}
Write-Host ''
