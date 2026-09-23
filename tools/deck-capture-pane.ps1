param(
    [string]$Prefix = "$env:TEMP\sd-pane",
    [int]$Frames = 3,
    [int]$IntervalMs = 400,
    [int]$Top = 380,
    [int]$Height = 200
)

# Screen-capture a band of the deck's terminal pane. No input is sent; the deck is raised topmost
# for the capture and put back. Reports which host's pane is visible so the caller can check.

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class SdCap { [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags); }
'@
$AE = [System.Windows.Automation.AutomationElement]
$w = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'Session Deck')))
if (-not $w) { throw "no main window" }
$r = $w.Current.BoundingRectangle
$panes = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Pane)))
$shown = $panes | Where-Object { $_.Current.Name -match 'terminal port=(\d+)' } | Select-Object -First 1
$port = if ($shown -and $shown.Current.Name -match 'port=(\d+)') { $Matches[1] } else { '' }
$host_ = Get-ChildItem "$env:APPDATA\SessionDeck\hosts\*.json" | ForEach-Object { Get-Content $_.FullName -Encoding UTF8 | ConvertFrom-Json } | Where-Object { "$($_.Port)" -eq $port } | Select-Object -First 1
"visible pane: port=$port provider=$($host_.Provider) title='$($host_.Title)'"
$hw = [IntPtr]$w.Current.NativeWindowHandle
[SdCap]::SetWindowPos($hw, [IntPtr](-1), 0, 0, 0, 0, 0x43) | Out-Null
Start-Sleep -Milliseconds 400
try {
    for ($i = 0; $i -lt $Frames; $i++) {
        $bmp = New-Object System.Drawing.Bitmap ([int]($r.Width - 420)), $Height
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen([int]$r.Left + 410, [int]$r.Top + $Top, 0, 0, $bmp.Size)
        $bmp.Save("$Prefix-$i.png")
        $g.Dispose(); $bmp.Dispose()
        Start-Sleep -Milliseconds $IntervalMs
    }
    "saved $Frames frames to $Prefix-N.png"
}
finally { [SdCap]::SetWindowPos($hw, [IntPtr](-2), 0, 0, 0, 0, 0x3) | Out-Null }
