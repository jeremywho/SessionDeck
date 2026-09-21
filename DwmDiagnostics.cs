using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace SessionDeck;

/// <summary>
/// Opt-in switches for separating the monitor's compositor surface from its recurring scanners.
/// These are environment variables rather than persisted settings so a diagnostic launch cannot
/// silently change the user's normal configuration.
/// </summary>
internal static class DwmDiagnosticOptions
{
    internal const string EnabledVariable = "SD_DWM_DIAGNOSTICS";
    internal const string GuardVariable = "SD_DWM_GUARD";
    internal const string DisableBackdropVariable = "SD_DISABLE_BACKDROP";
    internal const string DisableBackgroundWorkVariable = "SD_DISABLE_BACKGROUND_WORK";
    internal const string OutputVariable = "SD_DWM_DIAGNOSTIC_PATH";

    internal static bool Enabled => IsEnabled(Environment.GetEnvironmentVariable(EnabledVariable));
    internal static bool GuardEnabled => Enabled && IsEnabled(Environment.GetEnvironmentVariable(GuardVariable));
    internal static bool DisableBackdrop => IsEnabled(Environment.GetEnvironmentVariable(DisableBackdropVariable));
    internal static bool DisableBackgroundWork => IsEnabled(Environment.GetEnvironmentVariable(DisableBackgroundWorkVariable));

    internal static bool IsEnabled(string? value) =>
        value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                              value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                              value.Equals("on", StringComparison.OrdinalIgnoreCase));

    internal static string Mode => (DisableBackdrop, DisableBackgroundWork) switch
    {
        (true, false) => "no-backdrop",
        (false, true) => "backdrop-only",
        (true, true) => "no-backdrop-no-background",
        _ => "full",
    };
}

/// <summary>
/// One-second process telemetry plus event markers from the WPF refresh path. The guard is deliberately
/// based on the measured failure signature, not private bytes or handle count: the bad session held DWM
/// near one full logical core with a ~676 MB resident working set, while healthy private allocations and
/// handles have both exceeded their bad-session values.
/// </summary>
internal static class DwmDiagnostics
{
    internal const double GuardCoreAveragePercent = 60;
    internal const int GuardCoreSamples = 5;
    internal const double GuardWorkingSetAbsoluteMb = 350;
    internal const double GuardWorkingSetGrowthMb = 200;
    internal const int GuardWorkingSetSamples = 2;

    static readonly object Gate = new();
    static readonly Queue<double> RecentDwmCore = new();
    static StreamWriter? _writer;
    static System.Threading.Timer? _timer;
    static Process? _self;
    static Process? _dwm;
    static TimeSpan _lastSelfCpu;
    static TimeSpan _lastDwmCpu;
    static long _lastTimestamp;
    static double _baselineDwmWorkingMb;
    static int _workingSetBreaches;
    static int _guardTriggered;
    static Action? _onGuard;

    internal static string? FilePath { get; private set; }

