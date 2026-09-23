param(
    [string]$TabText = "",
    [string]$HostId = "",
    [int]$Frames = 8,
    [int]$IntervalMs = 120,
    [string]$Prefix = "$env:TEMP\sd-frame",
    [string]$Type = "",
    [switch]$Burst
)

# -Burst: once on the wanted tab, leave it with Ctrl+Tab, come back with Ctrl+Shift+Tab, and start
# capturing the instant that keystroke is sent, so the frames show the switch itself (the flash, if
# any) rather than the settled pane.

# Activate a deck tab by its title text WITHOUT synthetic mouse clicks (a full-screen overlay on this
# machine wins every hit-test): the tab is invoked through UI Automation, the deck is foregrounded,
# focus is handed to the terminal with Tab from the tab strip, and keystrokes go out only after the
# foreground window is verified to be the deck's own HWND. Then N screen captures of the pane.

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class SdFrames {
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte sc, uint f, IntPtr e);
}
'@

$AE = [System.Windows.Automation.AutomationElement]
$w = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'Session Deck')))
if (-not $w) { throw "no main window" }
$hw = [IntPtr]$w.Current.NativeWindowHandle
$wr = $w.Current.BoundingRectangle

function AssertForeground {
    $fg = [SdFrames]::GetForegroundWindow()
    if ($fg -ne $hw) { throw "foreground is $fg, not the deck ($hw); refusing to send input" }
}

[SdFrames]::SetWindowPos($hw, [IntPtr](-1), 0, 0, 0, 0, 0x43) | Out-Null
[SdFrames]::keybd_event(0x12, 0, 0, [IntPtr]::Zero); [SdFrames]::keybd_event(0x12, 0, 2, [IntPtr]::Zero)
[SdFrames]::SetForegroundWindow($hw) | Out-Null
Start-Sleep -Milliseconds 400
try {
    AssertForeground
    # One browser hosts every tab; its page titles itself "terminal port=<port>" for the shown host,
    # and that title is the browser pane's UIA name. Cycle Ctrl+Tab until the wanted port shows.
    $rec = Get-ChildItem "$env:APPDATA\SessionDeck\hosts\*.json" | ForEach-Object { Get-Content $_.FullName -Encoding UTF8 | ConvertFrom-Json } | Where-Object { if ($HostId) { $_.Id -eq $HostId } else { $TabText -and $_.Title -match [regex]::Escape($TabText) } } | Select-Object -First 1
    if (-not $rec) { throw "no host matching id '$HostId' / title '$TabText'" }
    $want = "port=$($rec.Port)"
    for ($i = 0; $i -lt 8; $i++) {
        $panes = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Pane)))
        $shown = $panes | Where-Object { $_.Current.Name -match ('terminal ' + [regex]::Escape($want) + '$') } | Select-Object -First 1
        if ($shown) { break }
        AssertForeground
        [System.Windows.Forms.SendKeys]::SendWait('^{TAB}')
        Start-Sleep -Milliseconds 600
    }
    if (-not $shown) { throw "could not bring the '$TabText' tab to the front" }
    # hand keyboard focus to the terminal: click nothing, just focus the browser pane via UIA
    try { $shown.SetFocus() } catch { }
    Start-Sleep -Milliseconds 500
    if ($Type) { AssertForeground; [System.Windows.Forms.SendKeys]::SendWait($Type); Start-Sleep -Milliseconds 400 }
    if ($Burst) {
        AssertForeground; [System.Windows.Forms.SendKeys]::SendWait('^{TAB}'); Start-Sleep -Milliseconds 900
        AssertForeground; [System.Windows.Forms.SendKeys]::SendWait('^+{TAB}')
    }

    $left = [int]($wr.Left + 410); $top = [int]($wr.Top + 70)
    $width = [int]($wr.Width - 420); $height = [int]($wr.Height - 80)
    for ($i = 0; $i -lt $Frames; $i++) {
        $bmp = New-Object System.Drawing.Bitmap $width, $height
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($left, $top, 0, 0, $bmp.Size)
        $bmp.Save("$Prefix-$i.png")
        $g.Dispose(); $bmp.Dispose()
        Start-Sleep -Milliseconds $IntervalMs
    }
    "captured $Frames frames to $Prefix-N.png"
}
finally {
    [SdFrames]::SetWindowPos($hw, [IntPtr](-2), 0, 0, 0, 0, 0x3) | Out-Null
}
