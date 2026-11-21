# SessionDeck — agent guide

Windows system-tray app (**.NET 10, WPF + [WPF-UI](https://github.com/lepoco/wpfui)/Fluent**) that
lists live local Claude Code **and Codex CLI** sessions, read from `~/.claude/` and `~/.codex/` on
disk. `README.md` is the user-facing feature tour; this file is for working *on* the code.

**Two exceptions to "all local".** In direct-login mode, the footer's plan-usage meters come from a
network call (`UsageApi` → `GET api.anthropic.com/api/oauth/usage`, bearer token read from
`~/.claude/.credentials.json`). CPA mode reads the selected account and meters from the sanitized
loopback dashboard instead. The pull requests pane reads GitHub through `gh` (see *Pull requests
pane*). Everything else is still pure disk reads. See *Usage bar* below before adding anything else
that touches the network or credentials.

## Build, run, verify
```
dotnet build -c Release
bin\Release\net10.0-windows\SessionDeck.exe
dotnet test Tests\SessionDeck.Tests.csproj
```
- It's a **tray app with no console** — failures are silent. After a launch, check
  `%TEMP%\sessiondeck-error.log`.
- Slow recurring work is written sparingly to `%TEMP%\sessiondeck-performance.log`:
  session scans at 1s+, UI application at 250ms+, and Codex probes/UIA sweeps at 2s+. The file rotates
  at 1MB. Keep this thresholded; logging every pass would itself become background I/O.
- The deck's page reports on itself there too (`PageWatch`, the block under `post` in `deck.html`). Every pane
  shares the page's one thread, so a stall anywhere is a stall in all of them:
  - `page-stall 1800ms ran=chat-paint=1650ms/21@<host> fit=90ms/20@<host>`: the thread was held 500 ms or
    more. `ran` adds up what the page's own handlers took inside it, by handler (`msg-<type>`, `chat-data`,
    `chat-paint`, `fit`): the time in all, how many runs, and the pane of the longest run. `ran=-` means the
    time went to the browser (style, layout) or to xterm's parsing, which runs on its own timers.
  - `tab-switch total=..ms queued=..ms handler=..ms frame=..ms pane=<host> kind=chat|terminal`: every layout
    that changed which pane a column shows, quick or not, from the app posting it to the page having drawn the
    frame after it. `queued` is the wait before the page took the message (the thread was busy: look for a
    `page-stall` beside it), `handler` the page's own work on it, `frame` the time to the second frame from
    there. All three small while the pane still looked stuck means the stall was in drawing to the screen, past
    the page; that is why the quick ones are logged too. `page-layout` is the same for a layout that changed
    nothing on show, logged from 300 ms.
  - `page-silent 3000ms waiting-for=page|frame ready=yes post=ok Renderer:1234=alive cpu=98% ws=700MB ..`: the
    app's side, for a page that cannot report on itself. The newest layout has gone unanswered for 3 s (and once
    more at 15 s). `waiting-for=page` means the page's thread never took the message (held, or gone);
    `waiting-for=frame` means it took it and no frame came. Each browser process follows with its CPU over one
    second: a renderer at a full core is spinning, an idle one is waiting on something, `=gone` is gone.
  - `page-process failed kind=.. reason=.. exit=..`: one of the browser's processes failed (`ProcessFailed`).
    A renderer that exited is loaded again (`reloading`), at most three times in five minutes.
  - `page-frame 140ms work=3ms raf=10ms layout=120ms blocked=90ms scripts=..`: a frame this thread took 100 ms
    or more over (long-animation-frame), split into what ran before the frame, its callbacks, and style and
    layout, with the three longest scripts (each with its `[file:function@position]`) and, as `ran=`, what the
    page's own timed handlers took of it and for which pane (from 10 ms). A frame whose callback is in `xterm.js`
    is a terminal drawing; `ran=chat-paint=..@<host>` is that chat. A slow `tab-switch` with no `page-frame`
    beside it waited past this thread. `page-pace median=..ms max=..ms` follows a switch whose frame took 60 ms+: the gap between the next
    eight frames, which says whether frames are slow to come at all. `app=` on a `tab-switch` is the app's own
    handler around the post.
  - `page-visibility hidden|visible` (a hidden page is given no frames, and draws what it missed when it is
    back), and `pane-socket lost host=..` then `pane-socket back host=..` (a pane's socket to its host dropped;
    the pane reconnects itself, and is logged once however often it has to ask).
- DWM investigation is opt-in and documented in `docs/dwm-diagnostics.md`. The diagnostic launcher
  separates Mica from recurring work, records one-second DWM/app telemetry, and exits only the test app
  if the known sustained-DWM-CPU/resident-growth signature appears. Never enable that logging normally.
- The diagnostic launcher can run `Normal` with `SD_EXPERIMENT_LOG` set: telemetry then records the
  production behavior without enabling any isolation switch. Its paired health watcher records DWM,
  Terminal, system, and monitor-process counters once per minute and exits with the monitored PID.
- Iteration loop that works well here: kill the running exe → build → launch → **screenshot to
  verify** (the UI is the spec; `PrintWindow` with flag `2` captures it). `--list` / `--windows` dump
  the session list / focus mapping to `%TEMP%` for headless checks.
- Source files are **CRLF** (Windows). Keep them CRLF when editing.
- Settings persist to `%APPDATA%\SessionDeck\settings.json` — **shared across builds**, so a
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
6. A background **MTA thread** tags each row with its virtual desktop via `VirtualDesktop` +
   `WindowActivator.ResolveWindows`. The UIA-heavy sweep runs only while visible, skips active session
   scans, normally waits 15s, and backs off to 30–60s after slow UIA or discovery passes.
7. `CodexScanner.Scan()` appends live Codex sessions to the same list (see below). Rows are told apart
   only by the provider mark in the model pill — sort, focus, columns and tooltips are all shared.

## Codex (`CodexScanner.cs`, `FileHolders.cs`)
Codex publishes **no live registry**. There is only the append-only rollout per thread,
`~/.codex/sessions/YYYY/MM/DD/rollout-<ts>-<id>.jsonl`, and everything has to be derived from it.
Four things will bite you, in rough order of how long they take to notice:

- **`codex resume` appends to the ORIGINAL file.** The filename timestamp, the day folder, and the
  creation time all describe when the thread *started*, not whether it's running. A thread from three
  weeks ago can be the live one. Never infer liveness from any of them.
