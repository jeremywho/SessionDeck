param(
    [Parameter(Mandatory)][string]$RowName,
    [ValidateSet('click', 'menu', 'list')][string]$Action = 'list',
    [string]$MenuItem
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class SdInput {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
  public struct POINT { public int X, Y; }
  public static readonly IntPtr TOPMOST = new IntPtr(-1), NOTOPMOST = new IntPtr(-2);
  public const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_SHOWWINDOW = 0x40;
  public const uint LD = 0x02, LU = 0x04, RD = 0x08, RU = 0x10;
}
'@

$AE = [System.Windows.Automation.AutomationElement]
$nameProp = $AE::NameProperty
$desk = $AE::RootElement
$w = $desk.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($nameProp, 'Session Deck')))
if (-not $w) { throw "no main window" }

# Tab-strip items and usage meters are DataItems too; only the session list's rows are wanted.
$rows = @($w.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem))) |
    Where-Object { $_.Current.Name -eq 'SessionDeck.SessionRow' })

function RowText($r) {
    ($r.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))) |
        ForEach-Object { $_.Current.Name }) -join ' | '
}

if ($Action -eq 'list') {
    foreach ($r in $rows) { RowText $r }
    return
}

$row = $rows | Where-Object { (RowText $_) -match [regex]::Escape($RowName) } | Select-Object -First 1
if (-not $row) { throw "no row matching $RowName" }
$rect = $row.Current.BoundingRectangle
$x = [int]($rect.Left + 80); $y = [int]($rect.Top + $rect.Height / 2)

# A synthetic click goes to whatever window is on top at that point, which is not necessarily
# the deck. Raise the deck topmost for the click and put it back afterwards, and refuse to click
# at all if the point still does not belong to the deck's own window.
$hwnd = [IntPtr]$w.Current.NativeWindowHandle
[SdInput]::SetWindowPos($hwnd, [SdInput]::TOPMOST, 0, 0, 0, 0, [SdInput]::SWP_NOMOVE -bor [SdInput]::SWP_NOSIZE -bor [SdInput]::SWP_SHOWWINDOW) | Out-Null
Start-Sleep -Milliseconds 200
$pt = New-Object SdInput+POINT; $pt.X = $x; $pt.Y = $y
$under = [SdInput]::GetAncestor([SdInput]::WindowFromPoint($pt), 2)
if ($under -ne $hwnd) {
    [SdInput]::SetWindowPos($hwnd, [SdInput]::NOTOPMOST, 0, 0, 0, 0, [SdInput]::SWP_NOMOVE -bor [SdInput]::SWP_NOSIZE) | Out-Null
    throw "point ($x,$y) is over hwnd $under, not the deck ($hwnd); not clicking"
}
[SdInput]::SetCursorPos($x, $y) | Out-Null
Start-Sleep -Milliseconds 150

if ($Action -eq 'click') {
    [SdInput]::mouse_event([SdInput]::LD, 0, 0, 0, [IntPtr]::Zero); [SdInput]::mouse_event([SdInput]::LU, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 300
    [SdInput]::SetWindowPos($hwnd, [SdInput]::NOTOPMOST, 0, 0, 0, 0, [SdInput]::SWP_NOMOVE -bor [SdInput]::SWP_NOSIZE) | Out-Null
    "clicked row $RowName"
    return
}

[SdInput]::mouse_event([SdInput]::RD, 0, 0, 0, [IntPtr]::Zero); [SdInput]::mouse_event([SdInput]::RU, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 600
[SdInput]::SetWindowPos($hwnd, [SdInput]::NOTOPMOST, 0, 0, 0, 0, [SdInput]::SWP_NOMOVE -bor [SdInput]::SWP_NOSIZE) | Out-Null
$items = $desk.FindAll([System.Windows.Automation.TreeScope]::Subtree,
    (New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)),
        (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $w.Current.ProcessId)))))
if ($items.Count -eq 0) { throw "context menu did not open" }
"menu: " + (($items | ForEach-Object { $_.Current.Name }) -join ' / ')
if ($MenuItem) {
    $target = $items | Where-Object { $_.Current.Name -like "$MenuItem*" } | Select-Object -First 1
    if (-not $target) { throw "no menu item $MenuItem" }
    # A real click, not InvokePattern: the popup is the deck's own HWND, so the window check holds.
    $mr = $target.Current.BoundingRectangle
    $mx = [int]($mr.Left + $mr.Width / 2); $my = [int]($mr.Top + $mr.Height / 2)
    $mpt = New-Object SdInput+POINT; $mpt.X = $mx; $mpt.Y = $my
    $mh = [SdInput]::GetAncestor([SdInput]::WindowFromPoint($mpt), 2)
    $mpid = 0; [SdInput]::GetWindowThreadProcessId($mh, [ref]$mpid) | Out-Null
    if ($mpid -ne $w.Current.ProcessId) { throw "menu item point is over pid $mpid, not the deck; not clicking" }
    [SdInput]::SetCursorPos($mx, $my) | Out-Null
    Start-Sleep -Milliseconds 120
    [SdInput]::mouse_event([SdInput]::LD, 0, 0, 0, [IntPtr]::Zero); [SdInput]::mouse_event([SdInput]::LU, 0, 0, 0, [IntPtr]::Zero)
    "clicked $($target.Current.Name)"
}
