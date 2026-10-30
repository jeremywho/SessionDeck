# Remote relay for the mobile companion

**Date:** 2026-10-30  
**Status:** approved in chat (Jeremy, 2026-10-30 afternoon CT)  
**Builds on:** `2026-10-29-mobile-companion-design.md` (LAN gateway + iOS app)

## Intent

The iOS companion works only on the home LAN today. Jeremy wants it to work anywhere, through his
VPS (`0a.gg`, Caddy in front), without opening a port into the home network. The desktop side dials
out; the phone talks to `https://sessiondeck.0a.gg`; the VPS carries bytes between the two.

This is a one-off deployment for Jeremy's own devices: the app is always built and installed by
Claude from the Mac Studio, so the public URL and the credential are stamped into the app at build
time. No pairing flow is needed for the remote path.

## Goals

- Open any session, read it, and type into it from the iPhone or iPad on cellular, with the same
  behaviour as on the LAN (tap to type, key bar, dictation, zoom, links, start new sessions, load header).
- Nothing new listens on the home network's edge. Only an outbound SSH connection leaves Talon.
- A stranger who finds `sessiondeck.0a.gg` gets 401/404 for everything useful and cannot pair.
- The remote path survives Talon reboots, VPS reboots, and Wi-Fi drops without anyone touching it,
  within the lifetime of Jeremy's Windows session: the deck, the hosts and therefore the sessions all
  live in that interactive session, so after a reboot nothing exists to show until Jeremy logs in at
  Talon (no auto-logon there, and turning it on is a machine security change outside this work).
  The relay comes back with the deck, which the HKCU Run entry starts at login.

## Non-goals

- LAN-first switching (using the LAN when at home and the relay otherwise). With secrets stamped in,
  the app always uses the relay. If the VPS is down the app is down, at home too. Accepted.
- Push notifications, multiple relays, several users, a pairing UI for the relay, Bonjour.
- Hosting the deck page for desktop browsers through the relay (it will work, but is not a goal).

## Anti-goals

- Committing the relay key, the SSH private key or the public URL's credential to the public repo.
- A relay that can reach anything on Talon other than the gateway port.
- A tunnel user on the VPS that can run commands or forward anything but one loopback port.

## Architecture

```
iPhone/iPad app ──HTTPS/WSS──▶ Caddy (sessiondeck.0a.gg, auto-TLS)
                                  │ reverse_proxy 127.0.0.1:8103
                                  ▼
                              sshd on 0a.gg (user sdtunnel, permitlisten 127.0.0.1:8103)
                                  ▲ one outbound ssh -N -R from Talon
                                  │
Talon: SessionDeck.exe --gateway ─┘  owns the ssh child; serves 0.0.0.0:7420 as today
```

Everything the gateway serves today is served unchanged through the tunnel: `/sessions`, `/meta`,
`POST /sessions`, `/events`, `/sessions/{id}/attach`, `/mobile/*`.

### VPS (`0a.gg`, decided)

- A new system user `sdtunnel`, shell `/usr/sbin/nologin`, home `/home/sdtunnel`, with one
  `authorized_keys` line: `restrict,port-forwarding,permitlisten="127.0.0.1:8103" ssh-ed25519 … sessiondeck-tunnel@talon`.
  Same shape as the existing zendgame engine tunnel from macstudio2 (`permitlisten="127.0.0.1:8101"`).
- An sshd drop-in `/etc/ssh/sshd_config.d/60-sdtunnel.conf` with a `Match User sdtunnel` block:
  `AllowTcpForwarding remote`, `PermitTTY no`, `X11Forwarding no`, `ClientAliveInterval 30`,
  `ClientAliveCountMax 3`. The keepalive matters: when Talon vanishes (reboot, link drop) sshd must
  free 127.0.0.1:8103 within about 90 s or the reconnect fails with "remote port forwarding failed".
