param(
    [ValidateSet('click', 'shot', 'hosts', 'kill-app', 'build', 'launch', 'dismiss-restore')]
    [string]$Action,
    [string]$Button,
    [string]$Out = "$env:TEMP\sd-shot.png"
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class SdNative {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT { public int L,T,R,B; }
}
'@

$exe = "C:\Data\Repos\.worktrees\SessionDeck\spike\bin\Release\net10.0-windows\SessionDeck.exe"
$nameProp = [System.Windows.Automation.AutomationElement]::NameProperty
$idProp = [System.Windows.Automation.AutomationElement]::AutomationIdProperty
$desk = [System.Windows.Automation.AutomationElement]::RootElement

function MainWindow {
    $desk.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($nameProp, 'Session Deck')))
}

switch ($Action) {
    'build' {
        # The launcher runs the Release binary; a Debug build leaves it stale and every check that follows measures the old app.
        dotnet build 'C:\Data\Repos\.worktrees\SessionDeck\spike\SessionDeck.csproj' -c Release -v m 2>&1 | Select-String -Pattern ' error |Build succeeded|Build FAILED'
        "release exe built $((Get-Item $exe).LastWriteTime.ToString('HH:mm:ss'))"
    }
    'launch' {
        $env:SD_NO_INSTALL = "1"
        Start-Process -FilePath $exe | Out-Null
        Start-Sleep -Seconds 5
        $w = MainWindow
        "launched exe built $((Get-Item $exe).LastWriteTime.ToString('HH:mm:ss')); main window: $($w -ne $null)"
    }
    'dismiss-restore' {
        $r = $desk.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($nameProp, 'Restore sessions')))
        if ($r) { $r.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close(); "dismissed" } else { "no restore window" }
    }
    'click' {
        $w = MainWindow
        if (-not $w) { throw "no main window" }
        $btn = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($idProp, $Button)))
        if (-not $btn) { throw "no button $Button" }
        $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        "clicked $Button"
    }
    'shot' {
        $w = MainWindow
        if (-not $w) { throw "no main window" }
        $h = [IntPtr]$w.Current.NativeWindowHandle
        $r = New-Object SdNative+RECT
        [SdNative]::GetWindowRect($h, [ref]$r) | Out-Null
        $bmp = New-Object System.Drawing.Bitmap(($r.R - $r.L), ($r.B - $r.T))
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $dc = $g.GetHdc()
        [SdNative]::PrintWindow($h, $dc, 2) | Out-Null
        $g.ReleaseHdc($dc)
        $bmp.Save($Out)
        "saved $Out ($($bmp.Width)x$($bmp.Height))"
    }
    'hosts' {
        Get-ChildItem "$env:APPDATA\SessionDeck\hosts\*.json" -ErrorAction SilentlyContinue | ForEach-Object {
            $h = Get-Content $_.FullName -Encoding UTF8 | ConvertFrom-Json
            $alive = (Get-Process -Id $h.HostPid -ErrorAction SilentlyContinue) -ne $null
            [pscustomobject]@{ Id = $h.Id; Provider = $h.Provider; HostPid = $h.HostPid; Alive = $alive; ChildPid = $h.ChildPid; Title = $h.Title; ExitCode = $h.ExitCode }
        } | Format-Table -AutoSize | Out-String -Width 200
    }
    'kill-app' {
        & "$PSScriptRoot\deck-stop.ps1" app
    }
}