    internal static void Start(Action onGuard)
    {
        if (!DwmDiagnosticOptions.Enabled) return;

        lock (Gate)
        {
            if (_timer != null) return;

            _onGuard = onGuard;
            _self = Process.GetCurrentProcess();
            _dwm = FindDwm(_self.SessionId);
            _self.Refresh();
            _dwm?.Refresh();
            _lastSelfCpu = SafeCpu(_self);
            _lastDwmCpu = SafeCpu(_dwm);
            _lastTimestamp = Stopwatch.GetTimestamp();
            _baselineDwmWorkingMb = BytesToMb(SafeWorkingSet(_dwm));

            string requested = Environment.GetEnvironmentVariable(DwmDiagnosticOptions.OutputVariable) ?? "";
            FilePath = requested.Length > 0
                ? Path.GetFullPath(requested)
                : Path.Combine(Path.GetTempPath(), $"sessiondeck-dwm-{DateTime.Now:yyyyMMdd-HHmmss}.tsv");
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            _writer = new StreamWriter(new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true,
            };
            _writer.WriteLine("timestamp\tkind\tdetail\tdwm_core_pct\tdwm_working_mb\tdwm_private_mb\tdwm_handles\tapp_core_pct\tapp_working_mb\tapp_private_mb\tapp_handles\tapp_threads\tapp_gdi\tapp_user");
            WriteEventLocked("start", $"mode={DwmDiagnosticOptions.Mode}; guard={DwmDiagnosticOptions.GuardEnabled}; pid={_self.Id}; dwmPid={_dwm?.Id}; baselineDwmWorkingMb={F(_baselineDwmWorkingMb)}");
            PerformanceLog.Write($"dwm-diagnostics started mode={DwmDiagnosticOptions.Mode} guard={DwmDiagnosticOptions.GuardEnabled} path={FilePath}");
            _timer = new System.Threading.Timer(Sample, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }
    }

    internal static void Mark(string kind, string detail = "")
    {
        if (!DwmDiagnosticOptions.Enabled) return;
        lock (Gate) WriteEventLocked(kind, detail);
    }

    internal static void Stop(string reason)
    {
        lock (Gate)
        {
            if (_timer == null && _writer == null) return;
            _timer?.Dispose();
            _timer = null;
            WriteEventLocked("stop", reason);
            _writer?.Dispose();
            _writer = null;
            _self?.Dispose();
            _self = null;
            _dwm?.Dispose();
            _dwm = null;
            RecentDwmCore.Clear();
            _workingSetBreaches = 0;
            _onGuard = null;
        }
    }

    static void Sample(object? _)
    {
        Action? guardAction = null;
        lock (Gate)
        {
            if (_writer == null || _self == null) return;
            try
            {
                // DWM is a protected process. Querying HasExited asks for SYNCHRONIZE access and fails
                // for a normal user even though CPU and memory counters remain readable. DWM changing
                // PID is rare and the outer safe property reads degrade to zero if it happens; never
                // make the entire sampler/guard depend on a protected-process handle.
                if (_dwm == null)
                {
                    _dwm = FindDwm(_self.SessionId);
                    _lastDwmCpu = SafeCpu(_dwm);
                }
                _self.Refresh();
                _dwm?.Refresh();

                long now = Stopwatch.GetTimestamp();
                double elapsedSeconds = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
                if (elapsedSeconds <= 0) return;

                TimeSpan selfCpu = SafeCpu(_self);
                TimeSpan dwmCpu = SafeCpu(_dwm);
                double selfCore = (selfCpu - _lastSelfCpu).TotalSeconds / elapsedSeconds * 100;
                double dwmCore = (dwmCpu - _lastDwmCpu).TotalSeconds / elapsedSeconds * 100;
                _lastSelfCpu = selfCpu;
                _lastDwmCpu = dwmCpu;
                _lastTimestamp = now;

                double dwmWorking = BytesToMb(SafeWorkingSet(_dwm));
                WriteSampleLocked(dwmCore, dwmWorking, selfCore);

                if (DwmDiagnosticOptions.GuardEnabled && Volatile.Read(ref _guardTriggered) == 0 &&
                    GuardShouldTrip(dwmCore, dwmWorking))
                {
                    Interlocked.Exchange(ref _guardTriggered, 1);
                    double average = RecentDwmCore.Count == 0 ? 0 : RecentDwmCore.Average();
                    string reason = $"dwm guard tripped: coreAverage={F(average)}%; workingMb={F(dwmWorking)}; baselineWorkingMb={F(_baselineDwmWorkingMb)}";
                    WriteEventLocked("guard-trigger", reason);
                    PerformanceLog.Write(reason);
                    guardAction = _onGuard;
                }
            }
            catch (Exception ex)
            {
                WriteEventLocked("sample-error", ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Never invoke application code while holding the file/telemetry lock.
        if (guardAction != null) Task.Run(guardAction);
    }

    static bool GuardShouldTrip(double dwmCore, double dwmWorkingMb)
    {
        RecentDwmCore.Enqueue(Math.Max(0, dwmCore));
        while (RecentDwmCore.Count > GuardCoreSamples) RecentDwmCore.Dequeue();

        double workingLimit = Math.Max(GuardWorkingSetAbsoluteMb, _baselineDwmWorkingMb + GuardWorkingSetGrowthMb);
        _workingSetBreaches = dwmWorkingMb >= workingLimit ? _workingSetBreaches + 1 : 0;

        bool sustainedCpu = RecentDwmCore.Count == GuardCoreSamples &&
                            RecentDwmCore.Average() >= GuardCoreAveragePercent;
        bool residentGrowth = _workingSetBreaches >= GuardWorkingSetSamples;
        return sustainedCpu || residentGrowth;
    }

    internal static bool GuardWouldTripForTests(IEnumerable<double> coreSamples, IEnumerable<double> workingSamples,
        double baselineWorkingMb)
    {
        var core = new Queue<double>();
        int workingBreaches = 0;
        double workingLimit = Math.Max(GuardWorkingSetAbsoluteMb, baselineWorkingMb + GuardWorkingSetGrowthMb);
        using var cores = coreSamples.GetEnumerator();
        using var working = workingSamples.GetEnumerator();
        while (cores.MoveNext() && working.MoveNext())
        {
            core.Enqueue(Math.Max(0, cores.Current));
            while (core.Count > GuardCoreSamples) core.Dequeue();
            workingBreaches = working.Current >= workingLimit ? workingBreaches + 1 : 0;
            if ((core.Count == GuardCoreSamples && core.Average() >= GuardCoreAveragePercent) ||
                workingBreaches >= GuardWorkingSetSamples)
                return true;
        }
        return false;
    }

    static Process? FindDwm(int sessionId)
    {
        foreach (Process process in Process.GetProcessesByName("dwm"))
        {
            try
            {
                if (process.SessionId == sessionId) return process;
            }
            catch { }
            process.Dispose();
        }
        return null;
    }

    static void WriteEventLocked(string kind, string detail)
    {
        _writer?.WriteLine($"{DateTime.Now:o}\t{Clean(kind)}\t{Clean(detail)}\t\t\t\t\t\t\t\t\t\t\t");
    }

    static void WriteSampleLocked(double dwmCore, double dwmWorking, double selfCore)
    {
        if (_writer == null || _self == null) return;
        uint gdi = Native.GetGuiResources(_self.Handle, 0);
        uint user = Native.GetGuiResources(_self.Handle, 1);
        _writer.WriteLine(string.Join('\t',
            DateTime.Now.ToString("o"), "sample", "",
            F(dwmCore), F(dwmWorking), F(BytesToMb(SafePrivate(_dwm))), SafeHandles(_dwm).ToString(CultureInfo.InvariantCulture),
            F(selfCore), F(BytesToMb(SafeWorkingSet(_self))), F(BytesToMb(SafePrivate(_self))),
            SafeHandles(_self).ToString(CultureInfo.InvariantCulture), SafeThreads(_self).ToString(CultureInfo.InvariantCulture),
            gdi.ToString(CultureInfo.InvariantCulture), user.ToString(CultureInfo.InvariantCulture)));
    }

    static string Clean(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    static string F(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
    static double BytesToMb(long bytes) => bytes / 1024d / 1024d;
    static TimeSpan SafeCpu(Process? p) { try { return p?.TotalProcessorTime ?? TimeSpan.Zero; } catch { return TimeSpan.Zero; } }
    static long SafeWorkingSet(Process? p) { try { return p?.WorkingSet64 ?? 0; } catch { return 0; } }
    static long SafePrivate(Process? p) { try { return p?.PrivateMemorySize64 ?? 0; } catch { return 0; } }
    static int SafeHandles(Process? p) { try { return p?.HandleCount ?? 0; } catch { return 0; } }
    static int SafeThreads(Process? p) { try { return p?.Threads.Count ?? 0; } catch { return 0; } }
}
