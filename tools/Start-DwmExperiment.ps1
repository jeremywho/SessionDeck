[CmdletBinding()]
param(
    [ValidateSet('NoBackdrop', 'BackdropOnly', 'Full')]
    [string] $Mode = 'NoBackdrop'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo 'bin\DwmDiagnostic\SessionDeck.exe'

if (-not (Test-Path -LiteralPath $exe)) {
    throw "Publish the diagnostic binary first with the .NET 10.0.11 command in docs\dwm-diagnostics.md"
}

$running = Get-Process -Name SessionDeck -ErrorAction SilentlyContinue
if ($running) {
    throw "Claude Session Monitor is already running (PID $($running.Id -join ', ')). Exit it from its tray menu before starting an isolated experiment."
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logDir = Join-Path $env:TEMP 'SessionDeck-Dwm'
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$logPath = Join-Path $logDir "$stamp-$($Mode.ToLowerInvariant()).tsv"

$start = [System.Diagnostics.ProcessStartInfo]::new($exe)
$start.WorkingDirectory = $repo
$start.UseShellExecute = $false
$start.Environment['SD_NO_INSTALL'] = '1'
$start.Environment['SD_DWM_DIAGNOSTICS'] = '1'
$start.Environment['SD_DWM_GUARD'] = '1'
$start.Environment['SD_DWM_DIAGNOSTIC_PATH'] = $logPath

switch ($Mode) {
    'NoBackdrop' {
        $start.Environment['SD_DISABLE_BACKDROP'] = '1'
        $start.Environment.Remove('SD_DISABLE_BACKGROUND_WORK') | Out-Null
    }
    'BackdropOnly' {
        $start.Environment.Remove('SD_DISABLE_BACKDROP') | Out-Null
        $start.Environment['SD_DISABLE_BACKGROUND_WORK'] = '1'
    }
    'Full' {
        $start.Environment.Remove('SD_DISABLE_BACKDROP') | Out-Null
        $start.Environment.Remove('SD_DISABLE_BACKGROUND_WORK') | Out-Null
    }
}

Write-Host "Starting Claude Session Monitor DWM experiment: $Mode"
Write-Host "Telemetry: $logPath"
Write-Host 'The memory guard exits the test app if DWM resident working set breaches the healthy baseline by >=200 MB twice.'
Write-Warning 'DWM denies the diagnostic app direct CPU-time access on this machine. Use the external counter/logger for CPU; the in-app CPU guard is not currently active.'
Write-Host 'Exit normally from the monitor tray menu when the observation period is complete.'

$process = [System.Diagnostics.Process]::Start($start)
$process.WaitForExit()
Write-Host "Experiment exited with code $($process.ExitCode)."
if ($process.ExitCode -eq 86) {
    Write-Warning "The DWM fail-safe fired. Do not start another mode until the telemetry has been reviewed: $logPath"
}
