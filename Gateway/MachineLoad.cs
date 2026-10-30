using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SessionDeck.Gateway;

/// <summary>How busy the deck's machine is right now, for the phone's list header. Percentages are 0 to 100; a value below zero means unknown.</summary>
internal sealed record MachineLoad(int CpuPercent, int MemoryPercent, double MemoryUsedGb, double MemoryTotalGb, int GpuPercent, DateTime SampledAtUtc);

/// <summary>
/// Samples on a thread of its own every few seconds and keeps the latest. CPU and memory are cheap
/// and published at once; the GPU counters take tens of seconds to open the first time, so they are
/// read after them on the same thread and join the sample when they are ready.
/// </summary>
internal sealed class MachineLoadMonitor
{
    readonly MachineLoadSampler _sampler = new();
    readonly TimeSpan _every;
    readonly Func<int> _gpu;
    volatile MachineLoad? _latest;

    public MachineLoadMonitor(TimeSpan? every = null, Func<int>? gpu = null)
    {
        _every = every ?? TimeSpan.FromSeconds(5);
        _gpu = gpu ?? _sampler.SampleGpu;
        new Thread(Run) { IsBackground = true, Name = "machine-load" }.Start();
    }

    public MachineLoad? Latest => _latest;

    /// <summary>What the gateway compares to decide whether a frame is worth pushing: whole percentages only.</summary>
    public string Signature => _latest is { } l ? $"{l.CpuPercent}/{l.MemoryPercent}/{l.GpuPercent}" : "";

    void Run()
    {
        int gpu = -1;
        while (true)
        {
            try
            {
                _latest = _sampler.SampleCpuAndMemory(gpu);
                gpu = _gpu();
                _latest = _latest with { GpuPercent = gpu };
            }
            catch (Exception ex) { App.LogError(ex); }
            Thread.Sleep(_every);
        }
    }
}

/// <summary>
/// CPU from the kernel's own idle/busy clocks between two samples, memory from the global status,
/// GPU from the "GPU Engine" performance counters Windows keeps per process and engine: one read of
/// the whole category, 3D engines summed, utilisation from the raw busy-time delta between reads
/// (absent on machines without a WDDM 2 driver, which reads as unknown).
/// </summary>
internal sealed class MachineLoadSampler
{
    long _idle, _kernel, _user;
    bool _primed;
    PerformanceCounterCategory? _gpuCategory;
    Dictionary<string, (long Raw, long Stamp)>? _gpuPrevious;
    bool _gpuUnavailable;

    public MachineLoad Sample()
    {
        var basic = SampleCpuAndMemory(-1);
        return basic with { GpuPercent = SampleGpu() };
    }

    public MachineLoad SampleCpuAndMemory(int gpuPercent)
    {
        int cpu = SampleCpu();
        var (usedGb, totalGb, memPct) = SampleMemory();
        return new MachineLoad(cpu, memPct, usedGb, totalGb, gpuPercent, DateTime.UtcNow);
    }

    int SampleCpu()
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt)) return -1;
        long idle = ToLong(idleFt), kernel = ToLong(kernelFt), user = ToLong(userFt);
        int result = -1;
        if (_primed)
        {
            // Kernel time includes idle time.
            long total = (kernel - _kernel) + (user - _user);
            long busy = total - (idle - _idle);
            if (total > 0) result = (int)Math.Clamp(Math.Round(busy * 100.0 / total), 0, 100);
        }
        _idle = idle; _kernel = kernel; _user = user; _primed = true;
        return result;
    }

    static (double UsedGb, double TotalGb, int Percent) SampleMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0) return (0, 0, -1);
        double total = status.ullTotalPhys / 1073741824.0;
        double used = (status.ullTotalPhys - status.ullAvailPhys) / 1073741824.0;
        return (Math.Round(used, 1), Math.Round(total, 1), (int)status.dwMemoryLoad);
    }

    /// <summary>Summed 3D-engine utilisation since the previous call; -1 until two reads exist or when the counters are not there.</summary>
    public int SampleGpu()
    {
        if (_gpuUnavailable) return -1;
        try
        {
            _gpuCategory ??= new PerformanceCounterCategory("GPU Engine");
            var data = _gpuCategory.ReadCategory();
            if (!data.Contains("Utilization Percentage")) { _gpuUnavailable = true; return -1; }
            var column = data["Utilization Percentage"]!;
            var current = new Dictionary<string, (long Raw, long Stamp)>(StringComparer.OrdinalIgnoreCase);
            double sum = 0;
            bool any = false;
            foreach (string name in column.Keys)
            {
                if (!name.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase)) continue;
                var d = column[name]!;
                current[name] = (d.RawValue, d.Sample.TimeStamp100nSec);
                if (_gpuPrevious != null && _gpuPrevious.TryGetValue(name, out var prev) && d.Sample.TimeStamp100nSec > prev.Stamp)
                {
                    sum += 100.0 * (d.RawValue - prev.Raw) / (d.Sample.TimeStamp100nSec - prev.Stamp);
                    any = true;
                }
            }
            _gpuPrevious = current;
            return any ? (int)Math.Clamp(Math.Round(sum), 0, 100) : -1;
        }
        catch (InvalidOperationException) { _gpuUnavailable = true; return -1; }
        catch (Exception ex) { App.LogError(ex); _gpuUnavailable = true; return -1; }
    }

    static long ToLong(FILETIME ft) => ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
