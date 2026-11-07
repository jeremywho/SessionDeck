using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace SessionDeck;

/// <summary>
/// F13 to F16 as system-wide hot keys, so a stick push reaches the deck from any app. Receiving a
/// hot key gives this process the right to come to the front, which a plain SendInput key does not.
/// A key another app already owns is left to it; the deck's in-window handling still covers that key
/// while the deck is in front.
/// </summary>
internal sealed class ListHotKeys : IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_NOREPEAT = 0x4000;
    static readonly (int Id, uint VirtualKey, string Name)[] Keys =
    {
        (0x5D01, 0x7C, "F13"), (0x5D02, 0x7D, "F14"), (0x5D03, 0x7E, "F15"), (0x5D04, 0x7F, "F16"),
    };

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    readonly HwndSource _source;
    readonly Action<string> _pressed;
    readonly List<int> _registered = new();

    /// <summary>Which keys could be registered, by name; the rest belong to another app.</summary>
    public IReadOnlyList<string> Owned { get; }

    public ListHotKeys(HwndSource source, Action<string> pressed)
    {
        _source = source;
        _pressed = pressed;
        var owned = new List<string>();
        foreach (var (id, vk, name) in Keys)
        {
            if (RegisterHotKey(source.Handle, id, MOD_NOREPEAT, vk)) { _registered.Add(id); owned.Add(name); }
        }
        Owned = owned;
        source.AddHook(WndProc);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;
        int id = wParam.ToInt32();
        foreach (var (keyId, _, name) in Keys)
        {
            if (keyId != id) continue;
            handled = true;
            _pressed(name);
            break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(WndProc);
        foreach (int id in _registered) UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
    }
}
