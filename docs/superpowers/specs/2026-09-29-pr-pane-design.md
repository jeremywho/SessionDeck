# Pull requests pane — design

Date: 2026-09-29 · Branch: `pr-pane` · Status: approved in conversation, spec awaiting review

## Goal

One place, always on screen, that shows every open pull request the `gh`-authenticated user
authored: whether it is a draft, how old it is, whether it has conflicts, its CI state, what it
needs next, and which live local Claude Code / Codex sessions are working on it.

## Decisions

- **It is a deck column, not a separate tool.** A prior standalone board (a CLI plus a local web
  page) went unused because it was out of sight: it had to be remembered and opened. A column in
  the deck is visible without doing anything.
- **Scope:** open PRs authored by the `gh` user, across every owner. PRs requesting the user's
  review are out of scope for this version.
- **Read-only on GitHub.** The pane never merges, marks ready, re-runs checks, or comments. The
  only actions are opening a PR in the browser and focusing a session.
- **No size column** (additions/deletions).

## The column

- A PR column is a deck column of its own kind. It holds no tabs. It is added from the per-column
  `+` menu ("Pull requests column"), which inserts it to the right of that column taking half that
  column's width, exactly as a spacer is added. There is at most one; the menu item is hidden when
  it exists.
- **Added once automatically.** On the first launch of a build with this feature, if the saved
  layout has no PR column, one is inserted as the rightmost column taking half the width of the
  rightmost session column, and `Settings.PrPaneIntroduced` is set. Removing the column afterwards
  sticks: it is never re-added automatically.
- Its strip shows `Pull requests`, the open count, `updated <age>`, and a refresh button. The strip
  is dragged to move the column, and its context menu offers Move left, Move right and Remove, the
  same operations and wiring as a spacer strip.
- A tab dropped onto the PR column does not enter it: a drop on its left or right half makes a new
  column on that side; a drop on its middle is treated as a drop on its nearer half.
- Width, position and existence are user-arranged state and are saved with the deck layout (see
  *Persistence*).

## What the column shows

### Grouping and order

- Two sections, **Live** and **Draft**, each headed with its count.
- **Stacks:** a PR whose base branch is another open PR's head branch in the same repository is
  that PR's child. Children render directly under their parent, indented, at any depth. A stack is
  placed in the section of its root; a child whose draft state differs from the root's carries a
  `draft` / `live` marker.
- **Series:** two or more PRs in the same repository with identical titles that are not part of a
  stack collapse into one row showing `×N`. The row's CI is the worst member's, its next action is
  the most urgent member's, and its agents are the union. The row expands to show its members.
  Which series are expanded is saved (`Settings.PrExpandedSeries`, keyed `owner/repo|title`,
  pruned when the series no longer exists).
- **Order within a section** is by attention band, then repository, then PR number ascending. It
  never uses a time-based key, so rows do not jump between refreshes. A stack sorts by its most
  urgent member.

| Band | Next actions in it |
|---|---|
| 1 Needs you | Conflicts, CI failing, Changes requested, N open threads, Behind base |
| 2 In progress | CI running |
| 3 Waiting | Waiting on #parent, Needs review, Merge state unknown |
| 4 Done on your side | Draft, all green; Ready to merge |

### Row cells

| Cell | Content |
|---|---|
| CI | Dot: green (`SUCCESS`), yellow (`PENDING` / `EXPECTED`), red (`FAILURE` / `ERROR`), grey (no checks). Tooltip lists failing checks by name and pending checks with time since they started. |
| PR | `#N` and title, with the repository name muted. Click opens the PR in the browser. |
| Next action | One chip: the first match in the list below. Its tooltip lists every match. |
| Agents | One pill per attributed live session (see *Agents*). |
| Age | `open 12d · last commit 3d`. |
| Local | `uncommitted` and/or `N unpushed` when the PR's local worktree has them; tooltip names the worktree path. Empty when there is no local worktree. |

### Next action (first match wins)

