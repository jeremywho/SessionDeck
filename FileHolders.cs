using System.Runtime.InteropServices;

namespace SessionDeck;

internal enum FileOwnerState
{
    Owned,
    Unowned,
    Indeterminate,
}

/// <summary>A Restart Manager ownership query without conflating "unowned" with "query failed".</summary>
internal readonly record struct FileOwnerResult(
    FileOwnerState State,
    int Pid = 0,
    int ErrorCode = 0,
    string ErrorStage = "")
{
    public static FileOwnerResult Owned(int pid) => new(FileOwnerState.Owned, pid);
    public static FileOwnerResult Unowned() => new(FileOwnerState.Unowned);
    public static FileOwnerResult Indeterminate(string stage, int errorCode) =>
        new(FileOwnerState.Indeterminate, ErrorCode: errorCode, ErrorStage: stage);
}

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
    /// Whether a process named <paramref name="processName"/>* holds <paramref name="path"/> open.
    /// A successful query with no matching process is <see cref="FileOwnerState.Unowned"/>; an API
    /// or interop failure is <see cref="FileOwnerState.Indeterminate"/>. Never throws.
    /// </summary>
    public static FileOwnerResult QueryOwner(string path, string processName)
    {
        uint handle = 0;
        try
        {
            int error = RmStartSession(out handle, 0, Guid.NewGuid().ToString());
            if (error != 0) return FileOwnerResult.Indeterminate("start", error);

            error = RmRegisterResources(handle, 1, new[] { path }, 0, IntPtr.Zero, 0, null);
            if (error != 0) return FileOwnerResult.Indeterminate("register", error);

            uint count = MaxProcs, reasons = 0;
            var procs = new RM_PROCESS_INFO[MaxProcs];
            error = RmGetList(handle, out _, ref count, procs, ref reasons);
            if (error != 0) return FileOwnerResult.Indeterminate("list", error);

            for (int i = 0; i < count && i < procs.Length; i++)
            {
                // strAppName is the process's exe name ("codex.exe"). Matching it here is what keeps a
                // stray indexer or backup agent with the file open from being mistaken for the session.
                if (procs[i].strAppName.StartsWith(processName, StringComparison.OrdinalIgnoreCase))
                    return FileOwnerResult.Owned(procs[i].Process.dwProcessId);
            }
            return FileOwnerResult.Unowned();
        }
        catch (Exception ex)
        {
            return FileOwnerResult.Indeterminate("exception", System.Runtime.InteropServices.Marshal.GetHRForException(ex));
        }
        finally { if (handle != 0) try { RmEndSession(handle); } catch { } }
    }
}
