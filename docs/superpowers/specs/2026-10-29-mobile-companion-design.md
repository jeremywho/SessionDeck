# Mobile companion (iOS) — design

Date: 2026-10-29. Branch: `mobile`. Status: approved by Jeremy 2026-10-29 ("begin implementation and keep working without me"); amended after Codex review round 1 (WebSocket auth, Codex probe warm-up, pre-protocol-4 hosts, firewall rule).

## Goal

An iPhone app that lists every session the deck knows about, with the same state each row
shows on the desktop, and opens any one of them to read its terminal and type into it, so a
session that is waiting for a word from Jeremy can be kept moving from the couch. Local network
only for now; a Cloudflare tunnel comes later and must not require redesign.

## Decisions (Jeremy, 2026-10-29)

- **Native SwiftUI app.** The session list is native. The terminal is a `WKWebView` running the
  same xterm.js the deck already ships, so there is one terminal emulator and one attach protocol.
- **Works with the desktop deck window closed.** Hosts already outlive the deck; a new always-on
  gateway process makes them reachable.
- **Interaction is "keep the session working", not a full terminal.** A text box with the normal
  iOS keyboard that types a line and presses Enter, plus a key bar for what a prompt needs: Esc,
  Enter, Tab, Shift+Tab, arrows, Ctrl+C. Copy and paste both ways.
- **The phone never resizes the pty** (my call, not contested). Resizing to a phone's width would
  redraw the desktop pane every time a session is opened. The phone renders the host's current
  size and scales it to fit, with pinch-zoom and pan.

## Non-goals (this version)

- Starting, restarting or closing sessions from the phone. Open-and-type only.
- Push notifications. The list updates live while the app is open; nothing arrives when it is not.
- Remote access, TLS, accounts. The gateway speaks plain HTTP and WebSocket on the LAN with bearer
  tokens; the tunnel will terminate TLS later. This means a paired device's token crosses the home
  LAN in the clear; acceptable for v1 and said out loud.
