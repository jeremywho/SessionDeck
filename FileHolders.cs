using System.Runtime.InteropServices;

namespace ClaudeSessionMonitor;

/// <summary>
/// "Which process currently has this file open?", via the Restart Manager API.
///
/// Codex has no live-session registry — nothing like Claude's <c>~/.claude/sessions/&lt;pid&gt;.json</c>.
/// Worse, <c>codex resume</c> APPENDS to the original rollout file, so neither the filename's
/// timestamp nor the file's mtime tells you whether a session is live or which process owns it.
/// The one thing that is always true: a live Codex session holds its rollout .jsonl open. This
/// turns that into a PID.
///
/// Restart Manager (rstrtmgr.dll) is the documented, non-admin way to ask that question — it's what
/// installers use to find "close these apps first". We only ever *ask*; we never restart anything.
/// Cost is ~50-60ms per file, which is why <see cref="CodexScanner"/> calls this from a background
/// thread with a probe budget, never from the UI scan.
/// </summary>
internal static class FileHolders
{
    [StructLayout(LayoutKind.Sequential)]
    struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, IntPtr rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[] rgAffectedApps, ref uint lpdwRebootReasons);

    const int MaxProcs = 12;

    /// <summary>
    /// PID of a process named <paramref name="processName"/>* that holds <paramref name="path"/> open,
    /// or 0 for "nobody" / "couldn't tell". Never throws.
    /// </summary>
    public static int OwnerPid(string path, string processName)
    {
        uint handle = 0;
        try
        {
            if (RmStartSession(out handle, 0, Guid.NewGuid().ToString()) != 0) return 0;
            if (RmRegisterResources(handle, 1, new[] { path }, 0, IntPtr.Zero, 0, null) != 0) return 0;

            uint count = MaxProcs, reasons = 0;
            var procs = new RM_PROCESS_INFO[MaxProcs];
            if (RmGetList(handle, out _, ref count, procs, ref reasons) != 0) return 0;

            for (int i = 0; i < count && i < procs.Length; i++)
            {
                // strAppName is the process's exe name ("codex.exe"). Matching it here is what keeps a
                // stray indexer or backup agent with the file open from being mistaken for the session.
                if (procs[i].strAppName.StartsWith(processName, StringComparison.OrdinalIgnoreCase))
                    return procs[i].Process.dwProcessId;
            }
            return 0;
        }
        catch { return 0; }
        finally { if (handle != 0) try { RmEndSession(handle); } catch { } }
    }
}
