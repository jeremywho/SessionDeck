# Remote Relay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The iOS companion reaches the deck from anywhere through `https://sessiondeck.0a.gg`, with the gateway dialling out over SSH and nothing new listening at the home edge.

**Architecture:** The gateway process on Talon owns an `ssh -N -R` child that publishes its own port on the VPS loopback (`127.0.0.1:8103`); Caddy fronts that with TLS. The gateway accepts one baked-in relay key as a bearer credential, closes pairing and admin to forwarded requests, and probes its own public URL to report reachability. The app, when built with secrets, uses a fixed https pairing instead of the pairing screen.

**Tech Stack:** .NET 10 WPF (gateway, settings), Windows OpenSSH client 10.3, sshd + Caddy 2.11 on Ubuntu (`0a.gg`), Swift 6 / SwiftUI / XcodeGen built on `macstudio.local`, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-30-remote-relay-design.md`

## Global Constraints

- The relay key, the SSH private key and `relay.env` never enter the repository (public repo). `ios/SessionDeckMobile/Generated/` is gitignored.
- SSH user `sdtunnel`, remote port `127.0.0.1:8103`, sshd port `2222`, public URL `https://sessiondeck.0a.gg`, relay key = 32 random bytes as 64 hex chars.
- ssh command line exactly as the spec: `-F none -N -T -o BatchMode=yes -o IdentitiesOnly=yes -o ExitOnForwardFailure=yes -o ServerAliveInterval=20 -o ServerAliveCountMax=3 -o StrictHostKeyChecking=accept-new -o UserKnownHostsFile=<datadir>\relay_known_hosts -i <datadir>\relay_ed25519 -p <SshPort> -R 127.0.0.1:<RemotePort>:127.0.0.1:<gateway port> <SshUser>@<SshHost>`.
- Backoff 2 s doubling to a 60 s cap for retryable exits; `Permission denied`, host key mismatch and a missing key file are terminal.
- Relay auth is constant-time; the synthetic device is `Id "relay"`, `Name "Relay"`, never written to `devices.json`.
- Forwarded requests (`X-Forwarded-For` present) get 404 on `POST /pair` and `/admin/*`.
- `GET /health` → 204, unauthenticated.
- `wwwroot/mobile.js` ships inside the deck: a page change needs a deck release (`gh release list` first; another session ships from this worktree too).
- Never stop, restart or send input to Jeremy's live deck hosts; tests use `SD_DATA_DIR`.
- Windows files CRLF; `.sh`, `.yml`, `.swift`, `.js` as `.gitattributes` says. Tests run with `set -o pipefail` so a red run cannot hide behind `grep`.

## Review Focus

1. A relay key saved with surrounding whitespace or in upper-case hex: the gateway trims and compares case-sensitively on the exact stored string; the app sends what `relay.env` holds. Test in Task 2 (`Relay_key_is_matched_exactly_after_trimming`).
2. A LAN client that adds `X-Forwarded-For` itself is treated as remote and loses `/pair`: acceptable and deliberate; Task 2 test pins the 404 so nobody "fixes" it into a bypass.
3. The ssh child exits instantly forever (DNS down): the loop must space restarts 2, 4, 8, 16, 32, 60, 60 s and never spin. Task 3 test (`Instant_exits_back_off_to_the_cap`).
4. The relay key is set but the probe fails (DNS, Caddy down) while the tunnel child is alive: status says `down` with the probe reason and the tunnel keeps running. Task 3 test (`A_failed_probe_reports_down_without_restarting_the_tunnel`).
5. A Keychain pairing written by today's app has no scheme: it must decode as `http` and keep working. Task 7 test (`Pairing_without_a_scheme_is_http`).

---

### Task 1: Relay settings and carry-through

**Files:**
- Modify: `Settings.cs:143-148` (`GatewaySettings`)
- Test: `Tests/SettingsRoundTripTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  internal sealed class RelaySettings
  {
      public bool Enabled { get; set; } = false;
      public string SshHost { get; set; } = "0a.gg";
      public int SshPort { get; set; } = 2222;
      public string SshUser { get; set; } = "sdtunnel";
      public int RemotePort { get; set; } = 8103;
      public string PublicUrl { get; set; } = "https://sessiondeck.0a.gg";
      public string Key { get; set; } = "";
  }
  // on GatewaySettings:
  public RelaySettings Relay { get; set; } = new();
  [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }
  ```