1. **Conflicts** — `mergeable == CONFLICTING`.
2. **CI failing: `<first failing check>`** — rollup `FAILURE` or `ERROR`.
3. **Changes requested** — `reviewDecision == CHANGES_REQUESTED`.
4. **N open threads** — unresolved review threads > 0 (bot reviewers included).
5. **Behind base** — `mergeStateStatus == BEHIND`.
6. **Waiting on #parent** — the PR is a stack child.
7. **CI running** — rollup `PENDING` or `EXPECTED`.
8. **Merge state unknown** — `mergeable` still `UNKNOWN` after the retry (see *GitHub*).
9. Drafts: **Draft, all green**. Live PRs: **Needs review** when `reviewDecision == REVIEW_REQUIRED`,
   otherwise **Ready to merge**.

## Agents

A pill means: this live session has recently worked on this PR.

**Which sessions.** Every live session the existing scan produces, both the deck's own and
sessions in other terminals, Claude and Codex. Headless Codex threads that
`CodexAttribution.Fold` rolls onto a Claude session contribute their evidence to that Claude
session; `Fold` is extended to keep the folded threads' rollout paths on the owner
(`SessionInfo.FoldedTranscripts`).

**Evidence.** From each session's transcript, the inputs of its last 100 tool calls:

- Claude: `assistant` records' `tool_use` blocks, input serialized to text. Plus the same from each
  subagent transcript under `projects/<slug>/<sessionId>/subagents/agent-*.jsonl` written in the
  last 2 hours; their evidence belongs to the parent session.
- Codex: `response_item` payloads of type `custom_tool_call` (`input`) and `function_call`
  (`arguments`). A `codex exec` thread whose `session_meta.cwd` is inside a PR's worktree counts as
  meeting the threshold for that PR on its own.

The input text is JSON-decoded first, then any run of `\` or `/` is collapsed to `/` and it is
compared case-insensitively, so `C:\\Repos\\…`, `C://Repos//…` and `D:/Repos/…` all match. A hit is:

- a path inside a local worktree whose checked-out branch is an open PR's head branch (see
  *Worktrees*; junctioned paths such as `C:\Repos` → `D:\Repos` resolve to the same worktree);
- `github.com/<owner>/<repo>/pull/<n>` for an open PR;
- `gh pr <verb> <n>` with `--repo` / `-R <owner>/<repo>` naming an open PR. Without a repo it counts
  only when exactly one open PR has that number.

**Threshold.** A session is attributed to a PR when it has at least 3 hits for it within that
window. Pills are ordered by the time of their most recent hit, newest first.

**Pill.** Provider mark, session display name, and state (Working / Awaiting / Idle, from the same
state mapping the session list uses). A `via codex` tag marks attribution that came only from
folded Codex threads. The tooltip states the evidence, e.g. `41 tool calls in …/<worktree>`.

