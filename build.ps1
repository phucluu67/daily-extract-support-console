<#
.SYNOPSIS
    Publishes ExtractConsole as a single self-contained .exe.

.DESCRIPTION
    Produces dist\ExtractConsole.exe plus appsettings.json. Nothing else is needed on
    the target machine: no .NET runtime, no installer, no admin rights.

    This builds the CONSOLE, which people run on their own machines. The unattended
    checker is not built or deployed by anything - it is carried inside its Agent job
    step, because nothing may be stored on the SQL Server hosts and the file shares
    belong to other teams. See sql\checker.ps1.

.EXAMPLE
    pwsh -File build.ps1
    pwsh -File build.ps1 -Output 'C:\Tools\ExtractConsole'
#>

[CmdletBinding()]
param(
    [string]$Output = "$PSScriptRoot\dist",
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

$proj = "$PSScriptRoot\src\ExtractConsole\ExtractConsole.csproj"

Write-Host ''
Write-Host 'Publishing ExtractConsole...' -ForegroundColor Cyan

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }

dotnet publish $proj -c Release -o $Output --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# The publish folder carries debug symbols and the config template; keep the config,
# drop the symbols so what you hand over is just the exe and its settings.
Get-ChildItem $Output -Filter '*.pdb' | Remove-Item -Force -ErrorAction SilentlyContinue

$exe = Join-Path $Output 'ExtractConsole.exe'
if (-not (Test-Path $exe)) { throw "Expected $exe to exist." }

$sizeMb = [Math]::Round((Get-Item $exe).Length / 1MB, 1)

Write-Host ''
Write-Host "  $exe  (${sizeMb} MB)" -ForegroundColor Green
Write-Host "  $(Join-Path $Output 'appsettings.json')" -ForegroundColor Green
Write-Host ''
Write-Host '  Copy both files anywhere and double-click the exe.' -ForegroundColor DarkGray
Write-Host ''

Write-Host '  The unattended checker is not part of this build - it lives in its' -ForegroundColor DarkGray
Write-Host '  Agent job step. Edit sql\checker.ps1 and run sql\Build-CheckerJobScript.ps1.' -ForegroundColor DarkGray
Write-Host ''

if (-not $SkipTests) {
    Write-Host 'Run the integration suite with:' -ForegroundColor DarkGray
    Write-Host '  pwsh -File tests\Invoke-IntegrationTest.ps1' -ForegroundColor DarkGray
    Write-Host ''
}