- [ ] **Step 1: Write the failing tests** in `Tests/SettingsRoundTripTests.cs`:
  - `Relay_settings_round_trip`: serialize a `Settings` whose `Gateway.Relay` has `Enabled = true`, `Key = "ab" * 32`, `PublicUrl = "https://x.example"`; deserialize; assert the three values.
  - `Gateway_settings_keep_keys_this_build_does_not_know`: deserialize `{"Gateway":{"Enabled":true,"Port":7420,"Future":{"a":1}}}` into `Settings`, serialize again, assert the JSON contains `"Future"` and `"a":1`.
  - `Relay_defaults_match_the_deployment`: a fresh `RelaySettings` has `SshHost "0a.gg"`, `SshPort 2222`, `SshUser "sdtunnel"`, `RemotePort 8103`, `PublicUrl "https://sessiondeck.0a.gg"`, `Enabled false`, `Key ""`.
- [ ] **Step 2: Run** `dotnet test Tests --filter "FullyQualifiedName~SettingsRoundTripTests" 2>&1 | tail -5` → FAIL (`RelaySettings` not found).
- [ ] **Step 3: Implement** `RelaySettings` and the two `GatewaySettings` members in `Settings.cs` as in Interfaces.
- [ ] **Step 4: Run** the same filter → PASS; run the whole suite → all green.
- [ ] **Step 5: Commit** `Settings.cs Tests/SettingsRoundTripTests.cs`: "Relay settings, and gateway settings carry unknown keys through".

### Task 2: Gateway accepts the relay key and closes pairing to the public path

**Files:**
- Modify: `Gateway/GatewayServer.cs` (constructor, auth block at ~148, `StillPaired`, `Revoke` handling of `DELETE /devices/{id}`, `OriginAllowed`, new `/health`)
- Modify: `Gateway/GatewayProcess.cs:142-146` (pass `settings.Gateway.Relay.Key`)
- Test: `Tests/GatewayServerTests.cs`

**Interfaces:**
- Produces: `GatewayServer(IPAddress bind, int port, DeviceStore devices, Func<IReadOnlyList<CatalogEntry>> catalog, Func<string, HostRecord?> hostById, Func<string, Stream?> embedded, string adminToken, string relayKey = "")`.
- `internal const string RelayDeviceId = "relay";` on `GatewayServer`.
- `internal static bool Forwarded(MiniHttp.Request r)` → `r.Header("X-Forwarded-For").Length > 0`.
- `internal static bool OriginAllowed(MiniHttp.Request r)`: expected scheme is `X-Forwarded-Proto` when non-empty, else `http`.
- `internal static bool KeyMatches(string presented, string key)`: `CryptographicOperations.FixedTimeEquals` over UTF-8 bytes of `presented.Trim()` and `key.Trim()`; false when either is empty.

- [ ] **Step 1: Write the failing tests** (the fixture gets a second constructor path: `_server` built with `relayKey: RelayKey` where `const string RelayKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"`):
  - `Relay_key_lists_sessions_and_opens_sockets_as_the_relay_device`: `GET /sessions` with `Bearer RelayKey` → 200; `/events?token=RelayKey` connects and the first frame is `type "sessions"`; `_devices.Devices` is still empty.
  - `Relay_key_is_matched_exactly_after_trimming`: `Bearer " RelayKey "` → 200; upper-cased key → 401; empty key configured (second server with `relayKey: ""`) and `Bearer ""` → 401.
  - `Forwarded_requests_cannot_pair_or_reach_admin`: `POST /pair` with header `X-Forwarded-For: 203.0.113.9` and a valid code → 404 and the code is still redeemable without the header; `POST /admin/pair-code` with the admin token and the header → 404.
  - `Health_is_open_and_says_nothing`: `GET /health` → 204 with an empty body; `GET /health` with `X-Forwarded-For` → 204.
  - `Origin_follows_the_forwarded_scheme`: `/events?token=RelayKey` with `Origin: https://sessiondeck.0a.gg`, `Host: sessiondeck.0a.gg`, `X-Forwarded-Proto: https` → connects; same with `Origin: http://sessiondeck.0a.gg` → handshake 403.
  - `Revoking_the_relay_device_is_not_a_thing`: `DELETE /devices/relay` with the admin token → 404 and an open relay `/events` socket stays open for 500 ms.