**Click.** A deck session activates its tab (the list's existing "Show tab" path). A session in
another terminal is focused with `WindowActivator.Activate`.

**Cost.** Transcript reads are cached by `(path, mtime, size)`. The tail read grows until it holds
100 tool calls or reaches 4 MB. The pass runs every 15 s on a background thread, single-flight,
never on the dispatcher, and writes a `pr-attribution <ms>` line to the performance log when it
takes 1 s or more.

## Worktrees

- **Clones:** directories directly under each root in `Settings.PrRepoRoots` (default
  `C:\Repos`, `C:\Data\Repos`; edited in `settings.json`, no UI) that contain a `.git` directory. The owner/repo comes from the
  `origin` URL in `.git/config`. Only clones of repositories that have open PRs are used.
- **Worktrees:** `git -C <clone> worktree list --porcelain` gives each worktree's path and branch.
  Paths are canonicalized with `GetFinalPathNameByHandle`, which resolves junctions, and deduplicated
  on the canonical path, so a second clone of the same repository that reports the same worktrees
  adds nothing.
- **Local state,** for worktrees whose branch is an open PR's head branch only:
  - `git -C <wt> status --porcelain` non-empty → `uncommitted`;
  - `git -C <wt> rev-list --count <headRefOid>..HEAD` > 0 → `N unpushed`. When the PR's head
    commit is not present locally (`git cat-file -e` fails), unpushed is reported as unknown and
    shown as nothing.
- **Cadence:** every 2 minutes, and immediately for a PR whose head commit changed. Git calls run
  one at a time on a background thread with a 10 s timeout each; a pass of 2 s or more writes a
  `pr-worktrees <ms>` performance line.

## GitHub

- One `gh api graphql` call:
  `search(query: "is:pr is:open author:@me archived:false", type: ISSUE, first: 100)`, paging on
  `pageInfo` past 100. Per PR: `id number title url isDraft createdAt repository{nameWithOwner}
  baseRefName headRefName headRefOid mergeable mergeStateStatus reviewDecision
  reviewThreads(first:100){nodes{isResolved}}` and
  `commits(last:1){nodes{commit{committedDate statusCheckRollup{state contexts(first:100){nodes{
  __typename ... on CheckRun{name status conclusion startedAt detailsUrl}
  ... on StatusContext{context state createdAt targetUrl}}}}}}}`.
- **Cadence:** every 60 s while the window is visible, every 5 minutes while hidden in the tray,
  immediately when the window is shown or refresh is clicked. All callers go through one
  `SingleFlight` with a 15 s minimum gap between calls.
- **Mergeability is lazy.** GitHub returns `mergeable: UNKNOWN` until it has computed it. PRs that
  come back `UNKNOWN` are re-read once, about 3 s later, with `nodes(ids: [...])`.
- **`gh` invocation** reuses the pattern in `Updater.Gh` (both pipes drained concurrently, no
  window), extracted into a shared `Gh` helper.

## Failure behaviour

- **`gh` not found:** the column body says so, with the install link.
- **Not authenticated:** the column body shows `gh auth login` to run.
- **Call fails** (network, rate limit, non-zero exit): the last good board stays. The strip's
  `updated <age>` turns amber after 150 s visible (two and a half missed polls; the same rule the
  usage bar uses) and the tooltip carries the error.
- **Partial GraphQL errors** (for example an organization that requires SSO authorization): the PRs
  that came back are shown, plus one warning line naming the organization.
- A git or transcript read that fails leaves that one cell empty. It never fails the board.
- **No open PRs:** the column body says `No open pull requests`.

## Rendering

- The page gains `wwwroot/prs.js` and `wwwroot/prs.css`, embedded like the other page assets and
  loaded by `deck.html`. The `layout` message carries `kind: "prs"` for the PR column; the page
  positions a `.pane.prs` element there with `place()`, where a spacer would get a filler.
- The app posts `{type: "prs", board}`: the complete derived board. It is posted only when the
  board's value signature changes, so an unchanged refresh does not re-render (and does not close
  an open tooltip). Updated-age text ticks in the page itself.
- Page → app messages: `open` (existing, for PR and check links), `focusSession {sessionId}`,
  `prRefresh`, `prToggleSeries {key}`.
- Colours follow the deck's dark/light theme and look. No repeating animations of any kind: the
  project measured small status animations keeping the compositor above one core.

## Components

| Unit | Responsibility | Depends on |
|---|---|---|
| `Gh` | Run `gh`, return stdout / exit code / stderr. | process |
| `PrSource` | Build the GraphQL query, call `Gh`, parse into `PullRequest` records, retry `UNKNOWN`. Parsing is a pure function. | `Gh` |
| `WorktreeIndex` | Find clones, list worktrees, canonicalize, dedupe, read local state. Porcelain parsing is a pure function. | git, `Native` |
| `PrAttribution` | Transcript tails → hits → `(sessionId, PR, count, lastHit)`. Extraction is a pure function over lines. | `SessionInfo`, `WorktreeIndex` |
| `PrBoard` | Pure derivation: sections, stacks, series, next action, bands, order, cells. | the three above, as data |
| `PrPaneController` | Timers, background passes, posting the board, handling page messages. | all of the above, `DeckBrowser` |
| `DeckModel` / `DeckGroup` / `DeckColumn` | The PR column's lifecycle and persistence. | — |