- **Liveness = who holds the file open.** `FileHolders.QueryOwner` (Restart Manager `RmGetList`) answers
  it exactly, non-admin, and hands back the `codex.exe` PID — which is also what makes double-click-to-
  focus work, since `WindowActivator` is provider-agnostic and just needs a PID. It costs ~50ms a call,
  so it lives on the `codex-probe` background thread with a per-pass budget; `Scan()` on the UI tick only
  reads the map it produces. New/growing candidates keep priority, while up to eight known owners are
  revalidated per pass on a rotating 30s cadence. A confirmed miss still needs two checks before
  removal. Restart Manager failures are **indeterminate**, not misses: known rows retain their live PID,
  while new candidates back off and retry without caching a failed attempt. Startup crash recovery
  bypasses this cache and directly verifies each saved Codex session. Don't move a probe onto the UI
  thread or let known-owner verification exceed the shared budget.
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

## Usage bar (`UsageApi.cs`, `CpaUsageApi.cs`, `AccountScanner.cs`)
The second footer bar: signed-in address on the left, one fill-behind pill per plan limit on the right.
- **CPA mode is selected automatically** when `ANTHROPIC_BASE_URL` points to loopback port 8317 (or
  `SD_CPA_USAGE_URL` is explicitly set). `CpaUsageApi` polls the sanitized local dashboard at
  `http://127.0.0.1:8318/api/usage` every 20s and shows only the row named by `serving.account` — the
  account CPA is actually routing, not merely the highest-priority row in its pool. It never opens
  CPA auth files or receives a token. `SD_CPA_USAGE_URL` can redirect the dashboard endpoint.
- **CPA failures retain the last good selected account and visibly age it.** Five minutes is stale:
  the dashboard attempts to refresh its cache after two minutes, so this represents more than two
  missed refresh opportunities. Before the first good snapshot, show `CPA account unavailable`
  instead of falling back to the unrelated direct-login identity. CPA mode does not start the
  direct credentials watcher.
- **Without CPA**, the established direct-login behavior below remains unchanged.
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

## Pull requests pane (`PrPaneController.cs`, `PrSource.cs`, `WorktreeIndex.cs`, `PrAttribution.cs`, `PrBoard.cs`)
A deck column kind (`DeckGroup.IsPrPane`, saved as a spacer with `Panel: "prs"`) showing the `gh`
user's open PRs. Design: `docs/superpowers/specs/2026-09-29-pr-pane-design.md`.
- **Network:** `gh api graphql` only, through `GhCli`, never on the dispatcher; every 60 s while
  visible, 5 min hidden, on show and on "Refresh now". Read-only on GitHub.
- **Mergeability is lazy on GitHub's side:** `UNKNOWN` PRs are re-read once ~3 s later.
- **Git runs with `GIT_OPTIONAL_LOCKS=0`** so a status refresh never takes `index.lock` from an
  agent working in that worktree.
- **Attribution is a heuristic:** 3+ tool calls touching a PR's worktree path, link or `gh pr`
  number among a live session's last 100 (plus recent subagents and folded threads). Registry `cwd`
  is useless for this: every session registers the folder it was started in. Headless threads
  `Fold` cannot place are handed back as unowned and listed as not-clickable agents.
- **`--prs`** dumps the board to `%TEMP%\sessiondeck-prs.json`; `tools/pr-pane-check.ps1` compares
  it with `gh search prs`. Use it before theorising about a wrong row.
- **Page messages without a terminal `id`** go to `DeckBrowser.PageMessage` → `DeckPane.RoutePage`.

## Chat sessions (`Host/ChatStream.cs`, `Host/PipeChild.cs`, `wwwroot/chat.js`)
A chat session is a Claude session with a conversation pane instead of a terminal. Claude only;
default login only; desktop only. Measured against Claude Code 2.1.322.
- **Same host, different child.** `HostSpec.Kind = "chat"` makes the host start the CLI on plain
  pipes (`PipeChild`) instead of a pseudoconsole, as `claude -p --input-format stream-json
  --output-format stream-json … --permission-prompt-tool stdio`. No shell wrapper and no hook
  settings: `HostManager.NewClaudeChatCommand` names `claude.exe` in full, and the stream says what
  hooks would. `HostRecord.Kind`/`SavedSession.Kind` carry it through restart, resume and reboot.
- **The ring holds the conversation, one JSON line each.** `ChatStream.Durable` keeps `user`,
  `assistant`, `result` and control lines; token deltas (`stream_event`) and per-turn `system`
  lines go to attached viewers only, as `{"type":"live","line":…}` text frames, so they never
  push the conversation out of scrollback.
- **Status comes from the stream** (`ChatStream.Apply`): a `system init` or replayed user line is
  busy, an open `can_use_tool` request is waiting, `result` is idle. A message sent mid-turn is
  folded into the running turn and gets no `result` of its own.
- **A viewer answers permission requests; the host declines every other control request**
  (`ChatStream.Decline`), because an unanswered one stalls the turn.
- **The host asks the CLI what it offers and what is in force, once, for every viewer.** At start
  it sends `initialize` (id `sd-initialize`) and `get_settings` (id `sd-settings`); the answers
  become a `catalog` frame (models, slash commands, cut to what a picker and a menu show) and a
  `settings` frame (permission mode, model, effort), sent on change and at attach. A viewer changes
  one with `set_model`, `set_permission_mode` or `apply_flag_settings {effortLevel}` under an id
  starting `sd-set-`; the host asks `get_settings` again after it and after every `result`, which
  is how a typed `/model` or `/effort` is picked up.
- **An answer to a control request is kept in the ring only when it settles a permission
  request** (`ChatStream.Durable` is asked before `Apply`). The rest are live-only, and the host's
  own two are not sent to viewers at all: `initialize` alone is about 30 KB.
- **Image bytes never enter the ring.** A pasted image goes to the CLI as a base64 image block in
  the user message; `ChatStream.WithoutImageData` empties `source.data` (leaving `omitted`) on the
  echoed line and on history read from the transcript. The pane shows its own preview for what it
  sent and "1 image attached" for anything else.
- **A plan's text is in the request, not the call.** `ExitPlanMode` arrives as a `tool_use` whose
  input can be `{}`; the plan (`plan`, `planFilePath`) is in the `can_use_tool` request, and for a
  conversation read back from the transcript it is in the approval's `tool_result` after
  `## Approved Plan:` (`planText` in `chat.js`). Approving sends `allow` with
  `updatedPermissions: [{type:"setMode", mode, destination:"session"}]`; "keep planning" is a
  `deny` with a message. `bypassPermissions` can be set this way only in a session launched with
  it, so it is offered only as the mode the session left (`resume` in the settings frame).
