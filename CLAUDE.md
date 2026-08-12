# ClaudeSessionMonitor — agent guide

Windows system-tray app (**.NET 10, WPF + [WPF-UI](https://github.com/lepoco/wpfui)/Fluent**) that
lists live local Claude Code **and Codex CLI** sessions, read from `~/.claude/` and `~/.codex/` on
disk. `README.md` is the user-facing feature tour; this file is for working *on* the code.

**One exception to "all local".** The footer's plan-usage meters come from a network call
(`UsageApi` → `GET api.anthropic.com/api/oauth/usage`, bearer token read from
`~/.claude/.credentials.json`). Everything else is still pure disk reads. See *Usage bar* below
before adding anything else that touches the network or those credentials.

## Build, run, verify
```
dotnet build -c Release
bin\Release\net10.0-windows\ClaudeSessionMonitor.exe
dotnet test Tests\ClaudeSessionMonitor.Tests.csproj
```
- It's a **tray app with no console** — failures are silent. After a launch, check
  `%TEMP%\claude-session-monitor-error.log`.
- Iteration loop that works well here: kill the running exe → build → launch → **screenshot to
  verify** (the UI is the spec; `PrintWindow` with flag `2` captures it). `--list` / `--windows` dump
  the session list / focus mapping to `%TEMP%` for headless checks.
- Source files are **CRLF** (Windows). Keep them CRLF when editing.
- Settings persist to `%APPDATA%\ClaudeSessionMonitor\settings.json` — **shared across builds**, so a
  debug run can clobber your real prefs. Restore anything a test changes.

## Architecture (data flow)
1. `SessionScanner.Scan()` — enumerates `~/.claude/sessions/<pid>.json` (the live registry), drops
   dead PIDs, and `Enrich()`s each from the **tail** of its transcript
   `~/.claude/projects/<slug>/<sessionId>.jsonl` (model, context %, last tool, title, API error).
   Transcript reads are **mtime/size-cached** — an unchanged session costs a `stat`.
2. `CountSubagents()` — counts `<sessionId>/subagents/agent-*.jsonl` (total + fresh-mtime `<30s` =
   "working now") through `SubagentCounter`. It inventories when the directory changes, stats only
   recently active files on 5s passes, and fully reconciles every 15s so settled history is cheap while
   an unusual resumed cold agent is still detected. This cache is independent of the parent transcript,
   because subagents stream while the parent transcript can remain static.
3. `SessionRow` (observable VM) derives the display **state** from `status` via `SessionStateMap`.
4. `SessionsWindow` binds `ObservableCollection<SessionRow>` to a DataGrid, live-sorted by a
   `ListCollectionView` (SortPriority → `LastChanged` desc → Name).
5. Refresh starts at most every **5s**. Discovery, enrichment, process-tree attribution, and registry
   snapshots run on a thread-pool thread with no overlapping passes; only the completed snapshot is
   applied on the WPF dispatcher. Do not reintroduce session-file-triggered full scans: busy Claude
   heartbeats can sustain a rewrite stream and previously drove nearly eight scans per second.
6. A background **STA thread** tags each row with its virtual desktop every ~8s
   (`VirtualDesktop` + `WindowActivator.ResolveWindows`, which is UIA-heavy — hence off the UI thread).
7. `CodexScanner.Scan()` appends live Codex sessions to the same list (see below). Rows are told apart
   only by the provider mark in the model pill — sort, focus, columns and tooltips are all shared.

## Codex (`CodexScanner.cs`, `FileHolders.cs`)
Codex publishes **no live registry**. There is only the append-only rollout per thread,
`~/.codex/sessions/YYYY/MM/DD/rollout-<ts>-<id>.jsonl`, and everything has to be derived from it.
Four things will bite you, in rough order of how long they take to notice:

- **`codex resume` appends to the ORIGINAL file.** The filename timestamp, the day folder, and the
  creation time all describe when the thread *started*, not whether it's running. A thread from three
  weeks ago can be the live one. Never infer liveness from any of them.
- **Liveness = who holds the file open.** `FileHolders.OwnerPid` (Restart Manager `RmGetList`) answers
  it exactly, non-admin, and hands back the `codex.exe` PID — which is also what makes double-click-to-
  focus work, since `WindowActivator` is provider-agnostic and just needs a PID. It costs ~50ms a call,
  so it lives on the `codex-probe` background thread with a per-pass budget; `Scan()` on the UI tick only
  reads the map it produces. New/growing candidates keep priority, while up to eight known owners are
  revalidated per pass on a rotating 30s cadence. A miss still needs two checks before removal. Startup
  crash recovery bypasses this cache and directly verifies each saved Codex session. Don't move a probe
  onto the UI thread or let known-owner verification exceed the shared budget.
- **Windows does not reliably refresh mtime for a file a process holds open.** A `codex exec` rollout was
  measured sitting at a **14-minute-old** timestamp while gaining 7KB in 12 seconds (the TUI's own
  rollout *does* update). So mtime cannot drive idle time or "is this subagent working" — both use
  **size growth** (`_growth`) and the **record timestamps inside the rollout** instead. Writes are bursty:
  several seconds of nothing, then a chunk. That's why `--list` probes four times.
- **The two id fields are not what they sound like.** In `session_meta`, `id` is *this* thread and
  `session_id` is the **root** thread — so on a subagent's rollout they differ. Reading `session_id` as
  "this session" makes every subagent masquerade as its parent (it shipped that way for one build: five
  duplicate rows). The upside: `session_id` is the root at *any* nesting depth, so subagent roll-up needs
  no parent-chain walk. Note also that `thread_source` is the plain string `"user"`/`"subagent"`; the
  spawn detail (parent, depth, nickname) is in `source`.
- **Not every live thread is a terminal.** The header's `originator` says who is driving:
  `codex-tui` (a terminal), `*exec*` (headless one-shot), anything else → **`companion`**
  (`CodexScanner.KindFor`) — a thread another app drives through the codex app server. The Claude
  Code codex plugin stamps `"Claude Code"` here for its "Codex Companion Task" second opinions; IDE
  extensions land in the same bucket. Companions are listed with an **agent badge** and the
  companion prefix trimmed off the name (`SessionRow.CompanionName`), double-click declines instead
  of hunting for a window that never existed, and `IsRestorable` excludes them (only `tui` restores).
  An empty/unknown originator stays `tui` on purpose: misreading a real terminal as a companion
  silently breaks focus + restore, the expensive direction.

Other Codex notes: model + effort come from `turn_context` (last one wins) and `thread_settings_applied`
(a mid-thread switch); context is `last_token_usage.input_tokens` against `model_context_window` —
**not** `total_token_usage`, which is cumulative for the thread and runs to tens of millions. The first
parse of a session reads the **whole file** because `turn_context` is per-turn and can sit megabytes
back; after that it's a tail read gated on mtime **and size**. There is deliberately **no**
`FileSystemWatcher` on the rollout tree — Codex writes to it constantly during a turn.

## Usage bar (`UsageApi.cs`, `AccountScanner.cs`)
The second footer bar: signed-in address on the left, one fill-behind pill per plan limit on the right.
- **Account** (email / org) comes from `~/.claude.json` → `oauthAccount`. Note that's `~/.claude.json`,
  the **file**, not the `~/.claude/` **directory** everything else reads.
- **Meters** come from the API, polled every 15 min. `~/.claude.json` → `cachedUsageUtilization` is
  only the **fallback** when a poll fails, and is labelled `· cached` when shown.
- **Switching accounts re-polls immediately** (`CredentialsWatcher`), because 15 minutes of the
  previous account's pills beside the new account's address is just wrong. The switch is detected by
  `~/.claude/.credentials.json` being **replaced** — the switcher writes a temp file and renames over
  the top, so Created/Renamed/Changed/Deleted are all treated the same — then debounced ~1s, because
  the sibling `~/.claude.json` patch (identity + usage cache) lands a few milliseconds later and
  firing on the first event would re-read the *old* identity. The 15-min poll is untouched; this is
  an extra trigger.
- **The account's identity, not the watcher, is what invalidates the numbers.** `RefreshAccount`
  compares `oauthAccount.accountUuid` against the last reading and drops `_liveMeters` when it
  changes, so a switch noticed by any path (the 5s scan, say) is handled — and a poll still in flight
  across the switch has its result thrown away, since the token was re-read from disk mid-swap and
  whose numbers came back is unknowable. Post-switch the bar shows the config's cache (`· cached`)
  for the moment it takes the new poll to land, or keeps showing it if that poll fails.
- **All three poll callers go through one `SingleFlight`** — startup, the 15-min timer, the switch —
  so a burst of switches can't put N HTTP calls in the air. One run in flight, at most one queued.
- Both sources hit the same parser: the API response body *is* the object the config caches under
  `utilization`, so `AccountScanner.ParseLimits` serves both. Keep it that way.
- Parse the self-describing **`limits` array**, never the `five_hour` / `seven_day_opus` siblings —
  per-model limits appear *only* in `limits` (as `weekly_scoped` + a model scope) while
  `seven_day_opus` and friends sit `null`. Each entry carries its own `severity`, so don't invent
  colour thresholds. The scoped pill is named from `scope.model.display_name` — don't hardcode "Fable".
- **The Codex meter is a different animal** and shares only the `UsageMeter` type. It comes from
  `rate_limits` on every `token_count` record in the rollout — no network, no credentials, no cache —
  via `CodexScanner.PlanMeters`, seeded at startup from the newest rollout so it shows before you run
  Codex. Codex sends **no severity**, so that one *does* derive its colour (the 70/85 Context%
  thresholds); the "don't invent thresholds" rule above is about not overriding a server that has an
  opinion, and Codex has none.
- The bar rebuilds on a **value signature**, not list identity: the Codex meters are rebuilt from the
  rollout on every read, so a reference check would rebuild every tick and kill any open tooltip.
- **Don't judge the cache's freshness — it can't be done.** Two thresholds (15 then 45 min) both cried
  stale during normal use; the cache was measured 52 minutes old while a session ran flat out. It
  refreshes on nothing observable from outside. This is *why* the app polls: staleness is now measured
  against **our own** interval (`PollInterval * 2.5` = two missed polls), which is a fact, not a guess.
- **`.credentials.json` is read-only, always.** It's Claude Code's live auth state; writing it — even
  to refresh an expired token — can break the user's sign-in. An expired token just means the fetch
  returns null and the cache shows instead.
- Layout: the meters are docked **before** the account label (DockPanel allocates in child order), and
  the address trims before the state note does. Both orderings were bugs first — at the 460px minimum
  width there is not room for everything.

## Status → display state (`SessionState.cs`)
`busy`→Working · `waiting`→Awaiting · `idle`→Completed (green ✓) · `shell`→Working · default→Idle.
**Error** is *not* a status — it's read from the transcript (`isApiErrorMessage`) and sorts to the top.

## Gotchas (don't rediscover these)
- **Nothing without a terminal gets a row.** `CodexAttribution.Fold` removes every headless session —
  Codex companion/exec threads *and* Claude's own `bg` sessions (what `/tr:pr` spawns) — folding it onto
  the session that started it as a badge count, or dropping it when no parent can be found. The rule is
  the requirement, not an optimisation: a row you can't click into is worse than absent. Claude `bg` is
  matched by that exact kind rather than "not interactive", because an unrecognised kind is far more
  likely to be a real terminal, and hiding one of those is the expensive mistake.
- **A Claude `bg` session is a child PROCESS of its parent**, so `OwnerByProcessTree` walks the tree and
  names it outright — the strongest signal available, and bounded to 12 hops so a deep shell nest can't
  attribute a job to something merely far above it. Codex threads can't use this: the app-server daemon
  they run under is nobody's child.
- **Headless Codex threads are folded onto the Claude session that started them** (`CodexAttribution`),
  because a row for one is a row you can't click into — no terminal exists. Attribution is a *heuristic*
  and worth understanding before trusting the count: there is no explicit link to follow, since the
  codex app-server daemon is shared between every Claude session and is orphaned from the process tree
  (the plugin's broker script has already exited). A thread started in a session's scratchpad names its
  parent exactly; otherwise all we have is the working directory, and **twelve sessions were observed
  sharing `C:\Users\Jeremy`**, so the `busy` tie-break decides — sound, because a session waiting on a
  second opinion is busy, but not certain. A thread we can't place is **hidden**, not shown: the list is
  for sessions you can act on, and a row with no terminal and no parent offers nothing to do. Don't
  "helpfully" restore those rows — their absence is the requirement.
- **Focus resolves a tab by asking the session's console for its title, not by guessing it.**
  `ConsoleTitle.Read` attaches to the session's console (`AttachConsole`) and reads the real title,
  which is the same string UIA reports as the tab's name. Guessing from session metadata was the
  original approach and it rots: Codex titles its tab with the thread UUID at startup, but a
  long-running session had that replaced by the shell's cwd (`Jeremy`) — so every metadata candidate
  missed. The metadata candidates remain only as a fallback.
- **Identical titles are broken by a temporary marker.** Two Codex sessions both sitting at `Jeremy`
  cannot be told apart by title, and nothing in UIA maps a tab to the process inside it. So
  `ActivateByConsoleTitle` stamps a unique marker on that session's console, polls for the tab wearing
  it, activates it, and restores the old title in a `finally`. Verified the right tab is chosen by
  marking a session externally and confirming its tab was the selected one in the foregrounded window.
- **Selecting a tab focuses its HEADER, not the session.** After `TabSelector.Select`, keystrokes go to
  the tab strip until something focuses the pane — the element classed `TermControl`. That's what
  `TabSelector.FocusTerminal` is for, and it must run *after* the window is foregrounded, since
  `SetFocus` on a background window is refused.
- **An idle Codex TUI writes no rollout at all** until its first turn, so a freshly opened Codex session
  is invisible to the app (and therefore un-focusable) until you actually send it something. Discovery
  is rollout-based; there is nothing else to see.
- **Launched terminals inherit THIS app's environment.** `Start` uses `UseShellExecute=false`, so
  whatever environment the app was started with is handed to every session it opens. An agent harness
  that sets **`NO_COLOR=1`** for clean tool output relaunched the app as a child, and from then on
  every session opened from the buttons was monochrome — with *no code change anywhere*, which is
  precisely why it was hard to find. `SessionLauncher` now drops such variables from the child, but
  only when they were **injected** (set on the process, persisted in neither the User nor Machine
  environment) — someone who genuinely sets `NO_COLOR` still gets no color. Covered by
  `LaunchEnvironmentTests`; verified those tests fail when the scrub is removed.
  **Practical note for agents: don't relaunch the user's app from your own shell** — it inherits your
  environment. Clear the offending variable first, or let the user start it.
- **The launcher's two rows are aligned by hand, and both mechanisms look deletable.** The Codex row
  carries an empty 36px `Border` where Claude's "named" button sits, and both provider glyphs have a
  fixed `Width` because `✳` and `◆` measure differently (~4px, enough to visibly skew the rows). Remove
  either and the rows stop lining up.
- **`;` is Windows Terminal's subcommand separator.** A command containing one gets split across
  multiple tabs rather than passed through (seen for real: a five-statement diagnostic became five
  tabs). Today's commands have no semicolons; anything that templates flags in must keep it that way.
- **An `Auto` column that can collapse will move everything beside it.** The subagent badge's column
  did exactly that: it measured to zero on rows with no agents, so the model pill sat further right
  there than on rows with a badge, and pills visibly jumped as agents came and went. Its slot is now
  reserved with `MinWidth` (not a fixed `Width` — a two-digit count still has to fit). Same trap for
  anything else added to that cell.
- **The footer bar is full at 460px.** The status legend already runs to the window edge at the minimum
  width, so the live-count label on the left has almost no slack — a "8 Claude, 1 Codex" breakdown there
  overlapped the legend and had to move into its tooltip. Verify footer changes at 460px, not at your
  window size.
- **`FriendlyModel` must not require a minor version.** Model ids come both ways — `claude-opus-4-8`
  *and* `claude-opus-5` / `claude-fable-5` — and the original regex demanded `-(\d+)-(\d+)`, so the newer
  ids fell through and rendered raw. It also strips a bracketed variant (`claude-opus-5[1m]`).
- **DataGrid `RowBackground` beats the RowStyle trigger.** Set the row Background in the style (base
  Transparent + an `IsMouseOver` trigger) — never a `RowBackground=` attribute — or hover highlight dies.
- **Column-width persistence** once clobbered the `*` Session column (saved it as a fixed width). Only
  `Toggleable` (extra) columns persist width; curated columns keep their XAML widths.
- **Theme swap:** `App.ToggleTheme` swaps the palette dictionary **and** calls
  `_window.RefreshAfterThemeChange()` to re-run the Context% brush converter. A converter-resolved
  brush won't re-theme on its own. The provider marks avoid that entirely where they can: the hues
  live in the theme dictionaries (`ClaudeMarkBrush` / `CodexMarkBrush`), so the static launcher glyphs
  bind them by `DynamicResource` and re-theme for free; only the per-row bindings go through
  `ProviderColorConverter`.
- **`ShowInTaskbar=false`** hides the taskbar button via a hidden **owner window** (WPF mechanism), not
  `WS_EX_TOOLWINDOW` — check `GW_OWNER`, not the ex-style, if you're verifying it.
- **`shell` status** = background bash shells running while the agent "holds" idle → it's **Working**,
  not Idle. The ⚙ subagent badge won't fire for it (that counts Task *subagents*, a different thing).
- **`IsAlive`** matches process name `StartsWith("claude")`, because Claude Code's self-update renames
  the running `claude.exe` → `claude.exe.old.<ts>` mid-session.
- Everything here reads **undocumented** Claude Code / Codex files; parsing is isolated in
  `SessionScanner` and `CodexScanner` (+ `VirtualDesktop` for the registry desktop list), so a schema
  change is a one-file fix.
- **Restore is per-provider, and the two invocations barely rhyme.** Claude gets
  `claude --resume <id> --name <name>`; Codex gets `codex resume <id>` — a *subcommand*, not a flag, and
  with **no `--name`** (the name already belongs to the thread on their side). Flags are separate
  settings for the same reason: nothing is spelled the same, so Claude's
  `--dangerously-skip-permissions` handed to codex just exits on an unknown argument. Assert the shape
  via `SessionLauncher.ResumeCommand`, which exists so this can be tested without launching anything.
- **`codex` has no name argument at all** — `codex --name x` fails with "unexpected argument"; a thread
  is named from inside the TUI after it starts. So the launcher's Codex row is two buttons where
  Claude's is four, and `NewCodexCommand` takes no name parameter. Don't "fix" the asymmetry by
  inventing a flag.
- **What counts as restorable differs too**: Claude's `Kind == "interactive"`, Codex's `Kind == "tui"`.
  A `codex exec` thread is a headless one-shot from a script or agent — reopening one in a terminal
  restarts somebody's automation instead of restoring work. See `SessionsWindow.IsRestorable`.
- **The orphan check can't use the ownership map.** `ComputeOrphaned` runs at startup *before* the probe
  thread exists, so the map is empty and every live Codex session would be offered for restore while
  it's on screen. `CodexScanner.IsThreadLive(id)` answers for one known id with one Restart Manager
  call instead — bounded enough for the startup path.
- **`SavedSession.Provider` must stay optional.** `active-sessions.json` files written before Codex
  support have no such key; they deserialize to Claude, which is the only thing they could have been.
  It's written as a name, not the enum's number — that file is indented for humans.

## Auto-update / install (`Installer.cs`, `Updater.cs`)
- **Dormant unless installed.** Both no-op unless `Installer.IsInstalledInstance()` (running from
  `%LOCALAPPDATA%\Programs\ClaudeSessionMonitor\`). A `\bin\` dev build never self-installs or
  self-updates, so your local build/verify loop is unaffected.
- **First run** anywhere else copies the exe to the install dir (+ Start Menu shortcut via WScript.Shell
  COM) and relaunches from there. Only correct for the **self-contained single-file** release exe — a
  framework-dependent dev build is multi-file, so a copied single exe would be missing its DLLs.
- **Update:** `gh` compares the latest release tag to the assembly version, downloads the staged exe,
  then Apply renames the running exe → `.old`, copies the new one in, relaunches with `--updated`
  (Program.cs retries the single-instance mutex so the new instance waits for the old to exit).
  Renaming a *running* exe is allowed on Windows (delete isn't) — the same trick Claude Code uses.
- **Rollback:** an `update.pending.json` boot-counter; if a post-update boot never reaches
  `Updater.ConfirmStartupOk()` (window up ~6s) and the app is relaunched, the next boot reverts to `.old`.
- **Update checks fire on launch, every 30 min, and whenever the window is shown.** The window trigger
  is the one that matters in practice — the periodic tick used to be 4 hours, which meant a release cut
  between ticks stayed invisible until a restart, and `DispatcherTimer` doesn't tick while the machine
  sleeps either. All callers go through `Updater.CheckAsync`, which enforces `MinCheckGap` so repeated
  opens can't respawn `gh`. **The show-trigger is gated on `_updaterStarted`**: the first `ShowWindow`
  runs *before* `StartUpdater` subscribes to `UpdateStaged`, so checking there could stage a release
  with nothing listening — and the button would stay hidden, which is the bug this all exists to fix.
- **Test hooks:** `CSM_INSTALL_DIR` (redirect install dir to a temp path), `CSM_NO_INSTALL=1` (skip
  self-install), `CSM_FAKE_UPDATE=<tag>` (force the title-bar button), `CSM_DATA_DIR` (redirect the
  `active-sessions.json` dir — the unit tests set it), `CSM_NO_USAGE_API=1` (force the usage fetch to
  fail, so the cache fallback can be verified without unplugging anything). The full
  download→swap→relaunch cycle can only be truly validated by cutting a real release.
- **Version stamping:** release.yml passes `-p:Version=<tag>` so the running assembly version == the
  release tag; the csproj `<Version>` is only the dev default.
- **Single-file gotcha (this bit us):** dev builds are **framework-dependent** (multi-file); the release
  is **self-contained single-file** — they behave differently at runtime. `Application.Shutdown()` throws
  a `System.Diagnostics.Tracing` `FileNotFoundException` from WPF's shutdown telemetry **only in
  single-file** builds, half-killing the process (mutex still held → update relaunch blocked, rollback
  marker never clears). Fix: `ApplyUpdate` / `ExitApp` dispose the tray + `Environment.Exit(0)` instead
  of `Shutdown()`. Lesson: **test the single-file publish for shutdown/update paths**, not just `dotnet build`.

## Conventions
- **Worktrees only.** Never edit the main checkout. Branch into
  `C:\Data\Repos\.worktrees\ClaudeSessionMonitor\<branch>`, build + verify there, fast-forward merge to
  `main`, push, then remove the worktree.
- **Releases are manual** — Actions → Release → Run workflow (never on push). Ships an unsigned
  self-contained `.exe`.