## Persistence

- `DeckGroup` gains a column kind: tabs, spacer, or pull requests. `DeckModel` gets `AddPrPane`
  and `RemovePrPane`. `MoveGroup`, `MoveGroupTo` and the divider fractions already work on any
  column. `LandingGroup` and `MoveTo` never put a tab into the PR column.
- `DeckColumn` saves the PR column as `Spacer: true` plus `Panel: "prs"`. An older build loading
  the file keeps a spacer in that position instead of dropping the column. `Restore` rebuilds a
  PR column from `Panel`.
- New settings: `PrPaneIntroduced` (bool), `PrRepoRoots` (list), `PrExpandedSeries` (list).
- **The PR column's existence, position and width must survive every transition:**
  - app restart and reattach;
  - restarting one session by hand;
  - auto-restart on CLI update, including many sessions at once;
  - reboot auto-resume under new host ids;
  - adopt;
  - `/resume` or `/clear` inside a session;
  - the exe update swap;
  - closing and reopening tabs;
  - adding, moving and removing spacers;
  - a session column beside it emptying and being removed;
  - corrupt-settings recovery.

  Expanded series survive app restart and the exe update swap.

## Testing

Unit tests (xUnit, `Tests/`):

- **`PrSource` parsing** against a fixture shaped from a real response but **scrubbed**: this is a
  public repository, so fixtures carry no real organization, repository, title, branch or user
  names.
- **`PrBoard`:** each next action and its precedence; bands and stable order; stacks at depth 3
  with mixed draft state; series collapse and the aggregate CI and next action; a stack child never
  shown as ready to merge; unknown mergeability.
- **`PrAttribution`:** Claude and Codex tool-call records; each path spelling (`\\`, `//`,
  drive-letter change, case); `pull/N`; `gh pr` with and without a repo, including the bare-number
  ambiguity rule; the threshold at 2 and 3 hits; the 100-call window; subagent roll-up and the
  2-hour cutoff; folded Codex threads and `via codex`; `exec` `cwd`.
- **`WorktreeIndex`:** porcelain parsing (detached HEAD, bare, prunable entries); junction dedupe.
- **`DeckModel`:** PR column add, move and remove; at most one; tab drops never enter it;
  snapshot → restore round trip; an old build's view of the saved column (a spacer); the
  model-level transitions in the list above.

Live verification, on an isolated instance (`SD_DATA_DIR` set to a temp directory,
`SD_NO_INSTALL=1`) so the user's settings are untouched:

- The PR column appears on first launch; a screenshot shows it.
- Its Live/Draft counts and PR numbers match `gh search prs --author=@me --state=open` run at the
  same time.
- A session known to be working in a PR's worktree shows as that PR's pill, and clicking it
  activates that session's tab.
- With `gh` removed from `PATH`, the column shows the not-found message.
- The app-level transitions from *Persistence* are rehearsed and the PR column's position and width
  read back unchanged from `settings.json` after each.

## Documentation

`CLAUDE.md` currently states that the usage bar is the one exception to "everything is local disk
reads". It gains a *Pull requests pane* section: the `gh` network dependency, the cadences, the
attribution rules and their limits. `README.md` gains the feature.

## Out of scope

- PRs requesting the user's review.
- Any GitHub mutation (merge, mark ready, re-run checks, comment).
- Grouping PRs across repositories into work items; merge order beyond stack nesting.
- Notifications.
- A size column.

## Known limits

- **Attribution is a heuristic.** A session reviewing many PRs can reach the threshold for several
  of them. The window, the threshold and the evidence tooltip keep this visible rather than hidden.
- **GitHub search lags** a newly opened PR by up to about a minute.
- **The transcript and rollout formats are undocumented.** Their parsing lives only in
  `PrAttribution`, next to the existing scanners, so a format change is a one-file fix.