- **"Always allow" is the CLI's own suggestion sent back.** A `can_use_tool` request may carry
  `permission_suggestions` (`setMode acceptEdits` for edits, `addRules` with a destination for
  commands); the card offers it only when `alwaysOffer` can say in words what it does, and answers
  with those suggestions as `updatedPermissions`. The host reads a `setMode` out of the echoed
  answer so the mode picker is right at once.
- **Tool calls are drawn inside runs.** In `ChatModel` a call is not a row: consecutive calls
  share a `tools` item (`AskUserQuestion`, `ExitPlanMode` and `Agent` stand alone), and `touch`
  repaints the row that holds the call. Tests reach calls through their runs.
- **A subagent's work arrives under its call's id.** Its own calls and their results are
  `assistant`/`user` lines whose `parent_tool_use_id` is the `Agent` call; `system`
  `task_started`/`task_progress`/`task_notification` lines name the call in `tool_use_id` and say
  what it is doing (`description`, `usage.tool_uses`). All of them are durable, so a viewer that
  attaches later draws the same thing. `ChatStream.ForRing` cuts what a subagent read and wrote to
  its first 1500 characters and drops the structured copy of every tool result
  (`tool_use_result`), which is megabytes nobody reads. The pane keeps them as `children` of the
  call. A subagent sent to the background has its call's result at once and goes on until its
  `task_notification`, across the end of the turn (`taskRunning`). There are no token deltas for
  a subagent.
- **Earlier conversation is paged from the transcript, on request.** The ring starts with the
  newest of the transcript that fits half of it (`ChatTranscript.Before`); the snapshot says
  `earlier` when there is more (or the ring has since dropped its oldest lines). A viewer asks
  `{"type":"history","request":id,"before":N,"uuid":…}`: `N` is the `at` (byte offset) of its
  oldest history line, and a line that arrived live has only its `uuid` (stream lines and
  transcript lines carry the same one). A place names a message only in the file it was read
  from, and the CLI can take a new session id under the host, so the host checks the uuid at that
  place, in the file in use and then the one it started with, and falls back to finding the uuid
  (`ChatTranscript.BeforeMessage`). The host answers that viewer alone with `{type:"history",
  request, lines, next}`. `ChatModel.prepend` puts a page ahead of what is shown, gives a call the result
  that was already on the page (`orphans`), and joins a run the page boundary cut in two. A call
  read from the transcript with no result is `stopped`, never running: its process is gone.
- **A chat host can be behind without the protocol changing.** `ChatStream.Revision` goes in the
  record (`ChatRevision`) and the snapshot (`chat`); bump it when the pane starts to rely on
  something new from the host. `SessionRow.HostBehind` is true for a lower one, and the deck's
  idle auto-restart moves the session to a current host, once per revision
  (`AutoRestartLedger.ChatHost`). The restart hands on what the old record says: the model on
  the command line, last, because the CLI takes the last `--model` (measured), and the mode and
  effort as `HostSpec.RestoreMode`/`RestoreEffort`, which the host sets right after `initialize`.
  A record written before revision 3 has no mode or effort to hand on.
- **A chat is resumed whenever its transcript exists** (`SessionScanner.CanResume`), not only once
  something was said. On pipes the CLI keeps a transcript that a slash command alone wrote
  (`queue-operation`, `system local_command`, no `user` line); `--session-id` under that id is then
  refused with "Session ID … is already in use", while `--resume` works and takes messages
  (measured, 2.1.323: set the file aside and `--session-id` works again). A terminal still resumes
  only with a `user` line.
- **`/resume` is the host's, not the CLI's** (`Host/ChatSessions.cs`, `ChatSupervise` in
  `HostProgram.cs`). On pipes the CLI refuses it (`commandOutcome: unavailable_headless`): its
  picker is drawn in a terminal. So the pane offers the command itself to a revision 4 host and
  never sends it. `{"type":"sessions","request"}` lists the conversations in the session's
  project folder (every transcript with something said in it, newest first, named by
  `custom-title`, else `ai-title`, else the first prompt; only the two ends of each file are
  read), marking ones another live CLI has open (`~/.claude/sessions`). They stay pickable:
  resuming a conversation that is still open in a terminal works, as does resuming one a terminal
  session wrote (measured, 2.1.323).
