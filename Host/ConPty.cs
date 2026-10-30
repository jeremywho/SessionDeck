using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SessionDeck.Host;

/// <summary>
/// One pseudoconsole and the child process attached to it. The child is placed in a job object
/// marked kill-on-close, so if this process dies for any reason the child (and everything it
/// started) dies with it rather than lingering unreachable.
/// </summary>
internal sealed class ConPty : IDisposable
{
    const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    const uint CREATE_SUSPENDED = 0x00000004;
    const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    const uint INFINITE = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFOW
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFOEXW
    {
        public STARTUPINFOW StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEXW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    IntPtr _pc;
    IntPtr _job;
    IntPtr _process;
    readonly SafeFileHandle _ptyIn;    // we write here -> child's stdin
    readonly SafeFileHandle _ptyOut;   // we read here <- child's stdout

    public int Pid { get; }
    public FileStream Input { get; }
    public FileStream Output { get; }
    public short Cols { get; private set; }
    public short Rows { get; private set; }

    public ConPty(string commandLine, string cwd, short cols, short rows)
    {
        Cols = Math.Max(cols, (short)2);
        Rows = Math.Max(rows, (short)2);
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>() };
        if (!CreatePipe(out var inRead, out _ptyIn, ref sa, 0)) throw new Win32Exception();
        if (!CreatePipe(out _ptyOut, out var outWrite, ref sa, 0)) throw new Win32Exception();

        int hr = CreatePseudoConsole(new COORD { X = cols, Y = rows }, inRead, outWrite, 0, out _pc);
        if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole failed");
        inRead.Dispose();
        outWrite.Dispose();

        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        IntPtr attrs = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(attrs, 1, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attrs, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _pc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            var si = new STARTUPINFOEXW
            {
                StartupInfo = new STARTUPINFOW { cb = Marshal.SizeOf<STARTUPINFOEXW>() },
                lpAttributeList = attrs,
            };
            uint flags = EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED;
            if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, cwd, ref si, out var pi))
                throw new Win32Exception();

            _process = pi.hProcess;
            Pid = pi.dwProcessId;
            _job = ChildJob.CreateKillOnClose();
            if (_job != IntPtr.Zero && !ChildJob.Assign(_job, pi.hProcess))
            {
                ChildJob.Close(_job);
                _job = IntPtr.Zero;
            }
            ResumeThread(pi.hThread);
            CloseHandle(pi.hThread);
        }
        finally
        {
            DeleteProcThreadAttributeList(attrs);
            Marshal.FreeHGlobal(attrs);
        }

        Input = new FileStream(_ptyIn, FileAccess.Write, 4096, false);
        Output = new FileStream(_ptyOut, FileAccess.Read, 65536, false);
    }

    public void Resize(short cols, short rows)
    {
        if (_pc == IntPtr.Zero) return;
        Cols = Math.Max(cols, (short)2);
        Rows = Math.Max(rows, (short)2);
        ResizePseudoConsole(_pc, new COORD { X = Cols, Y = Rows });
    }

    /// <summary>Block until the child exits; returns its exit code.</summary>
    public uint WaitForExit()
    {
        WaitForSingleObject(_process, INFINITE);
        return GetExitCodeProcess(_process, out uint code) ? code : 0xFFFFFFFF;
    }

    public void KillTree()
    {
        if (_job != IntPtr.Zero) ChildJob.Terminate(_job);
        else if (_process != IntPtr.Zero) TerminateProcess(_process, 1);
    }

    /// <summary>
    /// Close the pseudoconsole. Must happen AFTER the child exits and BEFORE the output pipe is
    /// drained to EOF: ConPTY keeps the write end of that pipe open until it is closed.
    /// </summary>
    public void ClosePseudoConsole()
    {
        if (_pc == IntPtr.Zero) return;
        ClosePseudoConsole(_pc);
        _pc = IntPtr.Zero;
    }

    public void Dispose()
    {
        ClosePseudoConsole();
        try { Input.Dispose(); } catch { }
        try { Output.Dispose(); } catch { }
        if (_process != IntPtr.Zero) { CloseHandle(_process); _process = IntPtr.Zero; }
        if (_job != IntPtr.Zero) { ChildJob.Close(_job); _job = IntPtr.Zero; }
    }
}
