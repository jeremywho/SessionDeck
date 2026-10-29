# Mobile companion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An iPhone app that lists the deck's sessions with their live state and opens any one to read its terminal and type into it, over the LAN, whether or not the desktop deck window is open.

**Architecture:** The list-building pipeline is pulled out of `SessionsWindow` into a headless `SessionPipeline` + `SessionCatalog` that the window, the `--list` diagnostic and a new `SessionDeck.exe --gateway` process all call. The gateway is a TcpListener HTTP/WebSocket server (the same shape as the host's own server) that serves the catalog, pushes changes, relays a host's attach stream with input-only filtering, serves a mobile terminal page from the embedded `wwwroot`, and holds device pairings. The iOS app is SwiftUI with a `WKWebView` running that page.

**Tech Stack:** .NET 10 WPF (existing), `System.Net.WebSockets` over `TcpListener` (existing pattern in `Host/HostProgram.cs`), xUnit 2.9, QRCoder (NuGet, MIT) for the pairing QR; Swift 6 / SwiftUI / iOS 18, XcodeGen, built on `macstudio.local` (Xcode 26.6, iPhone `iPhone17,2` paired).

**Spec:** `docs/superpowers/specs/2026-10-29-mobile-companion-design.md` (read it before starting any task).

## Global Constraints

- Repo is **public**: no private names, addresses or tokens in committed files. Fixtures use `acme/...`, `192.0.2.x`, `example` tokens.
- Line endings: CRLF for `*.cs`, `*.xaml`, `*.csproj`, `*.md`; LF for `*.swift`, `*.yml`, `*.sh` (they run on the Mac); `wwwroot/**` is `-text` and new files there are CRLF like `deck.html`. Verify every written file: `tr -dc '\r' < F | wc -c` equals `tr -dc '\n' < F | wc -c`.
- Comments: none unless the code cannot say it. One line.
- The gateway never writes `settings.json`, host records, or `active-sessions.json`. Its only files are `gateway.json` and `devices.json` under `Settings.DataDir`, written with `AtomicFile.Write`.
- The gateway forwards exactly one message type from a phone to a host: `{"type":"input"}`. `resize` and `kill` are dropped.
- Catalog entries sent to a device never contain a host `Port` or `Token`.
- Phone never resizes a pty (host protocol 4 only adds `cols`/`rows` to `snapshot` and a `size` frame).
- Hosts under test and all rehearsal use `SD_DATA_DIR`; never the live `%APPDATA%\SessionDeck`; never stop, restart or send input to a live deck host.
- Build: `dotnet build D:\Data\Repos\.worktrees\SessionDeck\spike\SessionDeck.csproj -c Release`. Tests: `dotnet test D:\Data\Repos\.worktrees\SessionDeck\spike\Tests\SessionDeck.Tests.csproj -c Release`. Use the `D:` path with Codex (`C:\Data\Repos` is a junction).
- Commit messages: plain prose subject, body explaining why, ending `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`. Push to `main` and release only at the end (Task 13).
- Each task's commit gets a blockers-only Codex review (`codex exec -C D:/Data/Repos/.worktrees/SessionDeck/spike -s workspace-write -c model_reasoning_effort='"xhigh"'`, brief in `~/.claude/mobile-review/`, 2 rounds max) before the next task starts.

## Review Focus

1. **A phone that sends `resize` or `kill` on the attach socket** — the host never sees it and the socket stays open. Pinned by Task 6 (`Only_input_reaches_the_host`).
2. **A phone reconnecting after being backgrounded** — the host's `after=<seq>` is passed through and the terminal resumes without a reset. Pinned by Task 6 (`The_after_sequence_is_passed_to_the_host`).
3. **A revoked or unknown token** — 401 on HTTP, close code 4401 on WebSocket upgrade, nothing relayed. Pinned by Task 5 (`Unknown_tokens_are_refused_everywhere`).
4. **The deck window is closed, or a newer deck starts while the gateway runs** — the gateway stays up; a newer deck replaces an older gateway without two listening at once. Pinned by Task 8 (`A_newer_deck_replaces_an_older_gateway`).
5. **A session whose host exited, or a Shell pane** — listed with state Idle (exited) or shown without a state, never crashes the catalog. Pinned by Task 3 (`Exited_hosts_and_shell_panes_are_entries_without_surprises`).

## File Structure

| File | Status | Responsibility |
|---|---|---|
| `Host/HostProgram.cs` | modify | Protocol 4: `cols`/`rows` in `snapshot`, broadcast `size` on resize, remember the current size. |
| `Host/Hooks.cs` | — | unchanged |
| `SessionPipeline.cs` | create | Headless scan + pairing: `Scan()` → `ScanResult`; `Pair(ScanResult)` → paired hosts with hook state and agy names applied. Moves `ApplyHookState`, `ApplyAgyNames` (minus tab rename) and `DescendsFrom` out of the window. |
| `SessionCatalog.cs` | create | `Build(pairs, settings, now)` → `CatalogEntry[]` ordered as the deck shows them; `CatalogEntry` DTO. |
| `SessionsWindow.xaml.cs` | modify | `RefreshAsync`/`ApplyRefresh` call the pipeline; tab renames from `info.Name`. |
| `Program.cs` | modify | `--list` uses the pipeline; `--gateway` entry. |
| `Gateway/MiniHttp.cs` | create | Request head parsing, response writers, WebSocket upgrade; lifted from `HostProgram` so both use one copy. |
| `Gateway/DeviceStore.cs` | create | `devices.json`: paired devices, tokens, last seen; pairing codes. |
| `Gateway/GatewayServer.cs` | create | Routes, auth, catalog loop, `/events`, static `/mobile/*`. |
| `Gateway/AttachRelay.cs` | create | Phone↔host WebSocket relay with the input-only rule. |
| `Gateway/GatewayProcess.cs` | create | `gateway.json`, single instance, `EnsureRunning()` from the deck, replace-on-update. |
| `Settings.cs` | modify | `GatewaySettings Gateway { Enabled, Port }`. |
| `SettingsWindow.xaml(.cs)` | modify | "Phone" section: QR, devices, revoke, firewall button. |
| `wwwroot/mobile.html`, `wwwroot/mobile.js` | create | Terminal page for the phone. |
| `ios/project.yml`, `ios/SessionDeckMobile/**`, `ios/build.sh` | create | The iOS app. |
| `Tests/HostAttachTests.cs`, `Tests/SessionPipelineTests.cs`, `Tests/SessionCatalogTests.cs`, `Tests/DeviceStoreTests.cs`, `Tests/GatewayServerTests.cs`, `Tests/AttachRelayTests.cs`, `Tests/GatewayProcessTests.cs`, `Tests/FakeHost.cs` | create | Tests; `FakeHost` is an in-process stand-in for a host's attach server. |

---

### Task 1: Host protocol 4 — the pty size travels with the stream

**Files:**
- Modify: `Host/HostProgram.cs` (`HandleMessage` `resize` case, `ServeClient` snapshot, `Serve` record `Protocol = 3` → 4)
- Modify: `Host/ConPty.cs` if it does not expose the current size (add `Cols`/`Rows` properties set in the constructor and `Resize`)
- Test: `Tests/HostRecordWriteTests.cs` (append)

**Interfaces:**
- Produces: `HostProgram.SnapshotJson(long start, long end, string title, int cols, int rows)` → `string`; `HostProgram.SizeJson(int cols, int rows)` → `string` (`{"type":"size","cols":120,"rows":30}`); `HostRecord.Protocol == 4` for new hosts.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void A_snapshot_carries_the_pty_size_and_a_resize_announces_the_new_one()
{
    using var snap = JsonDocument.Parse(HostProgram.SnapshotJson(10, 20, "t", 132, 40));
    Assert.Equal("snapshot", snap.RootElement.GetProperty("type").GetString());
    Assert.Equal(132, snap.RootElement.GetProperty("cols").GetInt32());
    Assert.Equal(40, snap.RootElement.GetProperty("rows").GetInt32());
    Assert.Equal("{\"type\":\"size\",\"cols\":80,\"rows\":24}", HostProgram.SizeJson(80, 24));
}
```

- [ ] **Step 2: Run to verify it fails** — `dotnet test ... --filter A_snapshot_carries` → FAIL (no such members).
- [ ] **Step 3: Implement** the two builders; use them in `ServeClient` and in the `resize` case (`Broadcast(true, SizeJson(...))` after `_pty.Resize`); track size in `ConPty`; set `Protocol = 4`. `deck.html` ignores unknown text frames already (no change).
- [ ] **Step 4: Run all tests** → PASS. Build Release.
- [ ] **Step 5: Integration test** `Tests/HostAttachTests.cs`: start the built `SessionDeck.exe --host` (it sits next to the test assembly) with a spec on stdin whose `HostsDir` is a temp folder and whose command is `pwsh -NoLogo -Command "Start-Sleep 20"`; attach with `ClientWebSocket`; assert the first text frame has `cols`/`rows`; send `resize`; assert a `size` frame arrives; then `kill`.
- [ ] **Step 6: Commit** — "A host tells its viewers the pty size".

### Task 2: `SessionPipeline` — one headless scan-and-pair for every caller

**Files:**
- Create: `SessionPipeline.cs`
- Modify: `SessionsWindow.xaml.cs` (`RefreshAsync` scan block → `SessionPipeline.Scan()`; `ApplyRefresh` first three lines → `SessionPipeline.Pair(scan)`; delete `ApplyHookState`, `ApplyAgyNames`, `DescendsFrom`; after pairing, for each agy pair with `info.Name.Length > 0`, `Deck.FindByHost(h.Id)?.View.Rename(info.Name)`)
- Modify: `Program.cs` `--list` → `SessionPipeline.WarmUp()` then `SessionPipeline.Pair(SessionPipeline.Scan())`, printing the same columns plus host id.
- Test: `Tests/SessionPipelineTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  internal sealed record ScanResult(List<SessionInfo> Sessions, List<Host.HostRecord> Hosts, Dictionary<int,int> Parents,
                                    List<SessionInfo> Unowned, int ClaudeDiscovered, int CodexDiscovered, long ElapsedMs);
  internal static class SessionPipeline
  {
      public static ScanResult Scan(bool prune = true);                  // off-dispatcher safe; logs session-scan like today; prune=false (gateway) never deletes a host record
      public static void WarmUp();                                       // CodexScanner.Probe() four times 3 s apart, as --list does today; headless callers run it once before their first Scan and Probe() once per cycle after
      public static List<(Host.HostRecord Host, SessionInfo Info)> Pair(ScanResult scan, HashSet<SessionInfo>? claimed = null);
      internal static void ApplyHookState(Host.HostRecord h, SessionInfo info);          // moved verbatim
      internal static void ApplyAgyNames(List<(Host.HostRecord, SessionInfo)> byHost, IReadOnlyDictionary<int,(int Parent,string Name)> table);
      internal static bool DescendsFrom(int pid, int ancestor, Dictionary<int,int> parents);
  }
  ```
- Consumes: `HostPairing.Pair`, `SessionScanner.Scan`, `CodexScanner.Scan`, `Native.BuildParentMap`, `CodexAttribution.Fold`, `HostManager.Discover(bool prune)` (new overload: `prune=false` skips every `File.Delete`), `AgyScanner`.
- Also produces `Settings.Peek()`: loads `settings.json` without `ApplyNewDefaults`/`Save` and without `AtomicFile.Read`'s set-aside rename (a plain parse of the file, then of `.bak`, null → `new Settings()`). The gateway only ever calls `Peek()` and `Scan(prune: false)`; tests `Peek_never_writes` (a settings file with `FlagsVersion = 0` is byte-identical after `Peek`) and `Discover_without_prune_leaves_dead_records` (a record whose pid is dead stays on disk).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Pairing_applies_the_hosts_hook_state_to_the_scanned_session()
{
    var host = new HostRecord { Id = "h", SessionId = "s1", Provider = "Claude", ChildPid = 5, AgentStatus = "busy", LastEvent = "PreToolUse", LastTool = "Bash", StatusAt = DateTime.UtcNow, Protocol = 4 };
    var info = new SessionInfo { SessionId = "s1", Pid = 7, Status = "idle", Kind = "interactive", StatusUpdatedAt = DateTime.Now.AddMinutes(-5) };
    var scan = new ScanResult(new() { info }, new() { host }, new(), new(), 1, 0, 0);
    var pairs = SessionPipeline.Pair(scan);
    Assert.Equal("busy", pairs.Single().Info.Status);
    Assert.Equal("Bash", pairs.Single().Info.LastTool);
}

[Fact]
public void A_host_with_no_scanned_session_gets_a_placeholder_of_its_own_provider()
{
    var host = new HostRecord { Id = "h", Provider = "Agy", ChildPid = 5 };
    var pairs = SessionPipeline.Pair(new ScanResult(new(), new() { host }, new(), new(), 0, 0, 0));
    Assert.Equal(SessionProvider.Agy, pairs.Single().Info.Provider);
}
```

- [ ] **Step 2: Run to verify they fail** → FAIL (type missing).
- [ ] **Step 3: Implement** by moving code, not rewriting: the scan block from `RefreshAsync` (keep its `PerformanceLog`/`DwmDiagnostics` lines; the window's `_lastScanMs`/`NoteScan` take `ElapsedMs` from the result), and the three statics from the window. `Pair` = `HostPairing.Pair(hosts, sessions, (s,h) => DescendsFrom(s.Pid, h.ChildPid, parents), claimed ?? new())`, then `ApplyHookState` per pair, then `ApplyAgyNames(pairs, SessionScanner.ProcessTable)`.
- [ ] **Step 4: Run all tests** → PASS (existing `StaleHostTests`, `AgyHooksTests` still green). Build Release.
- [ ] **Step 5: Rehearse** `SessionDeck.exe --list` (reads live state read-only, writes only `%TEMP%\sessiondeck-dump.txt`): every live deck row appears with its host id and state.
- [ ] **Step 6: Commit** — "The session list is built by one pipeline the window and the diagnostic share".

### Task 3: `SessionCatalog` — the list as a device sees it

**Files:**
- Create: `SessionCatalog.cs`
- Test: `Tests/SessionCatalogTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  internal sealed record CatalogEntry(string HostId, string SessionId, string Provider, string Name, string ModelChip, string Effort,
      string State, long StateSinceMs, int Subagents, string Group, int GroupOrder, int SortPriority, string Cwd, bool HostExited, bool MobileReady);
  // MobileReady = host.Protocol >= 4 && !host.HasExited (a protocol-3 host cannot report its pty size)
  internal sealed class SessionCatalog
  {
      public List<CatalogEntry> Build(List<(Host.HostRecord Host, SessionInfo Info)> pairs, Settings settings, DateTime nowUtc);
      public static string ToJson(IReadOnlyList<CatalogEntry> entries);   // camelCase, no indentation
  }
  ```
  The catalog is an instance that keeps one `SessionRow` per host id across builds and calls `row.Update(info, host)`, so `StateSince` keeps the turn's end through Claude's idle notification a minute later (the regression `ReadLedgerTests` pins for the window). Rows whose host is gone from `pairs` are dropped.
  `State` is `SessionState` name (`Working|Awaiting|Scheduled|Completed|Idle|Error`). Order = `SessionsWindow.SortedRows` rule: `GroupOrder`, then `HostExited` when `settings.RowOrder == Settings.RowOrderName` else `SortPriority`, then `Name` (`StringComparer.CurrentCulture`).
- Consumes: `SessionRow(host, info)` for `State`, `StateSince`, `Name`, `ModelChip`, `Effort`, `SubagentsActive`, `SortPriority`; `SessionGroup.Apply` on a **deep copy** of `settings.SessionGroups` (the gateway must not mutate or save the deck's groups).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Entries_carry_state_group_and_order_and_never_a_port_or_token()
{
    var settings = new Settings { RowOrder = Settings.RowOrderState, SessionGroups = new() { new SessionGroup { Name = "tr", Members = new() { "s2" } } } };
    var a = (new HostRecord { Id = "h1", SessionId = "s1", Provider = "Claude", Port = 5555, Token = "SECRET", AgentStatus = "busy", StatusAt = DateTime.UtcNow, Protocol = 4, Title = "alpha" },
             new SessionInfo { SessionId = "s1", Status = "busy", Kind = "interactive", Model = "claude-opus-5" });
    var b = (new HostRecord { Id = "h2", SessionId = "s2", Provider = "Codex", Port = 5556, Token = "SECRET2", AgentStatus = "idle", StatusAt = DateTime.UtcNow, Protocol = 4, Title = "beta" },
             new SessionInfo { SessionId = "s2", Status = "idle", Kind = "tui", Provider = SessionProvider.Codex, Model = "gpt-5.6-sol" });
    var entries = SessionCatalog.Build(new() { a, b }, settings, DateTime.UtcNow);
    Assert.Equal(new[] { "h1", "h2" }, entries.Select(e => e.HostId));     // ungrouped (order 0) before group "tr" (order 1)
    Assert.Equal("Working", entries[0].State);
    Assert.Equal("Opus 5", entries[0].ModelChip);
    Assert.Equal("tr", entries[1].Group);
    string json = SessionCatalog.ToJson(entries);
    Assert.DoesNotContain("SECRET", json);
    Assert.DoesNotContain("5555", json);
    Assert.Contains("\"hostId\":\"h1\"", json);
}

[Fact]
public void Exited_hosts_and_shell_panes_are_entries_without_surprises()
{
    var exited = (new HostRecord { Id = "h3", SessionId = "s3", Provider = "Claude", ExitCode = 0, ExitedAt = DateTime.UtcNow }, new SessionInfo { SessionId = "s3", Kind = "interactive" });
    var shell = (new HostRecord { Id = "h4", Provider = "Shell", Title = "Terminal" }, SessionRow.Placeholder(new HostRecord { Id = "h4", Provider = "Shell", Title = "Terminal" }));
    var entries = SessionCatalog.Build(new() { exited, shell }, new Settings(), DateTime.UtcNow);
    Assert.Equal("Idle", entries.Single(e => e.HostId == "h3").State);
    Assert.True(entries.Single(e => e.HostId == "h3").HostExited);
    Assert.False(entries.Single(e => e.HostId == "h3").MobileReady);
    Assert.Equal("Terminal", entries.Single(e => e.HostId == "h4").Name);
}

[Fact]
public void Only_hosts_that_report_their_size_are_mobile_ready()
{
    var old = (new HostRecord { Id = "h5", SessionId = "s5", Provider = "Claude", Protocol = 3 }, new SessionInfo { SessionId = "s5", Kind = "interactive" });
    var fresh = (new HostRecord { Id = "h6", SessionId = "s6", Provider = "Claude", Protocol = 4 }, new SessionInfo { SessionId = "s6", Kind = "interactive" });
    var entries = SessionCatalog.Build(new() { old, fresh }, new Settings(), DateTime.UtcNow);
    Assert.False(entries.Single(e => e.HostId == "h5").MobileReady);
    Assert.True(entries.Single(e => e.HostId == "h6").MobileReady);
}
```

- [ ] **Step 2: Run to verify they fail** → FAIL.
- [ ] **Step 3: Implement** `Build`: for each pair, reuse the cached row (`row.Update(info, host)`) or create one; drop rows for hosts not in `pairs`; `SessionGroup.Apply(copyOfGroups, rows)`; map to entries; sort by the rule above. `ToJson` with `JsonSerializerDefaults.Web`. Add the test `A_rebuilt_catalog_keeps_the_time_a_session_completed` (build at t0 with a completed row, build again at t0+70 s with the same status and a later `StatusUpdatedAt` → `StateSinceMs` unchanged).
- [ ] **Step 4: Run all tests** → PASS.
- [ ] **Step 5: Commit** — "A catalog of the session list that carries no secrets".

### Task 4: `DeviceStore` — pairings, tokens, codes

**Files:**
- Create: `Gateway/DeviceStore.cs`
- Test: `Tests/DeviceStoreTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  internal sealed record PairedDevice(string Id, string Name, string TokenHash, DateTime PairedAtUtc, DateTime LastSeenUtc);
  internal sealed class DeviceStore
  {
      public DeviceStore(string path);                         // Settings.DataDir\devices.json
      public string IssuePairingCode(DateTime nowUtc);          // 8 chars from "ABCDEFGHJKLMNPQRSTUVWXYZ23456789", valid 5 min, single use; issuing again replaces the previous code
      public (string Token, PairedDevice Device)? Redeem(string code, string deviceName, DateTime nowUtc);   // token = 32 random bytes as lowercase hex; stored as SHA-256 hex
      public PairedDevice? Authenticate(string token, DateTime nowUtc);   // updates LastSeen (persisted at most once a minute)
      public bool Revoke(string deviceId);
      public IReadOnlyList<PairedDevice> Devices { get; }
  }
  ```

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void A_code_pairs_once_within_five_minutes_and_the_token_authenticates()
{
    var store = new DeviceStore(Path.Combine(_dir, "devices.json"));
    var t0 = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
    string code = store.IssuePairingCode(t0);
    Assert.Equal(8, code.Length);
    var paired = store.Redeem(code, "Jeremy's iPhone", t0.AddMinutes(1));
    Assert.NotNull(paired);
    Assert.Null(store.Redeem(code, "again", t0.AddMinutes(2)));
    Assert.Equal(paired!.Value.Device.Id, store.Authenticate(paired.Value.Token, t0.AddMinutes(3))!.Id);
    Assert.Null(store.Authenticate("nope", t0));
    Assert.True(store.Revoke(paired.Value.Device.Id));
    Assert.Null(store.Authenticate(paired.Value.Token, t0.AddMinutes(4)));
}
[Fact] public void An_expired_code_is_refused_and_the_store_survives_a_reload()
{ /* Issue at t0, Redeem at t0+6min → null; Issue+Redeem, new DeviceStore(path).Authenticate(token) → device */ }
[Fact] public void The_file_holds_hashes_not_tokens()
{ /* after Redeem, File.ReadAllText(path) does not contain the token */ }
```

- [ ] **Step 2: Run to verify they fail** → FAIL.
- [ ] **Step 3: Implement** with `AtomicFile.Write`, `RandomNumberGenerator`, `SHA256`.
- [ ] **Step 4: Run all tests** → PASS.
- [ ] **Step 5: Commit** — "Paired phones and their one-time codes".

### Task 5: `MiniHttp` + `GatewayServer` — routes, auth, catalog, events, static page

**Files:**
- Create: `Gateway/MiniHttp.cs` (move `ReadHttpHead`, `ParseQuery`, `WriteAscii`, the `Sec-WebSocket-Accept` computation out of `HostProgram.cs`; `HostProgram` calls them)
- Create: `Gateway/GatewayServer.cs`
- Modify: `Settings.cs` — `public GatewaySettings Gateway { get; set; } = new();` with `bool Enabled = true; int Port = 7420;`
- Test: `Tests/GatewayServerTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  internal sealed class GatewayServer : IAsyncDisposable
  {
      public GatewayServer(IPAddress bind, int port, DeviceStore devices, Func<List<CatalogEntry>> catalog, Func<string, Host.HostRecord?> hostById,
                           Func<string, Stream?> embedded, string adminToken);
      public int Port { get; }                 // actual port after Start (0 → ephemeral, for tests)
      public Task StartAsync();
      public void CatalogChanged();            // push to /events subscribers
  }
  ```
  Routes (exact):
  - `POST /pair` body `{"code":"…","deviceName":"…"}` → 200 `{"token":"…","deviceId":"…"}` | 403 `{"error":"bad code"}`.
  - `GET /sessions` → 200 catalog JSON. Auth: `Authorization: Bearer <token>`.
  - `GET /events` (WebSocket) → on open and on every `CatalogChanged()`: text frame `{"type":"sessions","sessions":[…]}`. Auth: `?token=` on the upgrade (a header is also accepted). Origin rule: no `Origin` header, or `Origin` equal to `http://{Host header}`; anything else → 403 before the upgrade.
  - `GET /sessions/{hostId}/attach?after=N` (WebSocket) → relay (Task 6). Auth and Origin as `/events`. Host unknown or exited → 404; host `Protocol < 4` → 409 `{"error":"restart from the desktop to open here"}`.
  - `GET /mobile/{file}` → embedded `www/{file}`; `.html` → `text/html`, `.js` → `text/javascript`, `.css` → `text/css`; only files under `www/`, no `..`. No auth (the page itself holds no secrets; the token arrives from the native shell).
  - `GET /devices` → list (auth); `DELETE /devices/{id}` → 204 (auth: device token **or** `X-Admin-Token: <adminToken>`). Every open `/events` and attach socket is tagged with its device id; revoking closes them (close code 4401) within a second, so a revoked phone cannot keep typing.
  - `POST /admin/quit` with `X-Admin-Token` → 204 then the server stops (Task 8 uses it).
  - Anything else → 404. Missing/invalid token → 401 on HTTP; WebSocket upgrade refused with 401 before the 101.

- [ ] **Step 1: Write the failing tests** (server on `IPAddress.Loopback`, port 0; `HttpClient` + `ClientWebSocket`)

```csharp
[Fact] public async Task Pairing_then_listing_then_live_events()
{ /* POST /pair with issued code → token; GET /sessions with Bearer → the fake catalog JSON; connect ws /events?token= → first frame type "sessions"; CatalogChanged() → second frame */ }
[Fact] public async Task Unknown_tokens_are_refused_everywhere()
{ /* GET /sessions no header → 401; Bearer nope → 401; DELETE /devices/{id} while that device holds an open /events socket → the socket receives Close 4401 within 1 s; ws /events?token=nope → WebSocketException (handshake not 101); ws /events?token=good with Origin http://evil.example → refused; with Origin http://127.0.0.1:{port} → 101; GET /mobile/mobile.html → 200 without a token */ }
[Fact] public async Task Static_files_come_from_the_embedded_site_and_stay_inside_it()
{ /* /mobile/xterm.js → 200 text/javascript; /mobile/../deck.html → 404 */ }
```

- [ ] **Step 2: Run to verify they fail** → FAIL.
- [ ] **Step 3: Implement** on `TcpListener` exactly as `HostProgram.ServeClient` does (one `Task.Run` per connection, `WebSocket.CreateFromStream`); body limit 64 KB; connection header `Connection: close` on HTTP.
- [ ] **Step 4: Run all tests** → PASS (host tests still green after the `MiniHttp` move).
- [ ] **Step 5: Commit** — "A LAN gateway that serves the catalog to paired phones".

### Task 6: `AttachRelay` — the phone's terminal stream, input only

**Files:**
- Create: `Gateway/AttachRelay.cs`
- Create: `Tests/FakeHost.cs` (TcpListener WebSocket server speaking the host's attach protocol: records received text messages, sends a `snapshot` then whatever the test enqueues)
- Test: `Tests/AttachRelayTests.cs`

**Interfaces:**
- Produces: `internal static class AttachRelay { public static Task RunAsync(WebSocket phone, Host.HostRecord host, long after, CancellationToken ct); }` — connects `ws://127.0.0.1:{host.Port}/attach?token={host.Token}&after={after}` with `ClientWebSocket`, pumps both ways, closes both on either close. Phone→host: parse JSON; forward only when `type == "input"` (as received); drop everything else silently. Host→phone: forward every frame with its message type.
- Consumes: `GatewayServer` calls it from the `/sessions/{hostId}/attach` route after auth.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Only_input_reaches_the_host()
{ /* fake host; relay; phone sends {"type":"resize","cols":10,"rows":5}, {"type":"kill"}, {"type":"input","data":"ls\r"}; after 300 ms fake host received exactly one message, the input one */ }
[Fact] public async Task The_after_sequence_is_passed_to_the_host_and_frames_come_back_in_order()
{ /* relay with after=4096; fake host asserts its upgrade URL has after=4096; fake host sends snapshot text then 2 binary chunks; phone receives text first, then both chunks in order */ }
[Fact] public async Task Closing_either_side_closes_the_other()
{ /* phone closes → fake host sees close within 1 s; new relay, fake host closes → phone receives Close */ }
```

- [ ] **Step 2: Run to verify they fail** → FAIL.
- [ ] **Step 3: Implement**; wire the route in `GatewayServer` (unknown `hostId` or exited host → 404, `Protocol < 4` → 409, both before the upgrade; add `A_protocol_3_host_is_refused_with_409` to `GatewayServerTests`).
- [ ] **Step 4: Run all tests** → PASS.
- [ ] **Step 5: Rehearse** against a throwaway `--host` under `C:\Temp` (Task 1's rig) through a loopback gateway: a `ClientWebSocket` typing `echo hi\r` sees `hi` come back; a `resize` from the phone side changes nothing in the host record.
- [ ] **Step 6: Commit** — "The phone's terminal stream is relayed with input only".

### Task 7: The catalog loop

**Files:**
- Modify: `Gateway/GatewayServer.cs` (or create `Gateway/CatalogLoop.cs`)
- Test: `Tests/GatewayServerTests.cs` (append)

**Interfaces:**
- Produces: `internal sealed class CatalogLoop { public CatalogLoop(Func<List<CatalogEntry>> build, Action<List<CatalogEntry>> changed, Func<int> subscribers, TimeSpan busy = 5 s, TimeSpan idle = 30 s); public Task RunAsync(CancellationToken ct); public List<CatalogEntry> Current { get; } }` — rebuilds every `busy` while `subscribers() > 0`, else every `idle`; calls `changed` only when `SessionCatalog.ToJson` differs from the last push. In the gateway, `build = () => { CodexScanner.Probe(); return catalog.Build(SessionPipeline.Pair(SessionPipeline.Scan(prune: false)), Settings.Peek(), DateTime.UtcNow); }` after one `SessionPipeline.WarmUp()` at start (settings re-read each cycle so group edits on the desktop show up).

- [ ] **Step 1: Write the failing test**

```csharp
[Fact] public async Task The_loop_pushes_only_changes_and_slows_down_with_no_subscribers()
{ /* fake build returns A, A, B; busy=20 ms; subscribers=1 → changed called twice (A then B); then subscribers=0, idle=200 ms → next build happens ≥ 180 ms later */ }
```

- [ ] **Step 2: Run to verify it fails** → FAIL.
- [ ] **Step 3: Implement**; `GET /sessions` serves `Current`.
- [ ] **Step 4: Run all tests** → PASS.
- [ ] **Step 5: Commit** — "The gateway rebuilds the catalog on the deck's cadence and pushes changes".

### Task 8: `--gateway` process, started by the deck, replaced on update

**Files:**
- Create: `Gateway/GatewayProcess.cs`
- Modify: `Program.cs` (`--gateway` branch before the WPF app starts: load `Settings`, `DeviceStore`, `GatewayServer` bound to `IPAddress.Any`, `CatalogLoop`, write `gateway.json`, block until `/admin/quit` or Ctrl+C)
- Modify: `App.cs` (after `ApplyRunOnLogin()`: `if (Settings.Gateway.Enabled) GatewayProcess.EnsureRunning();`)
- Modify: `HostManager.cs` (`HostExe()` → `internal`)
- Test: `Tests/GatewayProcessTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  internal sealed record GatewayRecord(int Pid, long StartTicks, int Port, string Exe, string AdminToken);   // Settings.DataDir\gateway.json
  internal static class GatewayProcess
  {
      public static string RecordPath { get; }
      public static GatewayRecord? ReadRecord();
      public static bool IsAlive(GatewayRecord r);                 // pid + start time, like HostLiveness
      public static bool ShouldReplace(GatewayRecord r, string currentExe);   // alive && !PathEquals(r.Exe, currentExe)
      public static void EnsureRunning();                           // deck: none alive → spawn; alive and ShouldReplace → POST /admin/quit, wait ≤ 5 s, spawn
      public static Process Spawn();                                // HostManager.HostExe() + " --gateway", CreateNoWindow, strips InheritedSessionMarkers
  }
  ```
  Single instance: mutex `Settings.InstanceMutexName + "_Gateway"`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void A_newer_deck_replaces_an_older_gateway()
{
    var r = new GatewayRecord(Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, 7420, @"C:\old\SessionDeck.exe", "t");
    Assert.True(GatewayProcess.IsAlive(r));
    Assert.True(GatewayProcess.ShouldReplace(r, @"C:\new\SessionDeck.exe"));
    Assert.False(GatewayProcess.ShouldReplace(r, @"c:\OLD\SessionDeck.exe"));
    Assert.False(GatewayProcess.IsAlive(r with { StartTicks = r.StartTicks - 1 }));
}
```

- [ ] **Step 2: Run to verify it fails** → FAIL.
- [ ] **Step 3: Implement.** The `--gateway` branch logs to `%TEMP%\sessiondeck-gateway.log` (start, port, quit, errors).
- [ ] **Step 4: Run all tests** → PASS. Build Release.
- [ ] **Step 5: Rehearse** with `SD_DATA_DIR=C:\Temp\sd-gw`: run `SessionDeck.exe --gateway`, confirm `gateway.json`, `curl http://127.0.0.1:7421/sessions` → 401, `POST /admin/quit` → exits. Then run the deck itself under the same `SD_DATA_DIR` on the hidden desktop (`sd-rehearsal` tools) and confirm it spawns the gateway and a second deck start with a different stamped exe replaces it.
- [ ] **Step 6: Commit** — "A gateway process the deck keeps running and replaces when it updates".

### Task 9: Settings → Phone: QR pairing, devices, firewall

**Files:**
- Modify: `SessionDeck.csproj` — `<PackageReference Include="QRCoder" Version="1.6.0" />`
- Modify: `SettingsWindow.xaml` / `.cs` — new "Phone" section after "Terminal": enabled toggle, port box, "Show pairing code" button → QR image (`QRCoder.PngByteQRCode` → `BitmapImage`) + the code and `host:port` as text, 5-minute countdown; device list (name, paired, last seen, Revoke); "Allow through firewall" button.
- Create: `Gateway/GatewayClient.cs` — the deck's loopback client: `IssueCode()` (`POST /admin/pair-code` with `X-Admin-Token` → `{code, expiresAtUtc}`; add this route to `GatewayServer`), `Devices()`, `Revoke(id)`.
- Test: `Tests/GatewayServerTests.cs` (append: admin pair-code route needs the admin token; `QrPayload` formatting)

**Interfaces:**
- Produces: `internal static class QrPayload { public static string For(string host, int port, string code) => $"sessiondeck://pair?host={host}&port={port}&code={code}"; }`. `host` = the first non-loopback IPv4 of an up interface (`NetworkInterface.GetAllNetworkInterfaces()`), shown next to the QR so it can be typed.
- Firewall button runs elevated (`UseShellExecute = true, Verb = "runas"`): `netsh advfirewall firewall add rule name="SessionDeck gateway" dir=in action=allow protocol=TCP localport=<port> profile=private` after `delete rule name="SessionDeck gateway"`. One UAC prompt; the rule is by port so it survives updates.

- [ ] **Step 1: Write the failing tests** (`QrPayload.For("192.0.2.10", 7420, "ABCD2345")` exact string; `POST /admin/pair-code` without the admin token → 401, with it → a code `DeviceStore.Redeem` accepts).
- [ ] **Step 2: Run to verify they fail** → FAIL.
- [ ] **Step 3: Implement.** Follow the existing section layout (`TextBlock` header 13 SemiBold, rows in a two-column `Grid`). No icons, no pills.
- [ ] **Step 4: Run all tests** → PASS. Build Release.
- [ ] **Step 5: Rehearse** on the hidden desktop with `SD_DATA_DIR`: open Settings, show code, UIA-dump confirms the QR image and text; Revoke removes a device the test paired via curl.
- [ ] **Step 6: Commit** — "Pair a phone from Settings".

### Task 10: The mobile terminal page

**Files:**
- Create: `wwwroot/mobile.html`, `wwwroot/mobile.js` (CRLF; xterm from the same `wwwroot` files)

**Interfaces:**
- Consumes: `window.SD = { token, hostId, after }` injected by the native shell (`WKUserScript`, document start) or, for browser testing, from the query string `?host=&token=`.
- Produces (page → native via `window.webkit.messageHandlers.sd.postMessage(obj)`): `{type:"copy", text}`, `{type:"connected"}`, `{type:"disconnected"}`, `{type:"exit", code}`, `{type:"size", cols, rows}`. Native → page: `sdInput(text)` (sends `{"type":"input","data":text}`), `sdPaste(text)` (same), `sdSelect(on)` (toggles selection mode: xterm `disableStdin`, selection via touch), `sdCopySelection()` (posts `copy` with `term.getSelection()`).
- Behaviour: connects `ws://{location.host}/sessions/{hostId}/attach?token=…&after=…`; on `snapshot`/`size` sets `term.resize(cols, rows)`, sets `#term` width to `cols × cellWidth` px, and sets `<meta name="viewport">` to `width=<that px>` so WKWebView fits the full width on first paint and pinch-zoom/pan come from the scroll view. Same reconnect backoff as `deck.html` (`500 ms × 2^n`, cap 30 s). Theme: Campbell, same font stack as the deck; font size 14 px.

- [ ] **Step 1: Build the page**; verify in Edge against a loopback gateway + throwaway host (Task 6's rig) with `?host=&token=`: output renders at the host's cols, `sdInput("echo hi\r")` from the console echoes.
- [ ] **Step 2: Commit** — "A terminal page sized for a phone".

### Task 11: The iOS app

**Files:**
- Create: `ios/project.yml` (XcodeGen: target `SessionDeckMobile`, iOS 18.0, Swift 6, bundle id `com.jeremywho.sessiondeck`, `DEVELOPMENT_TEAM` from env `SD_TEAM`, automatic signing; `INFOPLIST_KEY_NSLocalNetworkUsageDescription`, `NSCameraUsageDescription`, ATS `NSAllowsLocalNetworking = YES`; test target `SessionDeckMobileTests`)
- Create: `ios/SessionDeckMobile/App.swift`, `Pairing/PairView.swift`, `Pairing/QRScanner.swift` (AVCaptureMetadataOutput), `Api/GatewayClient.swift` (URLSession; `pair`, `sessions`, `events` via `URLSessionWebSocketTask`), `Api/Models.swift` (`CatalogEntry` Codable mirroring Task 3, camelCase), `Store/Keychain.swift`, `Store/ViewedLedger.swift` (UserDefaults `[sessionId: Date]`), `Sessions/SessionsView.swift`, `Sessions/SessionRowView.swift`, `Terminal/TerminalView.swift` (`UIViewRepresentable` WKWebView, user script, message handler), `Terminal/InputBar.swift` (text field + Send + key bar), `Terminal/Keys.swift`
- Create: `ios/build.sh` — on the Mac: `xcodegen generate`, `security unlock-keychain -p "$(cat ~/.claude-keychain-pw)"`, `xcodebuild -project SessionDeckMobile.xcodeproj -scheme SessionDeckMobile -configuration Debug -destination "id=$UDID" -allowProvisioningUpdates -allowProvisioningDeviceRegistration DEVELOPMENT_TEAM=$SD_TEAM build`, `xcrun devicectl device install app --device $UDID <.app>`, `xcrun devicectl device process launch --device $UDID com.jeremywho.sessiondeck`. Also `test` subcommand running the unit tests on the simulator.
- Test: `ios/SessionDeckMobileTests/KeysTests.swift`, `ModelsTests.swift`

**Interfaces:**
- `Keys.bytes(for: Key) -> String`: `.esc → "\u{1b}"`, `.tab → "\t"`, `.shiftTab → "\u{1b}[Z"`, `.up/.down/.right/.left → "\u{1b}[A/B/C/D"`, `.ctrlC → "\u{03}"`, `.enter → "\r"`.
- Send: `sdInput(text + "\r")` as one call. Paste: `UIPasteboard.general.string` → `sdPaste`. Copy: toggles `sdSelect(true)`, a Copy button calls `sdCopySelection()`, the `copy` message sets `UIPasteboard.general.string` and leaves selection mode.
- Row colours: Working blue, Awaiting amber, Scheduled teal, Completed green (unread: violet tint on the row), Idle grey, Error red; glyph by provider `✳ ◆ ◈`. Unread = `state == "Completed" && stateSince > viewed[sessionId]`. `mobileReady == false` rows are greyed, not tappable, with the footnote "restart from the desktop to open here".
- `/events` reconnects with backoff; a banner "Gateway unreachable at host:port" when disconnected > 3 s. Pull to refresh calls `GET /sessions`.
- The text field: `.autocorrectionDisabled()`, `.textInputAutocapitalization(.never)`, smart quotes/dashes off via `UITextField.smartQuotesType = .no`, `smartDashesType = .no`, `smartInsertDeleteType = .no`.

- [ ] **Step 1: Write the failing tests** (`KeysTests`: each mapping above; `ModelsTests`: decode the Task 3 JSON sample, `state`, `stateSinceMs`).
- [ ] **Step 2: Sync the branch to the Mac** (`git push origin mobile` then on `macstudio.local`: `git clone`/`fetch` into `~/dev/SessionDeck`, `git checkout mobile`), run `ios/build.sh test` → FAIL.
- [ ] **Step 3: Implement the app.**
- [ ] **Step 4: `ios/build.sh test`** → PASS; `ios/build.sh` installs on the iPhone (device must be unlocked; USB or Wi-Fi paired).
- [ ] **Step 5: Commit** — "An iPhone companion that lists sessions and types into them".

### Task 12: End to end on the LAN, isolated

**Files:** none new (fixes go to the owning task's files; rehearsal notes to `docs/superpowers/plans/2026-10-29-mobile-companion.md` under *Rehearsal log*)

- [ ] **Step 1:** On Talon with `SD_DATA_DIR=C:\Temp\sd-gw-e2e`: start one throwaway `--host` running `pwsh -NoLogo` (never a live session), start `SessionDeck.exe --gateway` on port 7421, issue a code via the admin route, firewall rule for 7421 via the Settings button or `netsh` once.
- [ ] **Step 2:** On the iPhone: pair by typing `talon-ip:7421` and the code; the list shows the throwaway pane; open it; Send `Get-Date` + Enter; the output appears on the phone **and** in a desktop attach to the same host (`tools/host-probe.py`, read-only); Esc, arrows, Ctrl+C each produce the expected bytes in a `pwsh` `[Console]::ReadKey()` loop; Copy a selection and paste it into Notes; background the app 30 s, reopen, the screen resumes without reset.
- [ ] **Step 3:** Kill the throwaway deck instance's gateway, start it again: the phone's banner appears then clears; the list returns.
- [ ] **Step 4:** Record the results in the Rehearsal log; fix anything found in its owning task and re-run that task's tests.
- [ ] **Step 5: Commit** — "Rehearsal notes for the phone companion".

### Task 13: Release

- [ ] **Step 1:** `gh release list --repo jeremywho/SessionDeck --limit 1` (another session may have shipped); all tests pass; Release build clean; `git status --porcelain` empty.
- [ ] **Step 2:** `git push origin mobile:main`; `gh workflow run release.yml --repo jeremywho/SessionDeck -f version=vX.Y.Z` (next patch after the latest tag); confirm the asset.
- [ ] **Step 3:** Update `project_sessiondeck.md` memory with the gateway facts (port, files, routes, firewall rule, Mac build recipe) and bump the `MEMORY.md` line.

## Rehearsal log

**2026-10-29, Talon, isolated (`SD_DATA_DIR=C:\Temp\sd-e2e`, gateway on 7422, a throwaway `--host` running `pwsh -NoLogo`; no live host touched).** A scripted phone (`C:\Temp\sd-e2e\phone.py`, raw WebSocket): admin pair-code 200 → `/pair` 200 → `/sessions` listed the throwaway host as `mobileReady` → attach 101, first frame `snapshot` with `cols:100,rows:28` → a `resize` from the phone side changed nothing (host record still protocol 4, size untouched) → `input` of a `Write-Output` line came back in the binary stream → `/mobile/mobile.html` 200 → `/admin/quit` 204, gateway exited 0, `settings.json` byte-identical. Earlier the same day: `--gateway` alone wrote `gateway.json`, answered 401 without a token, and removed its record on quit.

**Still to run with the phone in hand:** pair by QR from Settings, open a session, Send / Esc / arrows / Ctrl+C, Copy a selection into Notes, background 30 s and resume, and the gateway-unreachable banner. The iPhone 16 Pro Max was `unavailable` to the Mac Studio while this was built (asleep or off the network), so the device install (`ios/build.sh install`) is the first step when it is awake.