- [ ] **Step 2: Run** `dotnet test Tests --filter "FullyQualifiedName~GatewayServerTests" 2>&1 | tail -5` → FAIL (no `relayKey` parameter).
- [ ] **Step 3: Implement** in `GatewayServer.cs`: the constructor parameter; in the request path, before `_devices.Authenticate`, `if (KeyMatches(BearerToken(r) ?? "", _relayKey)) device = RelayDevice` where `RelayDevice = new PairedDevice(RelayDeviceId, "Relay", "", DateTime.UnixEpoch, DateTime.UnixEpoch)`; `Forwarded(r)` → 404 for `/pair` and `/admin/`; `/health` → 204 before any auth; `StillPaired` returns true for `RelayDeviceId`; the devices DELETE route returns 404 for `RelayDeviceId`; `OriginAllowed` per Interfaces. Pass `settings.Gateway.Relay.Key` in `GatewayProcess.Run`.
- [ ] **Step 4: Run** the filter → PASS; whole suite green.
- [ ] **Step 5: Commit**: "The gateway takes the relay key as a credential and keeps pairing and admin off the public path".

### Task 3: RelayTunnel (ssh child, backoff, probe, status file)

**Files:**
- Create: `Host/ChildJob.cs` (move the job-object P/Invoke out of `Host/ConPty.cs:125-131,182,202-205` into `internal static class ChildJob { public static IntPtr CreateKillOnClose(); public static bool Assign(IntPtr job, IntPtr process); }`; `ConPty` calls it)
- Create: `Gateway/RelayTunnel.cs`
- Test: `Tests/RelayTunnelTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  internal sealed record RelayStatus(string State, string? Reason, DateTime? ConnectedSinceUtc, DateTime? LastProbeUtc); // State: "connecting" | "connected" | "down"
  internal interface IRelayChild { Task<int> Exited { get; } string StderrTail { get; } void Kill(); }
  internal enum RelayExit { Retry, Terminal }
  internal sealed class RelayTunnel : IAsyncDisposable
  {
      public RelayTunnel(RelaySettings settings, int gatewayPort, string dataDir,
                         Func<IReadOnlyList<string>, IRelayChild> start,   // args → running child
                         Func<CancellationToken, Task<string?>> probe,       // null = reachable, else reason
                         Action<RelayStatus> publish,
                         Func<int, TimeSpan>? backoff = null, TimeSpan? probeEvery = null);
      public Task RunAsync(CancellationToken ct);
      public static IReadOnlyList<string> Arguments(RelaySettings s, int gatewayPort, string dataDir);
      public static RelayExit Classify(string stderrTail);
      public static TimeSpan Backoff(int attempt);           // 2,4,8,16,32,60,60…
      public static string SshExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
      public static string StatusPath(string dataDir) => Path.Combine(dataDir, "relay.json");
      public static RelayStatus? ReadStatus(string dataDir);
      public static void WriteStatus(string dataDir, RelayStatus status);   // AtomicFile.Write of relay.json
      public static Func<CancellationToken, Task<string?>> HttpProbe(string publicUrl, string key); // GET <publicUrl>/health, Bearer key, 5 s timeout
  }
  ```
- `RunAsync` loop: publish `connecting`; start child with `Arguments(...)`; if the key file `<dataDir>\relay_ed25519` is missing publish `down`/"no key file" and return (terminal). While the child runs, probe every `probeEvery` (default 30 s): success → `connected` (ConnectedSinceUtc set once per child life), failure → `down` with the probe reason, child untouched. On child exit: `Classify(StderrTail)`; `Terminal` → publish `down` with the stderr tail and return; `Retry` → publish `down`, wait `Backoff(attempt)`, attempt++ (reset to 0 after a child lived ≥ 60 s), loop. Cancellation kills the child and publishes `down`/"stopped".
- `Classify`: `Terminal` when the tail contains `Permission denied`, `REMOTE HOST IDENTIFICATION HAS CHANGED`, `Host key verification failed`, `No such file or directory` (for the identity file), or `Could not resolve hostname` is **Retry**; everything else `Retry`.