- Bonjour discovery. The QR code carries the address; the phone also accepts a typed one.
- Image paste (Claude's Alt+V), mouse reporting, bracketed paste nuance beyond plain text.
- iPad layout. It runs on iPad as a phone app; nothing is tuned for it.

## Anti-goals

- Two implementations of "what state is this session in". The gateway and the window must share
  one catalog, or the phone will disagree with the desktop within a week.
- A second writer of `settings.json`. The deck owns it; the gateway reads it.
- Anything that sends input to a host the person did not open on the phone.

## Shape

```
iPhone (SwiftUI)  ──HTTP/WS over LAN──▶  SessionDeck.exe --gateway (Talon)  ──loopback WS──▶  SessionDeck.exe --host (one per session)
                                                   │ reads
                                                   ▼
                                  %APPDATA%\SessionDeck\{hosts\*.json, settings.json}
                                  ~/.claude/sessions, ~/.codex, ~/.gemini/antigravity-cli
```

### The catalog (shared, headless)

Today the list is built inside `SessionsWindow.RefreshAsync`/`ApplyRefresh`: scan Claude, Codex,
processes, fold headless Codex onto owners, discover hosts, pair hosts to sessions
(`HostPairing`), apply hook state (`ApplyHookState`), apply agy names (`ApplyAgyNames`), then
rows compute `State`, badges, `IsUnread`, grouping and order from `Settings`.

Extract the non-WPF part into `SessionCatalog` (new, in the main project, no `System.Windows`
references): one `Build(Settings settings)` that returns the list of catalog entries, each with
host id, session id, provider, display name, model chip, effort, state, unread-since time
(`StateSince`), subagent/background counts, group name, cwd, host port and token (never
serialised to the phone), and sort key per `Settings.RowOrder`. The window keeps `SessionRow`
objects and WPF concerns (succession, held priority, live sort pause, read ledger seeding) but
feeds them from the catalog instead of its own copy of the pipeline. The `--list` diagnostic mode
switches to the catalog too, so three callers prove it is the one truth.

Unread on the phone is the phone's own: the app keeps a per-session "last viewed" time and shows
a session as unread when its `StateSince` is later than that and its state is Completed. The deck's
`ReadAt` is untouched. Viewing on the phone does not mark it read on the desktop and vice versa;
cross-device read sync is a later item once there is a second writer story.

### The gateway: `SessionDeck.exe --gateway`

- One process, single-instance mutex of its own, started by the installed deck at launch and by
  the HKCU Run entry when `Settings.RunOnLogin` is set, so it is up after a reboot before the deck
  window is. Runs from a stamped copy under `host-bin\` like hosts do, so an app update never finds
  the gateway's exe in use; the deck tells a running gateway to hand off after an update the way
  the deck itself restarts (the new gateway starts, the old one exits once the new one listens).
- Binds `0.0.0.0:<Settings.Gateway.Port>` (default 7420). The firewall rule is by port, private
  profile only, created from a button in Settings that runs `netsh` elevated once (the installer
  is not elevated, and a rule by exe path would break at every update since the gateway runs from
  a stamped copy).
- Rebuilds the catalog every 5 s while a device is connected, every 30 s otherwise, and pushes
  the list to connected devices over `/events` when it changed. Each cycle first runs
  `CodexScanner.Probe()`, the rollout-to-process map the window keeps warm on its own thread today
  (`SessionsWindow.StartCodexProbe`); without it `CodexScanner.Scan()` returns nothing. The
  catalog owns that warm-up for every headless caller, `--list` included.
- Reads host records for ports and tokens; it is the only thing besides the deck that does.

HTTP API (JSON). Authentication: `Authorization: Bearer <device token>` on HTTP requests. A
WebSocket opened from a web view cannot carry a header, so WebSocket upgrades take the token in
the query (`?token=`), exactly as the deck's own page does with hosts today, and must carry either
no `Origin` (a native client) or an `Origin` equal to the gateway's own `http://host:port`; any
other origin is refused before the upgrade. The terminal page receives the token from the native
shell, never from a URL the user sees.

| Route | Purpose |
|---|---|
| `POST /pair` `{code, deviceName}` | One-time pairing code from the QR → `{token, deviceId}`. Code valid 5 min, single use. |
| `GET /sessions` | The catalog as the phone sees it (no ports, no host tokens). |
| `WS /events` | Pushes `{type:"sessions", sessions:[…]}` on every catalog change. |
| `WS /sessions/{hostId}/attach?after=<seq>` | Relays the host's attach stream both ways. |
| `GET /mobile/mobile.html`, `/mobile/*.js`, `/mobile/*.css` | The terminal page and xterm assets, from the embedded `wwwroot`. |
| `DELETE /devices/{id}` | Revoke (used by the deck's settings page, same token rules). |

Attach relay rules, enforced in the gateway, not trusted to the phone:
- Phone → host: only `{type:"input"}` is forwarded. `resize` and `kill` are dropped.
- Host → phone: everything, plus one addition the host needs anyway: the host's `snapshot` message
  gains `cols` and `rows`, and a `{type:"size", cols, rows}` text frame is broadcast when the pty is
  resized (host `Protocol` 4). The phone sizes its xterm from these.
- Reconnect uses the host's existing `after=<seq>` so a backgrounded phone catches up without a
  reset when it comes back.
- Hosts started before this version run protocol 3 from their stamped binaries for as long as
  they live and cannot report a size. The catalog marks each entry `mobileReady` (protocol ≥ 4
  and not exited); the phone lists the others greyed with "restart from the desktop to open here",
  and the gateway answers their attach requests with 409 instead of guessing a size.

Pairing UI on the desktop: Settings → "Phone" section shows a QR code (address, port, pairing
code) generated on demand, lists paired devices with last-seen time, and has Revoke per device.
Paired devices live in `%APPDATA%\SessionDeck\devices.json`, written only by the gateway; the
settings page calls the gateway to list and revoke.

### The iOS app

- Swift 6, SwiftUI, iOS 18 minimum. Project at `ios/SessionDeckMobile/` in this repo. Bundle id
  `com.jeremywho.sessiondeck`, dev-signed on team `PCWH4GSLHZ`, built on the Mac Studio over SSH
  with `xcodebuild` and installed with `xcrun devicectl` (the Konduit iOS recipe). `ios/build.sh`
  wraps build, install and launch for the iPhone (iPhone17,2).
- `Info.plist`: `NSLocalNetworkUsageDescription` and ATS `NSAllowsLocalNetworking` so plain
  `http://` and `ws://` to a LAN address are allowed; nothing else is exempted.
- Screens:
  1. **Pair** (first run, or after a revoke): scan QR with the camera, or type `host:port` and
     code. Stores the token in the Keychain.
  2. **Sessions**: grouped list mirroring the deck's groups and order. Each row: provider glyph
     (✳ ◆ ◈), name, model chip, state dot with the deck's colours (Working, Awaiting, Scheduled,
     Completed, Idle, Error), unread tint, subagent badge. Live via `/events`; pull to refresh.
     A banner when the gateway is unreachable.
  3. **Session**: `WKWebView` filling the screen above an input area, loading
     `/mobile/mobile.html?host=<id>` from the gateway. The page opens the attach relay, sets xterm
     to the host's `cols`×`rows`, scales the terminal element so the full width fits the screen,
     and supports pinch-zoom and two-finger pan. Below it: a native text field (autocorrect and
     smart punctuation off, so `/` and quotes arrive as typed) with Send, and a key bar: Esc, Tab,
     ⇧Tab, ↑ ↓ ← →, ^C, Enter, Paste, Copy. Send types the text and `\r`. Paste types the
     clipboard. Copy enters a selection mode in xterm; the selection goes to the pasteboard via
     a `WKScriptMessageHandler`. Haptic tick on each key.
- Input bytes: Esc `\x1b`, Tab `\t`, Shift+Tab `\x1b[Z`, arrows `\x1b[A/B/C/D`, Ctrl+C `\x03`,
  Enter `\r`. Text is sent as one `input` message so a slash command arrives whole.
- The app marks a session viewed when its screen appears and again when it disappears.

## Constraints

- No change to how hosts are started, named, resumed or killed. The host gains only protocol 4
  (`cols`/`rows` in `snapshot`, `size` frames).
- The gateway must never write `settings.json`, host records, or the registry.
- Hooks, scanners and pairing logic stay where they are; the catalog calls them.
- Tests: `SessionCatalog` gets tests proving it matches what the window shows for the existing
  fixtures (states, unread timing, grouping). Gateway routes get tests against an in-process
  listener with a fake host. The relay's input-only rule gets a test that sends `resize` and
  `kill` and asserts the fake host never sees them.
- All rehearsal against hosts and data under `SD_DATA_DIR`, never the live deck.

## Later (explicitly deferred, in likely order)

1. Cloudflare tunnel: the gateway behind `cloudflared` with Access; the app gets a second address
   and tries LAN first. TLS comes from the tunnel.
2. Push notifications when a session completes or awaits input (needs the tunnel or a relay).
3. Read-state sync between phone and desktop.
4. New session / restart / close from the phone.
5. Bonjour discovery.

## Left to the builder

- Exact JSON shape of catalog entries and the events diff.
- Pairing code format and QR payload encoding.
- How the gateway hands off across an update (mirror the deck's own restart handoff).
- xterm scaling implementation (CSS transform vs. font-size fit) and selection mode UX.
- Whether the iOS project is generated (XcodeGen) or a checked-in `.xcodeproj`.
