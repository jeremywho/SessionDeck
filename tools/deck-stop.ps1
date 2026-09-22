param(
    [ValidateSet('app', 'all')][string]$What = 'app'
)

# Stops ONLY SessionDeck.exe processes, by pid, never any other process.
#   app : the window process; every pty host (and the CLI inside it) keeps running
#   all : the window process AND every host this app started, via the hosts/*.json records

$hostsDir = "$env:APPDATA\SessionDeck\hosts"
$hostPids = @(Get-ChildItem "$hostsDir\*.json" -ErrorAction SilentlyContinue | ForEach-Object {
    try { (Get-Content $_.FullName -Encoding UTF8 | ConvertFrom-Json).HostPid } catch { }
})

$deck = @(Get-Process -Name SessionDeck -ErrorAction SilentlyContinue)
foreach ($p in $deck) {
    if ($p.Path -notmatch '\\SessionDeck\.exe$') { continue }
    $isHost = $hostPids -contains $p.Id
    if ($isHost -and $What -eq 'app') { continue }
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    "stopped SessionDeck pid $($p.Id) ($(if ($isHost) { 'host' } else { 'app' }))"
}
if ($What -eq 'all') { Remove-Item "$hostsDir\*.json" -ErrorAction SilentlyContinue }