- [ ] **Step 1: Write the failing tests** in `Tests/RelayTunnelTests.cs` with a `FakeChild` (completes `Exited` on demand, settable `StderrTail`) and a recording `publish`:
  - `Arguments_match_the_spec`: with defaults, port 7420, dataDir `C:\d`: the list equals `["-F","none","-N","-T","-o","BatchMode=yes","-o","IdentitiesOnly=yes","-o","ExitOnForwardFailure=yes","-o","ServerAliveInterval=20","-o","ServerAliveCountMax=3","-o","StrictHostKeyChecking=accept-new","-o",@"UserKnownHostsFile=C:\d\relay_known_hosts","-i",@"C:\d\relay_ed25519","-p","2222","-R","127.0.0.1:8103:127.0.0.1:7420","sdtunnel@0a.gg"]`.
  - `Backoff_doubles_from_two_seconds_to_a_minute`: attempts 0..6 → 2, 4, 8, 16, 32, 60, 60 s.
  - `Permission_denied_and_host_key_changes_are_terminal`: `Classify("sdtunnel@0a.gg: Permission denied (publickey).")` → Terminal; `"@@@ WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!"` → Terminal; `"Host key verification failed."` → Terminal; `"ssh: connect to host 0a.gg port 2222: Connection timed out"` → Retry; `"Error: remote port forwarding failed for listen port 8103"` → Retry; `""` → Retry.
  - `Instant_exits_back_off_to_the_cap`: backoff injected as `attempt => TimeSpan.FromMilliseconds(attempt)` recorder; children exit immediately with Retry tails; after 6 restarts the recorded attempts are 0..5 and the statuses alternate `connecting`/`down`.
  - `A_terminal_exit_stops_the_loop`: child exits with `Permission denied`; `RunAsync` completes; last status `down` with Reason containing `Permission denied`; `start` was called once.
  - `A_failed_probe_reports_down_without_restarting_the_tunnel`: probe returns `"503 from caddy"`, `probeEvery` 10 ms; after 50 ms the last status is `down`/`"503 from caddy"` and `start` was called once; then the probe returns null → status `connected` with `ConnectedSinceUtc` set.
  - `A_missing_key_file_is_terminal_before_any_child`: dataDir without `relay_ed25519` → last status `down`/`"no key file"`, `start` never called. (The other tests create an empty `relay_ed25519` in their temp dataDir.)
  - `Cancelling_kills_the_child`: cancel while the child runs → `FakeChild.Killed` true, last status `down`/`"stopped"`.
  - `Status_round_trips_through_relay_json`: write via a `publish` that uses the real file writer (`RelayTunnel.WriteStatus(dataDir, status)`), `ReadStatus(dataDir)` equals it.
- [ ] **Step 2: Run** `dotnet test Tests --filter "FullyQualifiedName~RelayTunnelTests" 2>&1 | tail -5` → FAIL (type not found).
- [ ] **Step 3: Implement** `Host/ChildJob.cs` (moved code; `ConPty` behaviour unchanged, its tests still green) and `Gateway/RelayTunnel.cs` per Interfaces. The production `start` lives in `RelayTunnel` as `public static IRelayChild StartSsh(IReadOnlyList<string> args)`: `ProcessStartInfo(SshExe)` with `ArgumentList`, `RedirectStandardError`, `UseShellExecute=false`, `CreateNoWindow=true`; assign to a `ChildJob.CreateKillOnClose()` job held for the tunnel's lifetime; stderr read on a background task into a ring of the last 20 lines. `WriteStatus` uses `AtomicFile.Write`.
- [ ] **Step 4: Run** the filter → PASS; `dotnet test Tests 2>&1 | tail -3` → all green (ConPty/host tests included).
- [ ] **Step 5: Commit**: "RelayTunnel keeps an ssh reverse tunnel to the VPS up and reports whether the public URL answers".

### Task 4: Gateway runs the tunnel; the page follows its scheme

**Files:**
- Modify: `Gateway/GatewayProcess.cs:170-192` (after `WriteRecord`, before the quit wait)
- Modify: `wwwroot/mobile.js:77`
- Test: `Tests/GatewayProcessTests.cs` (one test), page check script `C:\Temp\sd-ws-scheme-check.js` (not committed)

**Interfaces:**
- Consumes: `RelayTunnel` from Task 3; `settings.Gateway.Relay` from Task 1.
- Produces: `internal static RelayTunnel? StartRelay(Settings settings, int gatewayPort)` on `GatewayProcess` → null when `!Relay.Enabled || Relay.Key.Trim().Length == 0` (and `Log("relay disabled")`), else a tunnel built with `StartSsh`, `HttpProbe(PublicUrl, Key)`, and `WriteStatus(Settings.DataDir, …)`.

