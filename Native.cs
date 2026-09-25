using System.Runtime.InteropServices;
using System.Text;

namespace SessionDeck;

internal static class Native
{
    [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLengthW(IntPtr hWnd);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

    public const int SW_RESTORE = 9;

    /// <summary>
    /// All visible, titled top-level windows grouped by owning process id. Needed because a single
    /// process (e.g. Windows Terminal) can own many windows, and Process.MainWindowHandle returns
    /// only one of them.
    /// </summary>
    public static Dictionary<int, List<(IntPtr Hwnd, string Title)>> TopWindowsByPid()
    {
        var map = new Dictionary<int, List<(IntPtr, string)>>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            int len = GetWindowTextLengthW(h);
            if (len <= 0) return true;                       // skip untitled helper windows
            var sb = new StringBuilder(len + 1);
            GetWindowTextW(h, sb, sb.Capacity);
            GetWindowThreadProcessId(h, out uint pid);
            int key = (int)pid;
            if (!map.TryGetValue(key, out var list)) { list = new List<(IntPtr, string)>(); map[key] = list; }
            list.Add((h, sb.ToString()));
            return true;
        }, IntPtr.Zero);
        return map;
    }

    // ----- Toolhelp snapshot: child-pid -> parent-pid map (pure P/Invoke, no WMI dependency) -----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    const uint TH32CS_SNAPPROCESS = 0x00000002;

    public static Dictionary<int, int> BuildParentMap()
    {
        var map = new Dictionary<int, int>();
        foreach (var kv in BuildProcessTable()) map[kv.Key] = kv.Value.Parent;
        return map;
    }

    /// <summary>Every process: its parent and executable name, from one snapshot.</summary>
    public static Dictionary<int, (int Parent, string Name)> BuildProcessTable()
    {
        var map = new Dictionary<int, (int, string)>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var pe = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snap, ref pe))
                do { map[(int)pe.th32ProcessID] = ((int)pe.th32ParentProcessID, pe.szExeFile ?? ""); }
                while (Process32NextW(snap, ref pe));
        }
        finally { CloseHandle(snap); }
        return map;
    }

    static readonly HashSet<string> ShellNames = new(StringComparer.OrdinalIgnoreCase)
        { "bash.exe", "sh.exe", "pwsh.exe", "powershell.exe", "cmd.exe" };

    /// <summary>
    /// Shells running under <paramref name="pid"/>, counted once per chain: a Claude background
    /// shell is bash → bash → conhost, and only the top of that chain counts. This is how Claude's
    /// own "N shells" is known from outside: this Claude keeps their output in memory, not in files.
    /// </summary>
    public static int TopLevelShells(int pid, IReadOnlyDictionary<int, (int Parent, string Name)> table)
    {
        var children = new Dictionary<int, List<int>>();
        foreach (var kv in table)
        {
            if (!children.TryGetValue(kv.Value.Parent, out var list)) children[kv.Value.Parent] = list = new List<int>();
            list.Add(kv.Key);
        }
        int count = 0;
        var stack = new Stack<(int Pid, bool UnderShell)>();
        stack.Push((pid, false));
        var seen = new HashSet<int> { pid };
        while (stack.Count > 0)
        {
            var (cur, underShell) = stack.Pop();
            if (!children.TryGetValue(cur, out var kids)) continue;
            foreach (int kid in kids)
            {
                if (!seen.Add(kid)) continue;
                bool isShell = table.TryGetValue(kid, out var info) && ShellNames.Contains(info.Name);
                if (isShell && !underShell) count++;
                stack.Push((kid, underShell || isShell));
            }
        }
        return count;
    }
}
