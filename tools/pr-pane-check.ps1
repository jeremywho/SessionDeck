# Builds the pull requests board headlessly and checks it against gh's own list of open PRs; optionally asserts a named session is listed on a PR.
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\bin\Release\net10.0-windows\SessionDeck.exe'),
    [string]$ExpectSession = '',
    [string]$ExpectPr = ''
)
$ErrorActionPreference = 'Stop'
$dumpPath = Join-Path $env:TEMP 'sessiondeck-prs.json'
Remove-Item -LiteralPath $dumpPath -ErrorAction SilentlyContinue
Start-Process -FilePath $Exe -ArgumentList '--prs' -Wait -WindowStyle Hidden
if (-not (Test-Path -LiteralPath $dumpPath)) { throw "no dump written: $dumpPath" }
$board = Get-Content -LiteralPath $dumpPath -Raw | ConvertFrom-Json
if ($board.status -ne 'ok') { throw "board status $($board.status): $($board.error)" }

function Flatten($rows) { foreach ($r in $rows) { if ($r.seriesCount -gt 0) { Flatten $r.members } else { $r } } }
$dumped = @(foreach ($s in $board.sections) { foreach ($r in (Flatten $s.rows)) { [pscustomobject]@{ Key = "$($r.repo.ToLower())#$($r.number)"; Section = $s.name; Row = $r } } })
$gh = gh search prs --author=@me --state=open --limit 200 --json repository,number,isDraft | ConvertFrom-Json
$expected = @($gh | ForEach-Object { [pscustomobject]@{ Key = "$($_.repository.nameWithOwner.ToLower())#$($_.number)"; Draft = $_.isDraft } })
$fail = 0
"gh: $($expected.Count) open, $(@($expected | Where-Object Draft).Count) draft.  board: $($dumped.Count) PRs in $(@($board.sections).Count) sections"
foreach ($m in $expected | Where-Object { $_.Key -notin $dumped.Key }) { "MISSING from board: $($m.Key)"; $fail++ }
foreach ($x in $dumped | Where-Object { $_.Key -notin $expected.Key }) { "EXTRA on board: $($x.Key)"; $fail++ }
foreach ($d in $dumped | Where-Object { $_.Row.depth -eq 0 }) {
    $e = $expected | Where-Object Key -eq $d.Key
    if ($e -and (($d.Section -eq 'Draft') -ne [bool]$e.Draft)) { "WRONG SECTION: $($d.Key) is in $($d.Section)"; $fail++ }
}
if ($ExpectSession) {
    $row = $dumped | Where-Object Key -eq $ExpectPr.ToLower()
    if (-not $row) { "EXPECTED PR not on board: $ExpectPr"; $fail++ }
    elseif (-not ($row.Row.agents | Where-Object name -eq $ExpectSession)) { "EXPECTED '$ExpectSession' on $ExpectPr; agents: $(($row.Row.agents | ForEach-Object name) -join ', ')"; $fail++ }
    else { "attributed: '$ExpectSession' on $ExpectPr" }
}
$links = @($dumped | ForEach-Object { $_.Row.agents } | Where-Object { $_ }).Count
"attributions on the board: $links"
if ($fail -gt 0) { exit 1 }
'OK'