- Caddy block, appended to `/etc/caddy/Caddyfile` after a backup copy:
  ```
  sessiondeck.0a.gg {
      encode zstd gzip
      reverse_proxy 127.0.0.1:8103
      handle_errors {
          @down expression {err.status_code} == 502
          respond @down "SessionDeck is offline" 503
      }
  }
  ```
  Caddy proxies WebSocket upgrades without extra config, passes the original `Host` header through
  and adds `X-Forwarded-For` / `X-Forwarded-Proto`. Validate as `sudo -u caddy caddy validate` on a
  copy in `/tmp`, then `systemctl reload caddy`. `*.0a.gg` already resolves to the box; no DNS work.
- No ufw change (443 is open; 8103 is loopback only).

### Talon gateway (decided)

- `GatewaySettings` gains a `Relay` object in `settings.json`:
  `Enabled`, `SshHost` ("0a.gg"), `SshPort` (2222), `SshUser` ("sdtunnel"), `RemotePort` (8103),
  `PublicUrl` ("https://sessiondeck.0a.gg"), `Key` (the relay key, 64 hex chars). The SSH private
  key lives at `%APPDATA%\SessionDeck\relay_ed25519` (created by `ssh-keygen` during deployment,
  never in the repo); known hosts at `%APPDATA%\SessionDeck\relay_known_hosts`.
  `GatewaySettings` gets a `[JsonExtensionData]` bag like the outer `Settings` has, so a build that
  does not know `Relay` (the Updater's rollback to `.old` relaunches v0.5.54, which rewrites
  `settings.json` on launch) carries it through instead of dropping it. One-time hazard: a rollback
  to a build older than this change loses the `Relay` object; re-enter the key in Settings (the ssh
  key file and the Mac's `relay.env` survive, so no app rebuild).
- New `Gateway/RelayTunnel.cs`, owned by the gateway process (not the deck UI), starts and babysits:
  ```
  ssh -F none -N -T -o BatchMode=yes -o IdentitiesOnly=yes -o ExitOnForwardFailure=yes
      -o ServerAliveInterval=20 -o ServerAliveCountMax=3
      -o StrictHostKeyChecking=accept-new -o UserKnownHostsFile=<datadir>\relay_known_hosts
      -i <datadir>\relay_ed25519 -p 2222 -R 127.0.0.1:8103:127.0.0.1:<gateway port> sdtunnel@0a.gg
  ```
  Windows' bundled OpenSSH client (`C:\Windows\System32\OpenSSH\ssh.exe`, 10.3 on Talon). `-F none`
  and `IdentitiesOnly` keep Jeremy's own `~/.ssh/config` entry for `0a.gg` and his agent keys out
  of it: every wrong key offered is a failed publickey attempt in sshd's log, and fail2ban watches
  that log. Exits are sorted by stderr: network and forward failures restart with backoff 2 s
  doubling to 60 s; `Permission denied`, host-key mismatch and a missing key file are terminal
  (`down: key rejected` and so on) with no retry until the relay settings change or the gateway
  restarts, so a stale key can never get Talon's home IP banned from the VPS.
  The child is placed in a job object that kills it when the gateway dies (same mechanism
  `Host/ConPty.cs` uses for ptys), so a replaced gateway never leaves an orphan holding 8103.
- Reachability is measured end to end, not inferred from the child being alive: every 30 s the
  gateway `GET`s `<PublicUrl>/health` with the relay key and records the state
  (`connected` / `connecting` / `down: <reason>`) plus the last success time in `relay.json` next to
  `gateway.json` (its own file, so the `GatewayRecord` the deck reads stays as it is).
  `GET /health` is a new unauthenticated route returning 204 (it exposes nothing).
- Auth: a bearer token equal to `Relay.Key` (constant-time compare) authenticates as a synthetic
  device `{Id: "relay", Name: "Relay"}` that is not stored in `devices.json` and cannot be revoked
  from the devices list (rotate the key instead). The two places that consult the device list for
  live sockets, `StillPaired` and `Revoke`, treat the relay device as always paired, so its sockets
  are not closed on connect and `DELETE /devices/relay` is a 404. Everything else is unchanged.
- The Caddyfile has no access logging (checked 2026-10-30), so the `?token=` query on the sockets is
  not written to disk on the VPS; keep it that way for this vhost.
- Requests that arrived through Caddy (`X-Forwarded-For` present) get 404 for `POST /pair` and for
  `/admin/*`, so the public URL has no guessable pairing code and no admin surface. LAN clients are
  unaffected.
- Origin check: today a WebSocket upgrade with an `Origin` must equal `http://` + `Host`. Through
  Caddy the page's origin is `https://sessiondeck.0a.gg`, so the expected scheme comes from
  `X-Forwarded-Proto` when present, else `http`.
- Settings window, Phone section: a "Remote" group with the public URL, a password box for the relay
  key with "Generate" (32 random bytes, hex) and "Copy" buttons, an on/off toggle, and a status line fed from
  `relay.json` (`connected since …` / `down: reason`). Saving applies like other gateway changes
  (the gateway restarts).

### Page (`wwwroot/mobile.js`, decided)

- The attach socket URL uses `wss:` when `location.protocol` is `https:`; today `ws://` is hardcoded.
  This is the only page change; it ships in the next deck release.

### iOS app (decided)

- `ios/build.sh` writes `ios/SessionDeckMobile/Generated/RelaySecrets.swift` before `xcodegen`, from
  `~/.sessiondeck-mobile/relay.env` on the Mac (`RELAY_URL=https://sessiondeck.0a.gg`,
  `RELAY_KEY=…`). Without the env file it writes `nil` values, so a clean checkout still builds and
  behaves as the LAN app. `ios/SessionDeckMobile/Generated/` is gitignored.
- `Pairing` gains a scheme so `baseURL`/`wsBaseURL` can be `https`/`wss`; the Keychain record stays
  readable (missing scheme = `http`).
- With `RelaySecrets.url` set, `GatewayStore` starts with a fixed pairing (host from the URL, port
  443, scheme https, token = relay key, deviceId "relay") and never shows the pairing screen. A 401
  shows "Relay key rejected" with Retry instead of forgetting a pairing. The web view loads
  `https://sessiondeck.0a.gg/mobile/mobile.html`; the page's socket follows the page's scheme.
- The `…` menu shows the relay host next to the build stamp so a screenshot says which path is in use.

## Security notes

- The relay key is 256 bits; the only public surfaces without it are `/health` (204) and `/mobile/*`
  (static files already in the public repo). Pairing and admin are closed on the public path.
- The tunnel key can only open 127.0.0.1:8103 on the VPS; the VPS can only reach Talon's gateway
  port through that socket. fail2ban already watches sshd on 2222.
- Rotating: generate a new key in Settings, update `relay.env` on the Mac, rebuild the app.

## Verification (the deliverable is this list passing)

1. From the Mac: `curl https://sessiondeck.0a.gg/health` → 204; `/sessions` with the key → 200 and
   the live session list; without → 401; `POST /pair` → 404.
2. iPhone with Wi-Fi off, on cellular: the app lists sessions, opens one, tap-to-type lands in the
   host's scrollback (read with a passive attach), key bar, start a Terminal session from the `+`
   menu, load header updating.
3. Resilience: kill the ssh child on Talon → `gateway.json` says `down`, then `connected` again
   within 60 s and the app's banner recovers by itself. Hard-kill (`taskkill /f`) → reconnect
   succeeds once sshd's keepalive frees 8103 (under 2 min). Replace the gateway (deck update) → no
   orphaned ssh, 8103 re-bound by the new child.
4. Repo hygiene: `git grep` for the key and the URL credential finds nothing; `Generated/` ignored.

## Left to the builder

- Exact job-object plumbing for the ssh child; whether `RelayTunnel` shares code with `ConPty`.
- Backoff constants, log wording, how the Settings status line refreshes (poll `gateway.json` on a timer is fine).
- Where the health probe's HttpClient lives and how it is disposed with the gateway.
- Test shape for `RelayTunnel` (fake process runner) and for the forwarded-request rules in `GatewayServerTests`.