- [ ] **Step 1: Write the failing test** `Relay_is_not_started_without_a_key` in `Tests/GatewayProcessTests.cs`: `StartRelay(new Settings { Gateway = { Relay = { Enabled = true, Key = "" } } }, 7420)` → null; `Enabled = false, Key = "ab"` → null.
- [ ] **Step 2: Run** `dotnet test Tests --filter "FullyQualifiedName~GatewayProcessTests" 2>&1 | tail -5` → FAIL.
- [ ] **Step 3: Implement** `StartRelay`; in `Run`, after `WriteRecord`: `var relay = StartRelay(settings, server.Port); var relayRun = relay?.RunAsync(cts.Token);` and after `cts.Cancel()`: `try { relayRun?.Wait(TimeSpan.FromSeconds(5)); } catch { }` then `relay?.DisposeAsync()`; delete `relay.json` on a clean quit next to the `RecordPath` delete.
- [ ] **Step 4: Page:** in `wwwroot/mobile.js` build the socket URL with `` `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/sessions/...` ``. Check with `node C:\Temp\sd-ws-scheme-check.js` that evaluates the expression for `https:` → `wss://`, `http:` → `ws://`.
- [ ] **Step 5: Run** the filter → PASS; whole suite green; `git diff --numstat wwwroot/mobile.js` is a one-line change and `tr -dc '\r' | wc -c` on the file is unchanged.
- [ ] **Step 6: Commit**: "The gateway runs the relay tunnel when a key is set, and the page's socket follows the page's scheme".

### Task 5: Settings window "Remote" group

**Files:**
- Modify: `SettingsWindow.xaml` (after the firewall row, ~line 108), `SettingsWindow.xaml.cs` (load ~34, save ~67-72, a 2 s `DispatcherTimer` reading `RelayTunnel.ReadStatus(Settings.DataDir)`)

**Interfaces:**
- Consumes: `RelaySettings`, `RelayTunnel.ReadStatus`, `App.ApplyGatewaySettings()`.
- Controls: `RelayToggle` (ToggleSwitch), `RelayUrlBox` (TextBox), `RelayKeyBox` (PasswordBox), `RelayGenerateButton`, `RelayCopyButton`, `RelayStatus` (TextBlock).

- [ ] **Step 1: XAML:** a "Remote" sub-heading with the copy "Reach the deck from anywhere through your own server. The gateway dials out; nothing listens on this network." Rows: toggle; URL box; key box with Generate and Copy; status line.
- [ ] **Step 2: Code-behind:** load the three values; on save, any change to `Enabled`, `PublicUrl` or `Key` (trimmed) calls `_app.ApplyGatewaySettings()` like the port does. Generate → `Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()` into the box. Copy → `Clipboard.SetText(key)`. Status text: `connected since <local time>` / `connecting…` / `down: <reason>` / `off` (Relay disabled) / `no status yet` (file absent).
- [ ] **Step 3: Verify** with an isolated instance: `SD_DATA_DIR=C:\Temp\sd-relay-ui` and the built `SessionDeck.exe`; open Settings; toggle Remote on, Generate, Save; `C:\Temp\sd-relay-ui\settings.json` holds the key; the isolated gateway's `relay.json` appears within 5 s saying `down: no key file`. `dotnet build` has no warnings from the new code. Close the instance.
- [ ] **Step 4: Commit**: "Settings: a Remote group with the relay URL, key and live status".

### Task 6: VPS provisioning script and Talon key

**Files:**
- Create: `tools/relay-vps-setup.sh` (runs ON the VPS as `jeremy` with sudo; idempotent; takes the public key line as `$1`)

**Interfaces:**
- Produces on the VPS: user `sdtunnel`, `/home/sdtunnel/.ssh/authorized_keys` = `restrict,port-forwarding,permitlisten="127.0.0.1:8103" <pubkey>`, `/etc/ssh/sshd_config.d/60-sdtunnel.conf`, the `sessiondeck.0a.gg` Caddy block appended once (guarded by `grep -q "^sessiondeck.0a.gg"`), Caddyfile backup at `/etc/caddy/Caddyfile.bak.relay-<date>`.

