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
   "working now"). Deliberately **not** cached: subagents stream while the parent transcript is static.
3. `SessionRow` (observable VM) derives the display **state** from `status` via `SessionStateMap`.
4. `SessionsWindow` binds `ObservableCollection<SessionRow>` to a DataGrid, live-sorted by a
   `ListCollectionView` (SortPriority → `LastChanged` desc → Name).
5. Refresh is **event-driven**: a `FileSystemWatcher` on the sessions dir + a 2s fallback `DispatcherTimer`.
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
  reads the map it produces. Don't move a probe onto the UI thread.
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
- Both sources hit the same parser: the API response body *is* the object the config caches under
  `utilization`, so `AccountScanner.ParseLimits` serves both. Keep it that way.
- Parse the self-describing **`limits` array**, never the `five_hour` / `seven_day_opus` siblings —
  per-model limits appear *only* in `limits` (as `weekly_scoped` + a model scope) while
  `seven_day_opus` and friends sit `null`. Each entry carries its own `severity`, so don't invent
  colour thresholds. The scoped pill is named from `scope.model.display_name` — don't hardcode "Fable".
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
  brush won't re-theme on its own.
- **`ShowInTaskbar=false`** hides the taskbar button via a hidden **owner window** (WPF mechanism), not
  `WS_EX_TOOLWINDOW` — check `GW_OWNER`, not the ex-style, if you're verifying it.
- **`shell` status** = background bash shells running while the agent "holds" idle → it's **Working**,
  not Idle. The ⚙ subagent badge won't fire for it (that counts Task *subagents*, a different thing).
- **`IsAlive`** matches process name `StartsWith("claude")`, because Claude Code's self-update renames
  the running `claude.exe` → `claude.exe.old.<ts>` mid-session.
- Everything here reads **undocumented** Claude Code / Codex files; parsing is isolated in
  `SessionScanner` and `CodexScanner` (+ `VirtualDesktop` for the registry desktop list), so a schema
  change is a one-file fix.
- **Session restore is Claude-only** — it relaunches `claude --resume`, so `Refresh` filters the registry
  snapshot on `Provider == Claude` as well as `Kind == "interactive"`.

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
