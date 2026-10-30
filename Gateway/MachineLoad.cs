using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SessionDeck.Gateway;

/// <summary>How busy the deck's machine is right now, for the phone's list header. Percentages are 0 to 100; a value below zero means unknown.</summary>
internal sealed record MachineLoad(int CpuPercent, int MemoryPercent, double MemoryUsedGb, double MemoryTotalGb, int GpuPercent, DateTime SampledAtUtc);

/// <summary>
/// CPU from the kernel's own idle/busy clocks between two samples, memory from the global status,
/// GPU from the "GPU Engine" performance counters Windows keeps per 3D engine (summed across the
/// adapter's processes; absent on machines without a WDDM 2 driver, which reads as unknown).
/// </summary>
internal sealed class MachineLoadSampler
{
    long _idle, _kernel, _user;
    bool _primed;
    PerformanceCounter[]? _gpu;
    DateTime _gpuInstancesAt = DateTime.MinValue;
    bool _gpuUnavailable;

    public MachineLoad Sample()
    {
        int cpu = SampleCpu();
        var (usedGb, totalGb, memPct) = SampleMemory();
        int gpu = SampleGpu();
        return new MachineLoad(cpu, memPct, usedGb, totalGb, gpu, DateTime.UtcNow);
    }

    int SampleCpu()
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt)) return -1;
        long idle = ToLong(idleFt), kernel = ToLong(kernelFt), user = ToLong(userFt);
        int result = -1;
        if (_primed)
        {
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

    int SampleGpu()
    {
        if (_gpuUnavailable) return -1;
        try
        {
            // Engines come and go with processes; the instance list is rebuilt every 30 s.
            if (_gpu == null || DateTime.UtcNow - _gpuInstancesAt > TimeSpan.FromSeconds(30))
            {
                foreach (var old in _gpu ?? Array.Empty<PerformanceCounter>()) old.Dispose();
                if (!PerformanceCounterCategory.Exists("GPU Engine")) { _gpuUnavailable = true; return -1; }
                var category = new PerformanceCounterCategory("GPU Engine");
                _gpu = category.GetInstanceNames()
                    .Where(n => n.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    .Select(n => new PerformanceCounter("GPU Engine", "Utilization Percentage", n, readOnly: true))
                    .ToArray();
                foreach (var c in _gpu) c.NextValue();
                _gpuInstancesAt = DateTime.UtcNow;
                return -1;
            }
            double sum = 0;
            foreach (var c in _gpu)
            {
                try { sum += c.NextValue(); } catch (InvalidOperationException) { }
            }
            return (int)Math.Clamp(Math.Round(sum), 0, 100);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            _gpuUnavailable = true;
            return -1;
        }
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
