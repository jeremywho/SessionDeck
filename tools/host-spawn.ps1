param([string]$Id, [string]$Cmd)
Get-ChildItem Env: | Where-Object { $_.Name -like 'CLAUDE*' -or $_.Name -eq 'CLAUDECODE' -or $_.Name -like 'CODEX_*' } | ForEach-Object { Remove-Item "Env:$($_.Name)" }
New-Item -ItemType Directory -Force "C:\Temp\sd-probe\hosts" | Out-Null
$spec = @{ Id=$Id; SessionId=""; Provider="Shell"; CommandLine=$Cmd; Cwd="C:\Users\Jeremy"; Cols=100; Rows=30; HostsDir="C:\Temp\sd-probe\hosts"; RingBytes=65536 } | ConvertTo-Json -Compress
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "C:\Data\Repos\.worktrees\SessionDeck\spike\bin\Release\net10.0-windows\SessionDeck.exe"
$psi.Arguments = "--host"
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$p = [System.Diagnostics.Process]::Start($psi)
$p.StandardInput.Write($spec); $p.StandardInput.Close()
"ready: " + $p.StandardOutput.ReadLine()
Start-Sleep 3
"host exited: " + $p.HasExited
