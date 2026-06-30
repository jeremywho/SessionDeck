# ClaudeSessionMonitor

A Windows tray app that lists every **live local Claude Code session** at a glance, lets you jump to
one, and restores them after a reboot. It reads Claude Code's on-disk state — no Claude configuration is required to *see* sessions
(it uses the `~/.claude/sessions/<pid>.json` live registry).

.NET 10 · WPF + [WPF-UI](https://github.com/lepoco/wpfui) (Fluent / Mica).

## The window
A compact ~500px card, **sorted attention-first** (Error → Awaiting → Working → Completed → Idle)
and re-sorting live as states change:

| Glyph | State | Claude status | Meaning |
|---|---|---|---|
| red disc + ! | **Error** | *(transcript)* | last turn hit an API error (rate-limit, etc.) — hover the row for the message |
| amber pulsing ring | **Awaiting** | `waiting` | blocked on you (permission / input) |
| blue spinner | **Working** | `busy` | actively running |
| green disc + ✓ | **Completed** | `idle` | finished its turn, ready for you |
| slate ring | **Idle** | `shell` | sitting at a shell (rare) |

**Error** is read from the transcript (a synthetic `isApiErrorMessage` turn), not the registry — which
still reports `idle` — and it clears itself when the session's next real turn lands.

Each row shows the session **name**, the **Context %** (colored by fullness: amber > 70, red > 85 —
matching the omc status-line thresholds), and **Idle** time humanized (`42m`, `1h 5m`). **Hover a
row's Idle column** for the rest — model, context tokens, status, last tool, folder, PID/Id/version
(and the API-error text when it's in that state). Sessions whose terminal sits on a **different
virtual desktop** get a small colored pip at the row's left edge — one color per desktop, the
current desktop shows none (hover it for "Desktop N"). A footer shows the live count and a color legend.

The **title bar** holds a **theme toggle** (sun/moon) and an **always-on-top pin** (accents when
active), beside the min/max/close buttons. Dark/Light Fluent theme, remembered.

## Columns
The four columns above are the default. **Right-click any column header** to show/hide extra fields
(raw Status, PID, Id, context tokens, Last Tool, CWD, Version). Column visibility, order, and width
are all remembered.

## Docking
Right-click the tray icon → **Dock to** → an edge or corner, and the window **snaps there on the
monitor it's currently on** (multi-monitor aware). Edges become a full-height sidebar (pair with the
pin to keep it above other windows; drag the inner border to set the width). It's a **one-shot snap** —
the window's position/size is remembered on close, so it reopens where you left it; there's no sticky
"dock mode" or re-snapping. It floats over other windows (doesn't *reserve* space).

## Behavior
- **System tray** — double-click the tray icon to open; closing the window hides it back to the tray.
  Right-click for the (Fluent) menu: **Open**, **Settings…**, **Restore sessions…**, **Toggle theme**,
  **Dock to** (edge/corner), and **Exit**.
- **Double-click a row** → bring that session's terminal window + tab to the foreground. Rows
  highlight on hover (with a hand cursor) to show they're clickable.
- **Ctrl + mouse wheel** zooms the content larger/smaller (like browser zoom) without resizing the
  window; **Ctrl + 0** resets to 100%. Remembered.
- **Change-driven refresh** — a `FileSystemWatcher` on the sessions registry updates the list within
  ~120ms of a status change, with a 2s fallback poll. Event-driven, so idle CPU is ~0 — no hooks, no
  config changes.
- Settings persist to `%APPDATA%\ClaudeSessionMonitor\settings.json`; unhandled errors are logged to
  `%TEMP%\claude-session-monitor-error.log`.
- `--list` / `--windows` headless modes dump the session list / focus mapping to `%TEMP%`.

## Session restore
The app keeps `%APPDATA%\ClaudeSessionMonitor\active-sessions.json` in sync with the live interactive
sessions (snapshot-on-change). If the machine crashes or reboots, that file still holds whatever was
running — so on the next launch the app offers a **restore picker**: a checklist of the sessions that
were running but aren't now, reopening each selected one in a new Windows Terminal window via
`claude --resume <id> --name <name>` in its original folder. Also available any time from the tray
(**Restore sessions…**).

**Settings…** (tray) sets **Resume flags** appended to every resumed session — e.g.
`--dangerously-skip-permissions` — plus the context-window divisor.

## Build / run
Requires .NET SDK 10.

    dotnet build -c Release
    bin\Release\net10.0-windows\ClaudeSessionMonitor.exe

To produce a distributable single `.exe` (self-contained, no .NET install needed, ~74 MB):

    dotnet publish ClaudeSessionMonitor.csproj -c Release -r win-x64 --self-contained true `
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
      -p:EnableCompressionInSingleFile=true -o bin\publish

(Drop `--self-contained true` for a ~6.5 MB build that needs the .NET 10 Desktop Runtime installed.)

## Releasing
Releases are **manual** (never on push). Go to **Actions → Release → Run workflow**, enter a tag
(e.g. `v1.0.0`), and run it — it publishes the self-contained `.exe`, signs it (if a cert is
configured), and attaches it to a new GitHub Release. See `.github/workflows/release.yml`.

**Signing / SmartScreen:** the exe ships **unsigned** until a code-signing cert is wired up. To sign,
add secrets `SIGNING_CERT_BASE64` (base64 of a `.pfx`) and `SIGNING_CERT_PASSWORD` — signing then runs
automatically. To remove the SmartScreen *"unknown publisher"* warning **immediately** you need a
**trusted** cert (EV, Azure Trusted Signing, or one that already has reputation); a plain OV cert signs
the file but may still warn until reputation builds. For a cloud/HSM cert (Azure Trusted Signing,
DigiCert KeyLocker), swap the workflow's *Sign the exe* step for that provider's action.

## How it works
- **Discovery:** `~/.claude/sessions/<PID>.json` — one heartbeat file per live session, validated
  against a live process whose name starts with `claude` (tolerates Claude Code's self-update renaming
  the running `claude.exe` → `claude.exe.old.<ts>`, while still guarding against PID reuse). `status`
  is one of `busy`, `idle`, `waiting`, `shell`.
- **Detail:** tails `~/.claude/projects/<slug>/<sessionId>.jsonl` for token usage, model, last tool,
  and the Claude-set title — but only re-reads a transcript when its mtime/size changed since the last
  scan (an unchanged, idle session costs just a `stat`).
- **Refresh:** a `FileSystemWatcher` on `~/.claude/sessions/` fires when Claude rewrites a `<pid>.json`
  (status change / heartbeat) → a debounced re-scan; a 2s timer is the fallback. Event-driven, so idle
  CPU is ~0, with no hooks or edits to the user's files.
- **Focus:** finds the session's host process (parent-process-tree walk), enumerates that process's
  top-level windows, then uses UI Automation to find the *tab* whose title matches the session —
  across all those windows — selects that tab and foregrounds its window. Needed because Windows
  Terminal spreads tabs across several windows under one process, so `Process.MainWindowHandle` can't
  identify the right one. (`--windows` dumps the mapping.)

## Project layout
- `Program.cs` — entry point (single-instance; `--list` / `--windows` headless modes).
- `App.cs` — WPF application shell, the (WPF) tray icon + menu, and the theme-palette swap.
- `SessionsWindow.xaml` / `.xaml.cs` — the Fluent window (status-glyph template, grid, title-bar
  controls, live sort, header-right-click column menu).
- `SessionScanner.cs` — reads the registry and enriches each session from its transcript (mtime-cached).
- `SessionInfo.cs` — data model. `SessionRow.cs` — observable row VM (derives the display state).
- `SessionState.cs` — the display states (incl. **Error**) + the Claude-status → state mapping.
- `WindowActivator.cs` / `Native.cs` / `TabSelector.cs` — focus + Windows Terminal tab selection.
- `Themes/Dark.xaml`, `Themes/Light.xaml` — design-token brushes, swapped on theme toggle.
- `SessionRegistry.cs` / `SavedSession.cs` — persist the live set for crash/reboot restore.
- `SessionLauncher.cs` — reopen a session (`wt … claude --resume …`).
- `RestoreWindow.xaml` / `.xaml.cs` — restore picker. `SettingsWindow.xaml` / `.xaml.cs` — settings dialog.
- `app.ico` — coral starburst app/tray icon.
- `Settings.cs`, `Converters.cs`.

## Known limitations
- **`idle` shows as green "Completed".** Claude has no "done" status — an idle session has finished
  its turn and is ready for you, so it gets the green glyph. The pulsing **amber Awaiting** is the
  one that means "blocked on you."
- **Focus matches by tab title** — relies on each session having a distinct title (`/rename` or
  Claude's auto-title). Identical/empty titles may be ambiguous; it then falls back to a window-title
  match and refuses to focus the wrong window rather than guess.
- **Context %** is approximate: the true per-session context-window size is only handed to Claude
  Code *statusline* commands, not to a standalone app — so the divisor is a setting
  (`ContextWindowTokens`, default 1,000,000).
- Reads undocumented internal files; the schema may change between Claude Code versions. Parsing is
  isolated in `SessionScanner`, so a schema change is a one-file fix.

## Ideas / next
- AppBar docking (reserve screen space, taskbar-style) instead of floating.
- Codex support (`~/.codex`).
- Quick filter box; cumulative token totals; run on login.
