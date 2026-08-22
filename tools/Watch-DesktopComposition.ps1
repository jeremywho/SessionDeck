[CmdletBinding()]
param(
    [string] $OutputPath = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'dwm-watch-live.csv'),
    [ValidateRange(5, 3600)]
    [int] $IntervalSeconds = 30,
    [int] $MonitorPid = 0
)

$ErrorActionPreference = 'Stop'
$states = @{}
$lastSample = [DateTime]::UtcNow

function Measure-Group {
    param(
        [string] $Key,
        [object[]] $Processes,
        [double] $ElapsedSeconds
    )

    $cpu = [double](($Processes | Measure-Object CPU -Sum).Sum)
    $corePct = 0.0
    if ($states.ContainsKey($Key) -and $ElapsedSeconds -gt 0) {
        $corePct = [Math]::Max(0.0, ($cpu - [double]$states[$Key]) * 100.0 / $ElapsedSeconds)
    }
    $states[$Key] = $cpu

    [pscustomobject]@{
        Count = $Processes.Count
        Threads = [int](($Processes | ForEach-Object { $_.Threads.Count } | Measure-Object -Sum).Sum)
        Handles = [int](($Processes | Measure-Object HandleCount -Sum).Sum)
        WorkingMB = [Math]::Round([double](($Processes | Measure-Object WorkingSet64 -Sum).Sum) / 1MB, 1)
        PrivateMB = [Math]::Round([double](($Processes | Measure-Object PrivateMemorySize64 -Sum).Sum) / 1MB, 1)
        CorePct = [Math]::Round($corePct, 1)
    }
}

while ($true) {
    try {
        $counterSamples = @(Get-Counter -Counter @(
            '\Processor(_Total)\% Processor Time',
            '\Process(dwm)\% Processor Time',
            '\Process(WindowsTerminal)\% Processor Time'
        ) -SampleInterval 1 -MaxSamples 1 | Select-Object -ExpandProperty CounterSamples)
        $systemCpu = [Math]::Round([double]($counterSamples | Where-Object Path -Like '*\processor(_total)\% processor time' | Select-Object -ExpandProperty CookedValue -First 1), 1)
        $dwmCounter = [Math]::Round([double]($counterSamples | Where-Object Path -Like '*\process(dwm)\% processor time' | Select-Object -ExpandProperty CookedValue -First 1), 1)
        $terminalCounter = [Math]::Round([double]($counterSamples | Where-Object Path -Like '*\process(windowsterminal)\% processor time' | Select-Object -ExpandProperty CookedValue -First 1), 1)
        $now = [DateTime]::UtcNow
        $elapsed = ($now - $lastSample).TotalSeconds
        $lastSample = $now
        $all = @(Get-Process -ErrorAction SilentlyContinue)

        $dwmProcesses = @($all | Where-Object ProcessName -eq 'dwm')
        $explorerProcesses = @($all | Where-Object ProcessName -eq 'explorer')
        $terminalProcesses = @($all | Where-Object ProcessName -eq 'WindowsTerminal')
        $openConsoleProcesses = @($all | Where-Object ProcessName -eq 'OpenConsole')
        $claudeProcesses = @($all | Where-Object ProcessName -Like 'claude*')
        $codexProcesses = @($all | Where-Object ProcessName -Like 'codex*')
        $compilerProcesses = @($all | Where-Object ProcessName -eq 'VBCSCompiler')
        $monitorProcesses = if ($MonitorPid -gt 0) { @($all | Where-Object Id -eq $MonitorPid) } else { @() }

        $dwm = Measure-Group 'dwm' $dwmProcesses $elapsed
        $explorer = Measure-Group 'explorer' $explorerProcesses $elapsed
        $terminal = Measure-Group 'terminal' $terminalProcesses $elapsed
        $claude = Measure-Group 'claude' $claudeProcesses $elapsed
        $codex = Measure-Group 'codex' $codexProcesses $elapsed
        $compiler = Measure-Group 'compiler' $compilerProcesses $elapsed
        $monitor = Measure-Group 'monitor' $monitorProcesses $elapsed

        $row = [pscustomobject][ordered]@{
            Timestamp = [DateTimeOffset]::Now.ToString('O')
            UptimeMinutes = [Math]::Round([Environment]::TickCount64 / 60000.0, 1)
            SystemCpuPct = $systemCpu
            DwmCorePct = $dwmCounter
            DwmPid = if ($dwmProcesses.Count) { $dwmProcesses[0].Id } else { 0 }
            DwmThreads = $dwm.Threads
            DwmHandles = $dwm.Handles
            DwmWorkingMB = $dwm.WorkingMB
            DwmPrivateMB = $dwm.PrivateMB
            ExplorerThreads = $explorer.Threads
            ExplorerHandles = $explorer.Handles
            ExplorerWorkingMB = $explorer.WorkingMB
            ExplorerPrivateMB = $explorer.PrivateMB
            TerminalCount = $terminal.Count
            TerminalThreads = $terminal.Threads
            TerminalHandles = $terminal.Handles
            TerminalWorkingMB = $terminal.WorkingMB
            TerminalPrivateMB = $terminal.PrivateMB
            TerminalCorePct = $terminalCounter
            OpenConsoleCount = $openConsoleProcesses.Count
            ClaudeCount = $claude.Count
            ClaudePrivateMB = $claude.PrivateMB
            CodexCount = $codex.Count
            CodexPrivateMB = $codex.PrivateMB
            VBCSCompilerCount = $compiler.Count
            VBCSPrivateMB = $compiler.PrivateMB
            MonitorPid = $MonitorPid
            MonitorThreads = $monitor.Threads
            MonitorHandles = $monitor.Handles
            MonitorWorkingMB = $monitor.WorkingMB
            MonitorPrivateMB = $monitor.PrivateMB
            MonitorCorePct = $monitor.CorePct
        }

        if ((Test-Path -LiteralPath $OutputPath) -and (Get-Item -LiteralPath $OutputPath).Length -gt 0) {
            $row | Export-Csv -LiteralPath $OutputPath -NoTypeInformation -Append
        } else {
            $row | Export-Csv -LiteralPath $OutputPath -NoTypeInformation
        }
    } catch {
        $errorPath = [IO.Path]::ChangeExtension($OutputPath, '.errors.log')
        "$(Get-Date -Format O) $($_.Exception.Message)" | Add-Content -LiteralPath $errorPath
    }

    if ($MonitorPid -gt 0 -and -not (Get-Process -Id $MonitorPid -ErrorAction SilentlyContinue)) {
        break
    }

    Start-Sleep -Seconds $IntervalSeconds
}
