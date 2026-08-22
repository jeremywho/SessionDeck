[CmdletBinding()]
param(
    [ValidateSet('Normal', 'NoDesktopUia', 'FrozenGrid')]
    [string] $Mode = 'Normal'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo 'bin\AnimationFreeCpaV1\ClaudeSessionMonitor.exe'
$watchScript = Join-Path $PSScriptRoot 'Watch-DesktopComposition.ps1'

if (-not (Test-Path -LiteralPath $exe)) {
    throw "Missing experiment build: $exe"
}

$running = Get-Process -Name ClaudeSessionMonitor -ErrorAction SilentlyContinue
if ($running) {
    throw "Claude Session Monitor is already running (PID $($running.Id -join ', ')). Exit it from its tray menu first."
}

$start = [System.Diagnostics.ProcessStartInfo]::new($exe)
$start.WorkingDirectory = $repo
$start.UseShellExecute = $false
$start.Environment['CSM_NO_INSTALL'] = '1'
$start.Environment.Remove('CSM_EXPERIMENT_DISABLE_DESKTOP_UIA') | Out-Null
$start.Environment.Remove('CSM_EXPERIMENT_FREEZE_GRID') | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logDir = Join-Path $env:TEMP 'ClaudeSessionMonitor-Isolation'
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$logPath = Join-Path $logDir "$stamp-$($Mode.ToLowerInvariant()).tsv"
$healthPath = Join-Path $logDir "$stamp-$($Mode.ToLowerInvariant())-health.csv"
$start.Environment['CSM_EXPERIMENT_LOG'] = $logPath

switch ($Mode) {
    'NoDesktopUia' { $start.Environment['CSM_EXPERIMENT_DISABLE_DESKTOP_UIA'] = '1' }
    'FrozenGrid' { $start.Environment['CSM_EXPERIMENT_FREEZE_GRID'] = '1' }
}

Write-Host "Starting instrumented Claude Session Monitor: $Mode"
Write-Host "Telemetry: $logPath"
Write-Host "Health telemetry: $healthPath"
Write-Host 'Normal: production behavior. NoDesktopUia: live grid, no recurring Terminal accessibility sweep.'
Write-Host 'FrozenGrid: recurring scanners and Terminal accessibility sweep remain active; grid rows stop changing after initial population.'
Write-Host 'Exit from the tray menu when the observation is complete.'

$process = [System.Diagnostics.Process]::Start($start)
$watcher = $null
try {
    $watchStart = [System.Diagnostics.ProcessStartInfo]::new()
    $watchStart.FileName = (Get-Process -Id $PID).Path
    $watchStart.UseShellExecute = $false
    $watchStart.CreateNoWindow = $true
    $watchStart.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $watchStart.ArgumentList.Add('-NoProfile')
    $watchStart.ArgumentList.Add('-ExecutionPolicy')
    $watchStart.ArgumentList.Add('Bypass')
    $watchStart.ArgumentList.Add('-File')
    $watchStart.ArgumentList.Add($watchScript)
    $watchStart.ArgumentList.Add('-OutputPath')
    $watchStart.ArgumentList.Add($healthPath)
    $watchStart.ArgumentList.Add('-IntervalSeconds')
    $watchStart.ArgumentList.Add('60')
    $watchStart.ArgumentList.Add('-MonitorPid')
    $watchStart.ArgumentList.Add($process.Id.ToString())
    $watcher = [System.Diagnostics.Process]::Start($watchStart)

    $watchDeadline = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $watchDeadline -and -not (Test-Path -LiteralPath $healthPath) -and -not $watcher.HasExited) {
        Start-Sleep -Milliseconds 250
    }
    if (Test-Path -LiteralPath $healthPath) {
        Write-Host "Health watcher active (PID $($watcher.Id))."
    } else {
        Write-Warning "Health watcher did not produce its first sample; inspect $([IO.Path]::ChangeExtension($healthPath, '.errors.log'))."
    }

    $process.WaitForExit()
} finally {
    if ($watcher -and -not $watcher.HasExited) {
        $watcher.Kill()
        $watcher.WaitForExit()
    }
}
Write-Host "Experiment exited with code $($process.ExitCode)."
