using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeSessionMonitor;

/// <summary>
/// Reads and writes the console title of ANOTHER process, by attaching to its console.
///
/// This is what makes double-click-to-focus deterministic. Windows Terminal shows a tab's title, and
/// its accessible name is the only thing UIA gives us to identify a tab — but nothing maps a tab back
/// to the process running in it. We used to guess the title from session metadata (name, id, …), which
/// works right up until the session changes its own title: a long-running Codex session had its thread
/// UUID replaced by the shell's cwd (`Jeremy`), and then nothing matched.
///
/// Attaching to the session's console and asking gives the title it ACTUALLY has, which is the same
/// string UIA reports for its tab. <see cref="Write"/> covers the remaining case — two sessions whose
/// titles are identical — by briefly stamping a unique marker on one of them.
///
/// Nothing here writes to the console it borrows, and every call detaches before returning. The app is
/// normally a GUI process with no console, so this is invisible — but note the headless `--list` /
/// `--windows` modes DO own a console, and detaching is one-way: those modes must keep writing their
/// output to a file, never to stdout, or it would go nowhere after the first call.
/// </summary>
internal static class ConsoleTitle
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint dwProcessId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int GetConsoleTitleW(StringBuilder lpConsoleTitle, int nSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetConsoleTitleW(string lpConsoleTitle);

    /// <summary>Serialises the attach/detach window — console attachment is per-PROCESS, so two of
    /// these overlapping would have them stealing each other's console.</summary>
    static readonly object _gate = new();

    /// <summary>The title the session's terminal currently has, or null if it can't be read.</summary>
    public static string? Read(int pid)
    {
        lock (_gate)
        {
            if (!Attach(pid)) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int n = GetConsoleTitleW(sb, sb.Capacity);
                return n > 0 ? sb.ToString() : null;
            }
            catch { return null; }
            finally { FreeConsole(); }
        }
    }

    /// <summary>Set the session's terminal title. Used only to disambiguate identical titles.</summary>
    public static bool Write(int pid, string title)
    {
        lock (_gate)
        {
            if (!Attach(pid)) return false;
            try { return SetConsoleTitleW(title); }
            catch { return false; }
            finally { FreeConsole(); }
        }
    }

    static bool Attach(int pid)
    {
        try
        {
            FreeConsole();          // no-op for this GUI process; required if one were ever attached
            return AttachConsole((uint)pid);
        }
        catch { return false; }
    }
}
