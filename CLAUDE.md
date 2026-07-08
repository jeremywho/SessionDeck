# ClaudeSessionMonitor — agent guide

Windows system-tray app (**.NET 10, WPF + [WPF-UI](https://github.com/lepoco/wpfui)/Fluent**) that
lists live local Claude Code sessions, read entirely from `~/.claude/` on disk. `README.md` is the
user-facing feature tour; this file is for working *on* the code.

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

## Status → display state (`SessionState.cs`)
`busy`→Working · `waiting`→Awaiting · `idle`→Completed (green ✓) · `shell`→Working · default→Idle.
**Error** is *not* a status — it's read from the transcript (`isApiErrorMessage`) and sorts to the top.

## Gotchas (don't rediscover these)
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
- Everything here reads **undocumented** Claude Code files; parsing is isolated in `SessionScanner`
  (+ `VirtualDesktop` for the registry desktop list), so a schema change is a one-file fix.

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
  `active-sessions.json` dir — the unit tests set it). The full download→swap→relaunch cycle can
  only be truly validated by cutting a real release.
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