- **A chat moves to another conversation by restarting its CLI under the same host.**
  `{"type":"resume","request","session"}` is refused with a `resumed {ok:false,error}` frame when
  the id is not a session id, has no transcript in the folder, is the one open, or a turn is
  running. Otherwise the host ends the CLI and starts it on `HostSpec.ChatResumeCommand` (the
  spawner's resume form, `{SD_CHAT_SESSION}` replaced; the launch command is never edited) with
  the chat's model last and its mode and effort put back. The host id, port, tab and viewers
  stay.
- **Nothing changes for a viewer until the new CLI introduces itself.** A CLI that cannot resume
  says one `result error_during_execution`, the reason on stderr, and exits; a healthy one
  answers `initialize` first (`ChatStream.Introduces`). Until that answer the conversation is
  unproven: its lines are dropped, and if it ends, the conversation before it is started again
  with a line saying why. On the answer the host serves it: the record names it, the ring is
  emptied (`Ring.Clear`, sequence numbers go on) and seeded from its transcript, and every viewer
  gets a snapshot with a new `epoch`. A viewer starts over on a new epoch even when `start`
  equals the byte it had reached (`startsOver` in `chat.js`); `cut` says whether the bytes begin
  mid-line, since after an emptying `start > 0` no longer means that.
- **A `kill` is said before the CLI is ended** (`_killRequested`): otherwise the CLI going away
  could be read as making way for another conversation.
- **The deck follows a host whose conversation changed.** `TerminalView.Follow` takes each new
  reading of the record, so a tab's tooltip and "end session" name the conversation open now, and
  `SessionRegistry.LeftBehind` takes the one a chat host moved away from out of the saved set at
  once (it would otherwise come back after a reboot inside the ten minutes a vanished session is
  kept).
- **Two hosts can be on one conversation**, a chat moved to one a terminal session still has open.
  The CLI allows it (both write the transcript) and the picker marks it. There are then two
  scanned sessions with one id, and `HostPairing` gives each host the one running under it first;
  taken in order, each row showed the other's name and status. The saved set and a session group
  are keyed by session id, so they hold it once.
- **The pane has its own colours, by role** (`DESIGN.md`, tokens at the top of `chat.css`): a dark and a
  light set chosen by `data-scheme`, which the shell sets from the terminal scheme with `--chat-alpha`
  (`applyLook`). No hex outside the token block; a block is a step lighter than what it sits on
  (`--chat-well`), and only a control or a card that waits for an answer has a border.
- **The column's width is the person's.** A thin grip at each edge of the conversation column
  (`.chat-grip`, shown while the pointer is over the pane, desktop only) drags `--chat-wide`; the
  column is centred, so either grip moves both. The width is one for every chat on the page
  (`setWide` tells each pane), kept in `localStorage` under `sd.chat.wide`, and a double click
  goes back to the stylesheet's. A chosen width also lifts the 74ch limit on a paragraph
  (`.widened`), or the text would not get wider with its column.
- **A Codex chat is the same pane over a translator** (`Host/CodexChat.cs`, measured against
  codex-cli 0.167.2). The child is `codex app-server` on pipes: JSON-RPC, one object per line
  (`initialize`, `thread/start|resume`, `turn/start|steer|interrupt`, `thread/list`,
  `thread/turns/list`, `model/list`; `codex app-server generate-ts --out <dir>` prints every
  type). `CodexChat` turns its notifications into the lines a viewer already reads from Claude
  (user, assistant, `tool_use`/`tool_result`, `control_request`, `result`) and a viewer's lines
  into requests, as a list of effects the host carries out (`HostProgram.Agent`; the translators
  share `IChatAgent` in `Host/ChatAgent.cs`). It is pure and tested on its own; `ChatHostTests`
  runs the real host against a script standing in for the app server.
  The agent's pipe, each viewer and the once-a-second clock ask on threads of their own, so
  `HostProgram.Agent` works out and shows one thing at a time (`AgentOrder`): without it a line
  could land in the conversation after what followed it (an approval's answer after the reply it
  led to, when the agent answered at once). What goes to the agent is written last and
  outside that lock, because a write to a pipe waits while the agent is not reading and the thread
  that reads the agent must never wait behind it.
- **What differs from Claude, by measurement:** the thread id is the app server's to name, so
  the record's `SessionId` is empty until the thread is open; one process starts, resumes and
  lists threads, so `/resume` and earlier history are requests and never a restart
  (`thread/turns/list` pages newest first behind an opaque cursor, kept per page by its first
  message's id); model, effort and approval policy go with each `turn/start`, so a picker change
  is in force from the next turn; a message typed mid-turn is `turn/steer`; images are data
  URLs; an approval is a JSON-RPC request the host answers `accept`, `acceptForSession` or
  `decline`, and any other request from the server is answered with an error at once (unanswered,
  it holds the turn).
- **Codex has two modes here: ask first, or full access** (`CodexChat.Ask`/`Full`, named as
  Claude's `default`/`bypassPermissions`). The sandbox is off in both: on Windows its
  restricted-token sandbox refuses to run any command in `workspace-write`. A new chat takes its
  mode from `Settings.CodexFlags` (`HostManager.CodexChatMode`). The catalog frame carries
  `agent` and `modes`, which the pane uses for its placeholder and mode picker.
- **Antigravity and OpenCode chats are one more translator** (`Host/AcpChat.cs`, measured
  against agy 1.5.8 and opencode 2.2.6). Both have an `acp` subcommand: the Agent Client
  Protocol, JSON-RPC 2.0 on pipes (`initialize`, `session/new|load|list|prompt|cancel`,
  `session/set_config_option`, `session/update` notifications, `session/request_permission`
  from the agent). The model, effort and mode pickers are the agent's own `configOptions` by
  category (`model`, `thought_level`, `mode`), so nothing about either agent's models is in
  this repo. The deck starts `"<agy.exe>" acp --dangerously-skip-permissions` and
  `"<opencode.exe>" acp` (`HostManager.AgyChatCommand`/`OpenCodeChatCommand`; with that flag
  Antigravity sends no `session/request_permission`, so an AGY chat started here never asks; the npm
  `opencode.cmd` shim is followed to the exe behind it). The same command starts a conversation and comes back to one: the session id goes in
  `HostSpec.SessionId` and the translator loads it.
- **What ACP does not have, by measurement:** no part of a conversation on request
  (`session/load` replays the whole of it, and the scrollback takes its end, half the ring); no
  adding to a running turn (a message typed meanwhile is the next `session/prompt`); no
  transcript path, so an ACP chat is not restarted onto a new build by itself. A loaded
  conversation comes without its name, which is then read from `session/list`.
- **A replay can outlast the answer to `session/load`.** Antigravity answers before it has finished
  replaying a long conversation (measured on 1.5.13 with 2,374 messages: 1,073 before the answer, 1,301
  over the next half second), and nothing marks the end of a replay. For `ReplayQuiet` (1 s) after the
  last of it, the conversation's messages and calls are still the past (`AcpChat._tailing`): kept as
  read back when it goes quiet (`EndTail`), timed after the first part, never shown as being said, and
  no prompt is sent meanwhile. Before this the second half read as said now: "working" for 30 s after a
  restart, every message stamped with the restart. Names, settings and context use are taken as they
  come. Not covered: a message cut in two where the answer arrived, and subagent lines for calls in the
  late part. This did not get a `ChatStream.Revision` bump: the revision is one number for every kind
  of chat, a bump restarts every idle Claude chat, and only reopened Antigravity or OpenCode chats
  differ; a revision per translator is what would say this properly.
- **An earlier page comes from the host, not the agent.** `AcpChat` keeps every line of the open
  conversation (`_log`: the replay whole, then each line as it is kept, stamped by `Logged` at
  every entry point), up to `LogMost` (64 MB, then the oldest go). `History` answers with the
  page before the message a viewer names by `uuid`: at most `PageLines` lines and `PageBytes`
  bytes, never none while there is one. So no two lines have one id: a call's result is
  `<call>:r`, and a message the agent gave an id that is kept in two parts gets a second id
  for the second (`Flush`). `ChatSeed.Earlier` is true when the replay was cut.
  A page, and what the scrollback starts with, begins at a message of the conversation itself
  and holds the call of every subagent line in it (`Whole`, and the cut at `session/load`): a
  viewer moves on from a page by the first such message in it, so a page of subagent lines
  alone would be asked for again and again, and it shows a subagent's line only under a call
  it has. That can make a page as long as a subagent's whole work. Not covered: lines that
  leave the ring while a session runs can leave a viewer that attaches later with subagent
  lines whose call is in the page before.
- **What Antigravity keeps on disk, and what is read from it** (`Host/AgyDisk.cs`; measured on
  1.5.13, with `C:\Temp\chatlab\acp-subagent-files.mjs` and `acp-replay-vs-disk.mjs` on the
  machine it was measured on). Under `%USERPROFILE%\.gemini\antigravity-cli\brain\<conversation>\.system_generated`:
  `logs\transcript.jsonl`, one JSON object per step (`USER_INPUT`, `PLANNER_RESPONSE`, `GENERIC`
  for what a call came back with, `SYSTEM_MESSAGE`), appended when the step happens and never
  rewritten, each with `created_at` to the second; and `subagents\<id>.json` for every subagent
  the conversation started, each subagent being a conversation with a transcript of its own.
  The format is the agent's own and is read as such: only a UUID is ever made into a path, a
  line that does not parse is skipped, and nothing found means today's behaviour, not an error.
  `HostRecord.TranscriptPath` stays empty for these chats: the app restarts a chat that has
  one after an update, which would end the agent's timers and subagents.
  - **Times** (`AgyTimes.Stamp`, at `session/load`): each replayed message gets the time of the
    step that holds its words. What was typed is found between the `<USER_REQUEST>` marks (the
    agent adds its context after them and can append to the request itself); what the agent said
    is a step's `content`. The replay runs together what the agent said in steps that had only a
    system message between them (a timer's reply 40 s after the message that set it): those are
    cut apart again, each with its time (`<uuid>.1`). A wrong time is worse than none, so the
    search only goes forward, never past the next thing asked, short words are matched whole or
    from the start and never inside longer ones, a time is never earlier than the one before it,
    and a line whose words are not found keeps no time. OpenCode's replay is left as it comes.
  - **Subagents.** `invoke_subagent` ends within a second and nothing about the subagent is
    ever said over the protocol; its steps are on disk within a second of happening.
    `AgyWatch` (a timer of its own in `HostProgram`, once a second, files read outside every
    lock) hands what is new to `AcpChat.Outside`. A subagent is put under the oldest call still
    without one whose role is its own (`Claim`). The two wait for each other for `SpawnWait`
    (60 s; the file is there 0.1 to 1.6 s after the call is announced): a call that failed, or
    that nothing came for, started nothing, and a subagent no call came for is not this
    conversation's to show. Without that limit one unmatched call or subagent would shift every
    pairing after it by one. A subagent is shown as Claude's are: the call reads as
    `Agent` (role, task), its calls and words are lines with `parent_tool_use_id`, and
    `task_started` / `task_progress` / `task_notification` say whether it is at work. Its file
    says `ALIVE` for ever (it can be spoken to again), so its turn is over when its last step
    is words with no call; `KILLED` ends it too. While one is at work and no turn runs the
    session is `scheduled` (`Rest`); when one has its say it has reported to the agent, which
    speaks a second or so later, so the session is busy at once and ends on the usual quiet;
    one that writes nothing for `HelperQuiet` (30 min: a step is written when it ENDS, so a long
    command is silence) is no longer waited on. Only subagents that appear while the process is
    watching are followed: files of an earlier process still say `ALIVE`.
    What a subagent's call came back with belongs to a call of the step just before it (the
    agent speaks again only when every call of its last step is back).
    A reopened conversation shows what its subagents did, read from the same files (`Helped`),
    only when every such call has its subagent and the roles agree; none is shown as at work.
    Moving to another conversation (`Resume`) sets the open one's calls aside and keeps
    following its subagents until the other one is open: if it cannot be opened, the timers
    and subagents of the one still open are as they were.
    Not covered: a subagent's own subagents (they show as its calls), its thinking, and
    `/swarm`, `/teamwork` and `/boost`, which were not run (Antigravity marks them quota-heavy).
- **Quirks each agent has, each with a test in `AcpChatTests`:** Antigravity lists every
  folder's conversations whatever folder is asked for, writes a real folder as `/C:/…`, and
  gives the ones it has no folder for its own working folder, so where any folder is written
  that way only those are listed; it wraps a command's output in a "Created At… Command
  output:" header. OpenCode lists a hundred at a time and names a next page after the last;
  announces a call before it knows what the call is; ends the turn as `cancelled` when a call
  is declined (not shown as interrupted); runs a call it was waiting to ask about once an
  "always" given meanwhile covers it, without taking the question back (the card is closed
  when the call ends); keeps an "always" for the folder past the session (the button says "in
  this folder"); and can fail `session/new` once (the next message asks again). OpenCode's
  default model is a paid one: test with a free model set through `RestoreModel`.
- **`/rename <name>` is the host's, and never a turn** (chat revision 5). The pane sends
  `{"type":"rename","request","name"}` instead of the text (`renameAsked` in `chat.js`; a bare
  `/rename` only says how to use it), the host answers that viewer with `renamed`, and every
  viewer hears the name as a `title` frame. Where the name is kept depends on the agent:
  Claude is asked with the CLI's own `rename_session` control request (`source: "host"`,
  `session_id` so a CLI that has moved on refuses; it says `session_title_changed` and then
  answers, trims to 200 characters, and writes `custom-title` to the transcript); Codex with
  `thread/name/set`; the Agent Client Protocol has no rename, so for Antigravity and OpenCode
  the name is kept by conversation under `chat-names/` in the data folder (`Host/ChatNames.cs`,
  one file named by a hash of agent and conversation id) and wins over the agent's own title in
  the tab, after a reopen, and in the `/resume` list. Typed as a message instead, `/rename x`
  is a free local command to the Claude CLI but a prompt, and a turn, to the other three. A
  host older than revision 5 would drop the frame, so it is still sent the text, and a Codex,
  AGY or OpenCode chat started before this build spends a turn on it until it is restarted. A
  Claude chat renamed before anything was said has no transcript for the name to be in, so a
  restart that starts it fresh hands the name on with `--name` (`HostManager.NameCarried`). An AGY
  chat's row is named by its host, not by a `/rename` once typed into an AGY terminal
  (`SessionPipeline.ApplyAgyNames` leaves chats alone).
- **An ACP agent can go on after it has ended its turn, and the host has to notice on its own.** Antigravity
  answers `session/prompt` with `end_turn` and then keeps working: tool calls, thinking, and a closing message
  (measured in a live session, 1.5.13: `end_turn` at 14:06:40, calls until 14:07:00, then the message). A run of
  text has no end mark in the protocol and is kept when something else follows it; outside a turn nothing does.
  That message was streamed to whoever was watching and stayed out of the scrollback for 38 minutes, until the
  next prompt flushed it, after the person's own question. A phone, which reads the scrollback, never saw it.
  So the host tells the translator that time is passing (`IChatAgent.Tick`, once a second from `HostProgram`):
  - Outside a turn, text or thinking with nothing heard for 2 s becomes a line (`AcpChat.SaidQuiet`).
  - Work begun outside a turn (a chunk that says something, a call that starts) makes the session busy again,
    with the same live `system/init` a turn starts with, and is ended by a `result` line once nothing has
    been heard for 30 s with no call running (`WorkQuiet`), or 10 minutes with one (`CallQuiet`). A call that
    only reports its end after the turn, and news that is not work (commands, settings), start nothing.
  - A prompt sent meanwhile flushes what was being said first, so the kept order is the order it was read in.
  - Inside a turn nothing changes: a pause there is not an end, and the turn's own answer closes the run.
  - Antigravity's `schedule` call is its timer and its wait on a background task both (measured: a one-shot
    timer set by `/schedule` is a `schedule` call still `in_progress` at `end_turn`; 45 s later the call
    reports `completed` and the agent speaks, unprompted, in the same process). A turn that ends with one
    running leaves the session `scheduled`, not idle (`AcpChat.Waking`), as a Claude turn with background
    agents does. Its firing counts as the agent starting work (busy at once, not after the few seconds it
    takes to speak), and a timer still waiting does not count as a call running, so the work one timer
    started ends on the usual quiet and the session rests `scheduled` on the next. A timer read back from
    an earlier process is not waited on: the timer lived in that process. The pane leaves a `Schedule`
    call running at the end of a turn instead of marking it stopped (`OUTLIVES_TURN`).
    Ending the host ends its agent's timers. The restart of idle sessions after an update never
    touches an Antigravity chat (`SessionScanner.CanResume` is false without a transcript the app
    reads), but a deck that starts again brings a saved chat back on a new agent process, and there
    Antigravity replays the call that was still waiting as failed (measured in the deck rig).
    When a session ends the pane marks every call still running as stopped (`ChatModel.exited`): a
    call cut off mid-turn, a timer, a subagent left in the background.
  A viewer that attaches in the middle of a turn is told what has been said so far (next bullet).
- **Every viewer shows the same, whenever it attached** (`Host/ChatLive.cs`, chat revision 7). The desktop
  and the phone are two viewers of one host, and a viewer shows the scrollback plus whatever it was sent
  live while attached. Anything that reached the screen live and never became a line (a reply cut off by an
  interrupt; on hosts before v0.5.102 everything an agent said after its turn) stayed on the viewer that
  watched and never reached one that attached later: that was "the phone does not show what the desktop
  shows". The rule now: **the scrollback, plus what `ChatLive.Now()` says, is everything a viewer can show.**
  - `ChatLive` follows the pane's own rules for a message being said (`live` and `assistant` in chat.js): it
    begins at `content_block_start`, grows with each delta, and is over when a kept line of its kind
    arrives. It is read and written only under `ClientsLock`, the lock the ring is appended under and a
    viewer attaches under (`AppendChatLine`, `SayLive`, the attach path), so it and the ring are one state.
  - A viewer that attaches gets, after the scrollback, each message being said, whole, and for a translated
    agent the line that says it is at work.
  - A turn that ends (`result`, or the line that says it was interrupted) or a session that ends with a
    message still being said keeps it first, as an assistant line marked `partial`.
  - A pane whose socket comes back keeps its model and asks for what follows the last byte it has; it drops
    what it had of a message being said (`ChatModel.attached`), since the host says it whole again.
  - A chat on a host from before revision 7 cannot do this, and nothing restarts an Antigravity or OpenCode
    chat by itself: the pane says so (`.host-behind`) until the session is restarted. **A change to what a
    host keeps or says is a `ChatStream.Revision` bump**: v0.5.98 to v0.5.104 changed hosts without one, and
    chats started before them kept the old behaviour with nothing to say so.
  - Checked with `C:\Temp\chatlab\sync-rig.mjs` on the machine it was written on: the real page twice against
    one real Antigravity host, the second attached at eleven moments; and against the build before, where it
    differs while a thought is being written.
  Not covered: a message still being said when the model starts its next message is neither told to a new
  viewer nor kept (not seen: every block was kept before the next `message_start` in 41 of 41 recorded
  message starts, and none was open at 18 of 18 ends of turn); a call's input being written out is not shown
  by any viewer until the call is kept.
- **An ACP agent's context use comes from the agent, through its host.** The app reads Claude's and Codex's
  from their transcripts; Antigravity and OpenCode have none it reads. Both say `usage_update` (`used` of
  `size`, measured on agy 1.5.13 and OpenCode 2.2.6), after a turn and again while a conversation is
  replayed. The host keeps it on its record (`HostRecord.ContextTokens` / `ContextWindow`, part of
  `ChatStream.Signature` so the record is written), and `SessionPipeline.ApplyHookState` fills a row that
  has no figure of its own; one read from a transcript is never replaced. A count said during a replay is
  held until that conversation is the one served, and a conversation that says none shows none.
- **A link to a file is opened by the deck, by kind, and never run** (`OutsideLink.Decide`). Agents write
  `[name](file:///C:/…)` and `[name](C:/…)` for what they made. The pane used to strip those hrefs (only
  `http`, `https` and `mailto` survived `scrub`), leaving text styled as a link that did nothing. Now, in the
  deck, a file URL or a drive path stays a link and its click is posted like any other; the deck opens a
  picture, video, page, document or inert source file with its default program, opens a folder, and shows
  anything else selected in Explorer. Never opened: programs, shortcuts, installers, and source that
  Windows may hand to something that runs it (`.js`, `.vbs`, `.py`, `.ps1`, `.sh`, `.bat`, `.cmd`). Never
  followed: a share on another machine (asking for one sends this account's credentials there) and any other
  scheme. A file that is not there opens the folder it would be in. A relative path stays text: the pane does
  not know the session's folder. On a phone a file link is text too, since the file is on the computer;
  `a:not([href])` is styled as plain text so nothing looks clickable that is not.
- **`/skills` and `/skills reload` in an Antigravity chat are the host's, and never a turn.** They are the
  two forms agy's own terminal has ("Usage: /skills List available skills /skills reload Reload skills from
  disk", read out of agy 1.5.13); its chat protocol advertises 14 commands and that one is not among them.
  The protocol has no request that re-reads skills, and agy does not watch the disk (a skill written while
  it runs is not announced). What it does do is read them whenever a conversation is attached to, so the
  host attaches to the one already open: `session/resume` with the open session's id (`AcpChat.Commands`).
  Measured on 1.5.13: nothing is replayed, the conversation keeps its context and its settings, and one
  `available_commands_update` follows the answer. The asking viewer is answered then
  (`{"type":"commands","ok","count","added","removed","changed"}`), with the difference worked out from the
  names and descriptions; every viewer gets the new catalog as always. Rules that follow:
  - Offered only where it was measured to work: the catalog says `reload: true` when the agent says it can
    be reattached (`sessionCapabilities.resume`) AND `AcpChat.RereadsCommands` names it. OpenCode 2.2.6 can
    be reattached too and answers with the list it already had (it reads a command's file when the command
    is used, so a new one works at once and only its menu entry is missing until the host restarts): it is
    not offered there, and `/skills` typed into any other chat goes to the agent as words. No
    `ChatStream.Revision` bump: the flag is in the catalog, so only a host started after the update has it.
  - Refused during a turn or while the session opens or moves, and what is typed while the agent has not
    answered waits for it (`_resuming`): only a conversation at rest was measured. `/schedule` and `/goal`
    left running across a reload are untested.
  - A command entry is a name and a description. Nothing says which is a skill and which is built in, so
    the list does not sort them (agy's terminal does: workspace, global, built-in).
  - A reload is about the list. agy reads a skill's body when the skill is used (measured: a body edited in
    an open conversation ran, new, on the next use with no reload), so an edit never needs one. What needs
    one is a skill that is new, gone, or described differently; "Updated" means the description changed,
    and a reload that finds nothing says the list is the same, not that nothing changed.
  - Started with `--dangerously-skip-permissions`, as the deck starts it, agy asked for no approval after
    being reattached (measured: a file read after three reloads).
  - The pane gives up on an answer after 15 s (`skills` in `chat.js`), and the phone's relay lets the
    request through (`AttachRelay.AllowedTowardHost`).
- **A Claude chat whose turn ends with background agents still running is "scheduled", not idle**
  (`ChatStream`, measured on 2.1.325). The CLI says the whole set of running background tasks in
  `system background_tasks_changed` (replace, not delta); agents are `local_agent`, `remote_agent`,
  `in_process_teammate` and `local_workflow`, and anything `ambient` is left out. A background shell
  alone does not hold the session, as for terminals. Lines with a `parent_tool_use_id` are a
  subagent's work and never set busy or the last tool. The set starts empty when the CLI answers
  `initialize`, since it says nothing at startup.
- **`claude-swift` is a built-in Claude profile, `swift`** (`ClaudeWrapper`, `ClaudeProfiles.Wrapper`). The
  provider stays `Claude`; `Profile = "swift"` rides the host record, the saved session, restart and
  reboot restore like an account profile. The script owns the pointing (endpoint, token, model); the deck
  reads from it only `CLAUDE_CONFIG_DIR`, because transcripts and `sessions/<pid>.json` are looked for
  there (`SessionScanner` scans every wrapper folder, `SessionInfo.ConfigDir`; a chat's `HistoryPath`
  and `HostSpec.ClaudeSessionsDir` point there). Rules that keep a Swift session on Swift:
  a wrapper that cannot be resolved is an error, never a fall back to plain `claude` (another account and
  model); its flags are fixed (`--dangerously-skip-permissions`) and the deck's Claude flags are not
  added, since a `--model` there would win; no model is passed on resume (`HostSpec.ChatModelFixed`).
  A chat's child must be a process, so the script runs through `cmd /d /s /c`; a terminal runs it in
  pwsh, which also hands a `.cmd` to `cmd`. Either way `cmd` re-reads the line, so nothing typed goes on
  it: a name only when `HostManager.CmdSafe`, an id only when it is one. A folder the wrapper's config
  has not trusted shows the CLI's own trust prompt in a terminal; that is the CLI's to ask.
- **A chat host runs its CLI once** (chat revision 6). From v0.5.85 to v0.5.91 a Claude chat host also
  started the same command in a ConPTY nobody saw (a dangling `else` when Codex chats arrived): a second
  idle CLI per chat, holding the same session id, and still holding the old conversation after `/resume`.
  `ChatHostTests.A_claude_chat_host_runs_its_cli_once` counts the host's children.
- **A new Claude chat starts on `--model default`** (`HostManager.DefaultChatModel`), unless a model was
  picked or the deck's flags name one (`NamesModel`). Without it the CLI starts on `model` from the
  person's settings, which `/model` rewrites in whatever session it was last used, so new chats followed
  the last terminal. Measured on 2.1.325: the alias resolves to the account default, shows as "Default
  (recommended)" in the picker, and leaves the settings file alone. Only new chats: a resume carries the
  chat's own model, and terminal sessions follow the CLI's settings as before.
- **The advisor works in a chat as the CLI has it** (settings `advisorModel`, or `--advisor`): a server-side
  tool, so it arrives as blocks in the model's own message, `server_tool_use` named `advisor` and then
  `advisor_tool_result` whose content is `advisor_result` (text), `advisor_redacted_result` (sealed) or
  `advisor_tool_result_error` (`error_code`). The pane shows the consult as a tool line of its own
  ("Advisor"), running until the result block comes (`serverResult` in `chat.js`); any other
  `server_tool_use` gets the same treatment under its own name. The host passes these lines through
  untouched.
- **`.chat thead, .chat tfoot { break-inside: auto; }` in `chat.css` is load-bearing; do not remove it.** Chromium 157's
  built-in sheet styles `thead` and `tfoot` with `break-inside: if(media(overflow-block: paged): avoid;)`. Every
  header styled that way leaves a media-query result in a cache the engine never trims while results stay the same,
  and on a window resize it walks the whole cache once for every tree scope with a stylesheet. Each `<details>`
  has a built-in shadow tree with one, and a chat pane is mostly `<details>` (tool calls, thinking). A streaming
  reply's table is drawn again on every update, so both numbers are large in a deck that has been open for hours.
  Measured in a dump of the frozen renderer (2026-11-17): 27,808 cached entries, all that one condition, walked
  for each of 9,120 `<details>`, about 3.5 minutes a pass, at least two passes back to back after the deck was
  moved to another virtual desktop. Reproduced in headless Edge 157 with the real stylesheet: 600 `<details>` and
  16,000 table re-renders make one resize take 21 s; with no tables 6 ms, with no `<details>` 35 ms, with this
  rule 7 to 14 ms. The page's own rule wins the cascade, so the built-in value is never evaluated and no entry is
  made. What it does not cover: a table drawn outside `.chat` (give it the same rule), any other built-in or
  page rule that uses `if(media(...))`, and entries already made (gone when the page reloads, which a deck
  update does). The earlier "page answers nothing until the deck is restarted" reports fit this but were not
  dumped. Evidence and the scripts that read it without symbols: `C:\Temp\deck-freeze-20261117-200615`,
  `C:\Temp\refute-work`, `C:\Temp\chatlab\thead-details.mjs` on the machine it happened on.
- **Every message line carries when it was said, as `timestamp` (ISO 8601, UTC), and the pane reads only that.**
  Claude writes it itself, on live lines and in the transcript (`ChatTranscript.Line` copies it into history
  lines). Codex records `startedAtMs`/`completedAtMs` on every item and repeats them when turns are read back
  (`CodexChat.When`). ACP says no times at all, live or in `session/load` (checked against OpenCode 11-17 and
  Antigravity 11-20), so for any agent's message that has none the host writes its own clock on it as it is
  kept (`ChatLines.StampedNow`, in `AcpChat.Logged` and again in `HostProgram.Agent`), and never on a
  `history` line. An OpenCode chat's times are lost when its host restarts and replays the conversation; an
  Antigravity chat gets them back from the agent's own transcript on disk (`AgyTimes`, above). This needs
  no `ChatStream.Revision` bump; a line without a time just shows none.
  In the pane (`chat.js`) the model keeps a clock (`lastAt`) and puts a `time` item ahead of whatever is
  shown next after a pause (`pauseBefore`: the start, an hour, a new day). Times that go backwards do not
  move it (queued messages and parallel results arrive out of order in real transcripts). What is sent or
  streamed from here is timed by the viewer's clock until the session's own line arrives. `prepend` settles
  the mark at the seam. The words are a function of `(at, now)` and are worked out again on `focus()` and on
  one shared minute timer (`retime`), writing only what changed. The timer skips a pane that cannot be seen
  (`checkVisibility`): every pane's words change together, and a deck of hidden panes laid out again in one
  frame would stall the one being read. The line under the last message (`paintTail`) shows from a minute of
  quiet (`QUIET_MS`); the marks inside the conversation keep the hour (`PAUSE_MS`).
- **A phone ends a session through the gateway, and the deck finishes it.** `POST
  /sessions/<hostId>/end` makes the gateway write `phone-ended.json` (it is the only writer; the deck
  only reads, as with `phone-read.json`) and end the host. On each refresh the deck forgets that
  session (or a reboot would bring it back), closes its tab, ends the host again if it still runs,
  and removes the row once it has stopped (`PhoneEndStore.Plan`, `SessionsWindow.ApplyPhoneEnds`).
- **A record write is retried past a reader** (`RecordGate.Write`). The deck reads
  `hosts/<id>.json` whenever it scans, and a replace is refused while a reader has the file
  open; a write that still fails is forgotten as written, so the next change carries it. Before
  this a status change could be lost until the session's next one.
- **An unsent message is kept by conversation.** The pane stores the box under
  `sd.chat.draft:<session id>` in `localStorage` (the deck names the session in its `open`
  message, a revision 3 host in its snapshot), so a restart under a half-typed message gives it
  back.
- **Slash commands are just messages.** A local one (`/context`, `/effort`, `/rename`) is never
  echoed: the CLI answers with a synthetic assistant message and a `result`, so the pane settles
  its pending bubble at the end of the turn. `set_model` and friends replay a
  `<local-command-stdout>` user line, which is no turn (the host must not go busy on it) and
  shows as a note.
- **A resumed process says nothing about earlier turns.** The host seeds the ring from the
  transcript at start (`ChatTranscript.Before`, lines marked `history`), so a viewer has one source.
- **The CLI registers itself** in `~/.claude/sessions` (`kind: interactive`, `entrypoint:
  sdk-cli`), so the row gets name, model and context like any Claude session; the tab is renamed
  from that name.
- **Page side:** `ChatModel` in `chat.js` has no DOM and is tested under Node
  (`Tests/js/chat.test.js`, run by `ChatPageTests`). Markdown is `marked` with raw HTML escaped,
  then scrubbed to a tag whitelist; never render model text any other way. Code colouring
  (`highlight.js`, vendored) runs after the scrub, on a block's text only, and only for a named
  language in a finished message. The pane takes its ink
  from the terminal scheme's background, not the app theme (`chatLook` in `deck.html`).
- **A phone opens a chat as the same pane.** The gateway serves `/mobile/mobile-chat.html`, which
  hosts `chat.js` full screen (`touch: true`) and attaches through the gateway's relay
  (`socketUrl`), exactly as `mobile.html` does for a terminal; the app's `ChatScreen` is a web
  view around it. A chat pane change therefore reaches phones with the deck update, not an app
  build. `CatalogEntry.MobileReady` stays false for a chat (an older app must not open it as a
  terminal) and `ChatReady` says this gateway relays it. `AttachRelay` forwards `input`,
  `history`, `sessions` and `resume`, which is everything a chat sends.
- **Phone differences live behind `opts.touch`:** Return is a new line and the button sends, an
  attach button stands in for paste, and under 600 px the pickers use one-word labels because
  three full ones do not fit.
- **Testing the phone path without the live deck:** an isolated gateway (`SD_DATA_DIR`, its own
  port) lists hosts spawned under the same data dir; a debug app launch with `--pair host:port
  CODE` sets the built-in relay aside and reaches only that gateway (`--open`, `--send` work for
  chats too).
- **Try one without touching the live deck:** spawn `--host` with a chat `HostSpec` under its own
  `HostsDir`; `Tests/ChatHostTests.cs` does exactly that against a script standing in for the CLI.

## Status → display state (`SessionState.cs`)
`busy`→Working · `waiting`→Awaiting · `idle`→Completed (green ✓) · `shell`→Working · default→Idle.
**Error** is *not* a status — it's read from the transcript (`isApiErrorMessage`) and sorts to the top.
All status glyphs are intentionally static. Working is a filled blue play badge and Awaiting is an
amber ring. Do not add repeating WPF storyboards, opacity pulses, progress rings, or rotating transforms:
the tiny status animations were measured keeping DWM above one full core and making desktop dragging
choppy, with compositor load only partially recovering after the app exited.

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
- **Do not give Windows Terminal a bare `pwsh`.** An installed tray app can inherit Explorer's stale
  PATH even though PowerShell 7 is already installed, causing WT to show `0x80070002`. The launcher
  resolves the existing `Program Files\PowerShell\7\pwsh.exe` to an absolute path, falling back to
  built-in Windows PowerShell. This fixes launch without changing PATH or installing another shell.
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
  `%LOCALAPPDATA%\Programs\SessionDeck\`). A `\bin\` dev build never self-installs or
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
- **Test hooks:** `SD_INSTALL_DIR` (redirect install dir to a temp path), `SD_NO_INSTALL=1` (skip
  self-install), `SD_FAKE_UPDATE=<tag>` (force the title-bar button), `SD_DATA_DIR` (redirect the
  `active-sessions.json` dir — the unit tests set it), `SD_NO_USAGE_API=1` (force the usage fetch to
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
  `C:\Data\Repos\.worktrees\SessionDeck\<branch>`, build + verify there, fast-forward merge to
  `main`, push, then remove the worktree.
- **Releases are manual** — Actions → Release → Run workflow (never on push). Ships an unsigned
  self-contained `.exe`.