- [ ] **Step 1: Write the script** with `set -euo pipefail`: `useradd --system --shell /usr/sbin/nologin --create-home sdtunnel` if absent; write authorized_keys (mode 600, owner sdtunnel); write the sshd drop-in (`Match User sdtunnel` / `AllowTcpForwarding remote` / `PermitTTY no` / `X11Forwarding no` / `ClientAliveInterval 30` / `ClientAliveCountMax 3`); `sudo sshd -t`; `systemctl reload ssh`; append the Caddy block from the spec; copy to `/tmp/Caddyfile.check`, `sudo -u caddy caddy validate --config /tmp/Caddyfile.check --adapter caddyfile`; `systemctl reload caddy`; print `curl -s -o /dev/null -w '%{http_code}' https://sessiondeck.0a.gg/health` (expected 503 until the tunnel is up).
- [ ] **Step 2: Talon key:** `ssh-keygen -t ed25519 -N "" -C sessiondeck-tunnel@talon -f "%APPDATA%\SessionDeck\relay_ed25519"` (this is the one write to the real data dir, and it is the deployment Jeremy asked for). Read `relay_ed25519.pub`.
- [ ] **Step 3: Run** `scp tools/relay-vps-setup.sh jeremy@0a.gg:/tmp/ && ssh jeremy@0a.gg 'bash /tmp/relay-vps-setup.sh "<pubkey line>"'` → ends with `503`. `sudo ss -ltnp | grep 8103` shows nothing yet.
- [ ] **Step 4: Verify the tunnel by hand once** from Talon (Git Bash): the exact `Arguments` list with `C:\Windows\System32\OpenSSH\ssh.exe` and `-v`, in the foreground for 20 s → `Remote connections from 127.0.0.1:8103 forwarded to local address 127.0.0.1:7420`; from the Mac `curl -s -o /dev/null -w '%{http_code}' https://sessiondeck.0a.gg/health` → 204 (Jeremy's live gateway, read-only); Ctrl-C the ssh. The known-hosts file now exists.
- [ ] **Step 5: Commit** `tools/relay-vps-setup.sh`: "A script that prepares the VPS for the relay tunnel".

### Task 7: iOS app uses the baked-in relay

**Files:**
- Modify: `ios/build.sh` (`generate()`), `ios/project.yml` (nothing if sources are the folder; confirm `Generated/` is under `SessionDeckMobile`), `.gitignore` (`ios/SessionDeckMobile/Generated/`)
- Modify: `ios/SessionDeckMobile/Api/Models.swift:64-72` (`Pairing`), `ios/SessionDeckMobile/Api/GatewayStore.swift` (init, 401 handling), `ios/SessionDeckMobile/App.swift:22-29`, the `…` menu in `ios/SessionDeckMobile/Sessions/SessionsView.swift`
- Test: `ios/SessionDeckMobileTests/ModelsTests.swift`

**Interfaces:**
- Produces: `ios/SessionDeckMobile/Generated/RelaySecrets.swift`:
  ```swift
  enum RelaySecrets { static let url: String? = <"https://sessiondeck.0a.gg" or nil>; static let key: String? = <key or nil> }
  ```
  written by `generate()` from `~/.sessiondeck-mobile/relay.env` (`RELAY_URL=`, `RELAY_KEY=` lines) or with `nil` values when the file is absent.
- `Pairing` gains `let scheme: String` with a custom `init(from:)` defaulting to `"http"`; `baseURL` = `"\(scheme)://\(host):\(port)"`, `wsBaseURL` uses `"wss"` for `"https"` else `"ws"`.
- `static func relay() -> Pairing?` on `GatewayStore`: from `RelaySecrets` → `Pairing(host: <url host>, port: <url port ?? 443>, token: key, deviceId: "relay", scheme: <url scheme>)`.
- `GatewayStore.init`: `pairing = Self.relay() ?? Keychain.loadPairing()`; `var usesRelay: Bool`.
- On 401 / 4401 when `usesRelay`: do **not** forget the pairing; `lastError = "Relay key rejected"`, `connected = false`, and the existing Retry banner reconnects.
- `…` menu: a disabled row "via <host>" when `usesRelay`.

- [ ] **Step 1: Write the failing tests** in `ModelsTests.swift`:
  - `testPairingWithoutASchemeIsHttp`: decode `{"host":"10.0.0.5","port":7420,"token":"t","deviceId":"d"}` → `scheme == "http"`, `baseURL.absoluteString == "http://10.0.0.5:7420"`, `wsBaseURL.absoluteString == "ws://10.0.0.5:7420"`.
  - `testHttpsPairingUsesWss`: `Pairing(host: "sessiondeck.0a.gg", port: 443, token: "k", deviceId: "relay", scheme: "https")` → `baseURL == "https://sessiondeck.0a.gg:443"`, `wsBaseURL == "wss://sessiondeck.0a.gg:443"`.
  - `testRelayPairingComesFromTheSecrets`: `GatewayStore.relay(url: "https://sessiondeck.0a.gg", key: "k")` (a testable overload taking the two strings) → host, port 443, scheme https, token "k", deviceId "relay"; `relay(url: nil, key: nil)` → nil; `relay(url: "https://x", key: "")` → nil.
- [ ] **Step 2: Run** on the Mac: `ssh macstudio.local 'cd ~/dev/SessionDeck && git fetch -q && git reset -q --hard origin/mobile && ios/build.sh test 2>&1 | grep -E "error:|Executed|TEST"'` → FAIL (compile errors).
- [ ] **Step 3: Implement** the Swift changes and `generate()` per Interfaces; `.gitignore` entry; a `Generated/RelaySecrets.swift` with nils is written by `generate()` so `xcodegen` always sees the file.
- [ ] **Step 4: Run** the test command → `Executed 16 tests, with 0 failures`.
- [ ] **Step 5: Put the secrets on the Mac** (never in the repo): `~/.sessiondeck-mobile/relay.env` with the URL and the key Jeremy's Settings now holds (read from Talon's `settings.json`, read-only). `chmod 600`.
- [ ] **Step 6: Commit** (confirm `git status --porcelain` shows no `Generated/` file): "The app reaches the deck through the relay when the build carries the secrets".

