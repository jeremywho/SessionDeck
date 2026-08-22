# DWM diagnostic modes

The diagnostic build separates the monitor's WPF compositor surface from its recurring scanners. It is
opt-in: a normal launch creates no extra timer or telemetry file and retains the Mica backdrop.

Publish without launching. Pinning the runtime here makes the diagnostic executable self-contained on
the same .NET 10.0.11 servicing release as the installed app, even if the workstation runtime is older:

```powershell
dotnet publish ClaudeSessionMonitor.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:RuntimeFrameworkVersion=10.0.11 `
  -o bin\DwmDiagnosticLowFps
```

Exit any installed Claude Session Monitor instance from its tray menu. Then run one mode at a time:

```powershell
.\tools\Start-DwmExperiment.ps1 NoBackdrop
.\tools\Start-DwmExperiment.ps1 NoBackdropLowFps
.\tools\Start-DwmExperiment.ps1 NoBackdropNoAnimations
.\tools\Start-DwmExperiment.ps1 NoAnimations
.\tools\Start-DwmExperiment.ps1 BackdropOnly
.\tools\Start-DwmExperiment.ps1 Full
```

Modes:

- `NoBackdrop`: all normal scanning and UI updates, but the main FluentWindow uses no Mica. This is the
  safest first test and checks whether the monitor remains useful without the compositor feature.
- `NoBackdropLowFps`: the same no-Mica test with cached status glyphs capped at 10 FPS. Compare this
  against `NoBackdropNoAnimations` and the earlier uncapped run to measure the cost of useful motion.
- `NoBackdropNoAnimations`: the same no-Mica test with the per-row spinner and pulse clocks stopped.
  Comparing it with `NoBackdrop` isolates continuous WPF animation from the mere presence of the window.
- `NoAnimations`: normal Mica and background work, but static status glyphs. Run this only after the
  no-backdrop comparison is healthy; it checks whether Mica remains safe without continuous transforms.
- `BackdropOnly`: the same populated window and Mica surface, but only one initial discovery pass; all
  recurring session, Codex, virtual-desktop, usage, and credential work stays off. This isolates the
  compositor surface from background work.
- `Full`: normal Mica and normal background work, with telemetry and the guard enabled.

Each run writes a one-second TSV under `%TEMP%\ClaudeSessionMonitor-Dwm`. Event rows correlate startup,
window visibility, session scans, UI application, Codex probes, and virtual-desktop UIA sweeps with DWM
and app CPU, resident/private memory, handle/thread counts, and app GDI/USER objects.

The fail-safe exits only the diagnostic app (exit code 86) if either measured bad-state signature appears
on a machine where DWM exposes both counters:

- DWM averages at least 60% of one logical core over five samples, or
- DWM resident working set exceeds both 350 MB and the run's baseline by at least 200 MB for two samples.

On this workstation DWM allows the app to read resident memory but denies its direct process CPU-time
query, so the TSV CPU column is zero and only the resident-memory branch is active. The external
Performance Counter logger remains the authoritative CPU measurement until the sampler uses PDH.

Private bytes and raw handle counts are deliberately not guard signals: both exceeded their earlier
bad-session values while the desktop remained smooth. The guard does not restart or kill DWM.
