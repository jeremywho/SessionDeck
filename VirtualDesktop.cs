using System.Runtime.InteropServices;

namespace ClaudeSessionMonitor;

/// <summary>
/// Which virtual desktop a window is on. Uses the documented IVirtualDesktopManager
/// (GetWindowDesktopId) for the window's desktop GUID, plus the registry's desktop order to turn
/// that GUID into a real "Desktop N" index + tell whether it's the current desktop. The registry
/// layout is unofficial but stable across builds — far steadier than the IID-versioned internal API.
/// </summary>
internal static class VirtualDesktop
{
    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr w, out int onCurrent);
        [PreserveSig] int GetWindowDesktopId(IntPtr w, out Guid id);
        [PreserveSig] int MoveWindowToDesktop(IntPtr w, ref Guid id);
    }

    // Per-thread so the (STA) resolver thread owns its own COM object.
    [ThreadStatic] static IVirtualDesktopManager? _mgr;
    static IVirtualDesktopManager Mgr() => _mgr ??= (IVirtualDesktopManager)Activator.CreateInstance(
        Type.GetTypeFromCLSID(new Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a"))!)!;

    /// <summary>The virtual-desktop GUID a window is on (Guid.Empty on failure).</summary>
    public static Guid DesktopOf(IntPtr hwnd)
    {
        try { return Mgr().GetWindowDesktopId(hwnd, out var g) == 0 ? g : Guid.Empty; }
        catch { return Guid.Empty; }
    }

    const string Base = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    /// <summary>Ordered desktop GUIDs (index 0 = "Desktop 1") and the current desktop GUID, from the registry.</summary>
    public static (List<Guid> Order, Guid Current) Layout()
    {
        var order = new List<Guid>();
        Guid current = Guid.Empty;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Base);
            if (k?.GetValue("VirtualDesktopIDs") is byte[] ids)
                for (int i = 0; i + 16 <= ids.Length; i += 16)
                    order.Add(new Guid(ids[i..(i + 16)]));
            if (k?.GetValue("CurrentVirtualDesktop") is byte[] c && c.Length == 16)
                current = new Guid(c);
        }
        catch { }
        return (order, current);
    }
}