### Task 8: Ship and verify end to end

**Files:** none new.

- [ ] **Step 1: Deck release:** `gh release list --repo jeremywho/SessionDeck --limit 3` (another session ships from this worktree: pick the next patch); push `mobile:main`; `gh workflow run release.yml --repo jeremywho/SessionDeck -f version=vX.Y.Z`; wait for the asset; tell the peer session (Xteink) to update the deck so the gateway with the relay goes live.
- [ ] **Step 2: Talon settings:** in the deck's Settings (after the update), Remote on, Generate key, URL `https://sessiondeck.0a.gg`, Save. Status reaches `connected since …` within 60 s. `relay.env` on the Mac gets that key (Task 7 step 5, if not done).
- [ ] **Step 3: App:** `/tmp/sd-mac-cycle.sh` → build N installed on iPad and iPhone; `…` menu shows "via sessiondeck.0a.gg".
- [ ] **Step 4: Spec verification 1** from the Mac: `curl -s -o /dev/null -w '%{http_code}' https://sessiondeck.0a.gg/health` → 204; `/sessions` with `Authorization: Bearer <key>` → 200 and a JSON list; without → 401; `curl -X POST https://sessiondeck.0a.gg/pair -d '{}'` → 404.
- [ ] **Step 5: Spec verification 2** on the iPhone with Wi-Fi off (hand-off to Jeremy with exact steps: Control Centre → Wi-Fi off → open SessionDeck → open a session → tap, type `echo relay-ok` into a throwaway Terminal session started from `+`): the output appears; a passive attach from Talon to that host shows the line. Until he does, the same path is proven on Wi-Fi with `--open` and `--send` through the relay build (the app has no LAN path once secrets are stamped).
- [ ] **Step 6: Spec verification 3:** `taskkill /f /im ssh.exe` is NOT allowed (Jeremy's other ssh sessions); instead find the gateway's ssh child by parent PID (`Get-CimInstance Win32_Process -Filter "Name='ssh.exe'" | ? ParentProcessId -eq <gateway pid>`) and `Stop-Process -Force` it → `relay.json` goes `down` then `connected` within 60 s, the app's banner recovers; the VPS frees 8103 within 2 min (`ss -ltnp | grep 8103` on the VPS). Then Settings → Save (gateway replaced) → one ssh child, 8103 bound again, no orphan.
- [ ] **Step 7: Spec verification 4:** `git grep -n "<key>"` and `git grep -n "RELAY_KEY="` in the worktree → nothing; `git check-ignore ios/SessionDeckMobile/Generated/RelaySecrets.swift` → listed.
- [ ] **Step 8: Memory + report:** append to `project_sessiondeck.md` (relay live, versions, VPS user, files) and send the result to the Xteink session.
