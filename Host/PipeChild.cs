using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SessionDeck.Host;

/// <summary>
/// A child process on three plain pipes instead of a pseudoconsole: what a CLI needs when it is
/// spoken to in a line protocol rather than drawn. Same job object rule as <see cref="ConPty"/>:
/// the child and everything it starts die with this process.
/// </summary>
internal sealed class PipeChild : IDisposable
{
    const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    const uint CREATE_SUSPENDED = 0x00000004;
    const uint CREATE_NO_WINDOW = 0x08000000;
    const int STARTF_USESTDHANDLES = 0x00000100;
    const int PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002;
    const uint HANDLE_FLAG_INHERIT = 0x00000001;
    const uint INFINITE = 0xFFFFFFFF;

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
    static extern bool SetHandleInformation(SafeFileHandle hObject, uint dwMask, uint dwFlags);

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

    IntPtr _job;
    IntPtr _process;

    public int Pid { get; }
    public FileStream Input { get; }
    public FileStream Output { get; }
    public FileStream Error { get; }

    public PipeChild(string commandLine, string cwd)
    {
        var (childIn, ourIn) = Pipe(childReads: true);
        var (ourOut, childOut) = Pipe(childReads: false);
        var (ourErr, childErr) = Pipe(childReads: false);

        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        IntPtr attrs = Marshal.AllocHGlobal(size);
        IntPtr handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
        bool listed = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attrs, 1, 0, ref size)) throw new Win32Exception();
            listed = true;
            // Only these three handles cross into the child: anything else inheritable in this process stays here.
            Marshal.WriteIntPtr(handles, 0, childIn.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, childOut.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size * 2, childErr.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attrs, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, (IntPtr)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            var si = new STARTUPINFOEXW
            {
                StartupInfo = new STARTUPINFOW
                {
                    cb = Marshal.SizeOf<STARTUPINFOEXW>(),
                    dwFlags = STARTF_USESTDHANDLES,
                    hStdInput = childIn.DangerousGetHandle(),
                    hStdOutput = childOut.DangerousGetHandle(),
                    hStdError = childErr.DangerousGetHandle(),
                },
                lpAttributeList = attrs,
            };
            uint flags = EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED | CREATE_NO_WINDOW;
            if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, true, flags, IntPtr.Zero, cwd, ref si, out var pi))
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
        catch
        {
            ourIn.Dispose();
            ourOut.Dispose();
            ourErr.Dispose();
            throw;
        }
        finally
        {
            if (listed) DeleteProcThreadAttributeList(attrs);
            Marshal.FreeHGlobal(attrs);
            Marshal.FreeHGlobal(handles);
            childIn.Dispose();
            childOut.Dispose();
            childErr.Dispose();
        }

        Input = new FileStream(ourIn, FileAccess.Write, 4096, false);
        Output = new FileStream(ourOut, FileAccess.Read, 65536, false);
        Error = new FileStream(ourErr, FileAccess.Read, 4096, false);
    }

    /// <summary>One pipe whose child end is inheritable and whose end kept here is not.</summary>
    static (SafeFileHandle Read, SafeFileHandle Write) Pipe(bool childReads)
    {
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = 1 };
        if (!CreatePipe(out var read, out var write, ref sa, 0)) throw new Win32Exception();
        if (!SetHandleInformation(childReads ? write : read, HANDLE_FLAG_INHERIT, 0)) throw new Win32Exception();
        return (read, write);
    }

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

    public void Dispose()
    {
        try { Input.Dispose(); } catch { }
        try { Output.Dispose(); } catch { }
        try { Error.Dispose(); } catch { }
        if (_process != IntPtr.Zero) { CloseHandle(_process); _process = IntPtr.Zero; }
        if (_job != IntPtr.Zero) { ChildJob.Close(_job); _job = IntPtr.Zero; }
    }
}
