# Changelog

## Unreleased (`fresh-codex-state`)

Session handling, feed honesty, and a bot-card UI pass. Self-tests grew
from 104 to 113 checks; all green (`.\build.ps1 -Test`).

### Session feed: newest takes focus, nothing hides silently

- The view follows the newest active session automatically. When a new
  Codex run appears, it takes focus within one discovery cycle instead of
  leaving a stale team on screen.
- Every workspace-matching team stays tracked. The footer counts what is
  live (`2 sessions · 1 working`, `1 needs you`) instead of showing one
  team as if it were the only one.
- `--session` still pins a single tree (including older rollouts via the
  400-directory historical search). Pinned mode is unaffected by
  auto-follow.
- `active` is defined from explicit reader state only: any member
  `working`, `waiting`, `attention`, or `error`. File-write time orders
  teams but never marks one active; silence still demotes `working` to
  `idle` after five minutes and never reports completion.
- Sessions launched in a workspace subdirectory join the feed; a sibling
  path that merely shares a prefix (`proj` vs `proj-unrelated`) does not.
- An earlier sticky-team experiment (stay on first team, announce newer
  in footer) and a pill-strip session switcher were both tried and
  removed — the strip was bad UX. Auto-follow plus footer counts won.
- New regressions: first-team focus, newest-takes-focus with older team
  retained, session-summary ordering, subdirectory vs prefix-sibling
  scoping.

### Freshness you can read

- Footer states: `Synced with local Codex`, `Synced … Xs ago`,
  `last write HH:mm`, `Catching up local Codex…` while a 1 MiB poll
  budget backlog drains, and `N sessions · W working` for multi-session.
- `Snapshot` carries `Sessions[]` (per-team agent/working counts,
  attention flag, last-write time, focus flag), `FocusedSession`,
  `TotalSessions`, `LastWriteUtc`, and `CatchingUp`.
- Discovery runs every 1.5 s while waiting for the first team, 5 s once
  settled; event tailing stays at ~1.5 s per team on a background thread.
- Agent tooltips show last-update time, so `Synced` can never be mistaken
  for process liveness.

### Bot cards instead of a file tree

- Agent rows are compact tree rows: 26 px avatar circle with neutral
  initial, status ring, name, task, right-aligned status pill and elapsed
  time, and the latest five activity lines inline beneath, nested under
  classic tree chrome (expander arrow column plus guide line). An
  earlier card-container experiment was removed.
- The whole row header is clickable (hover feedback, keyboard
  accessible) and opens the detail view; the ↗ arrow is gone. Expansion
  belongs to the tree arrow; the row keeps a static `N replies` label.
- Status column (pill, elapsed) is right-aligned, so live rows with long
  raw task text line up exactly like preview rows.
- Active subagents (`working`, `waiting`, `attention`, `error`)
  auto-expand on first sight so live activity is visible without
  clicking; collapsing is still preserved across polls. Idle and done
  branches stay collapsed.
- Subagent nesting kept as reply indent (24 px); the synthetic `Session`
  divider node no longer inherits that indent, fixing the preview/live
  left-gap mismatch.
- `TreeView` virtualization (recycling) is now explicit; node identity
  still preserves expansion and event rows across live updates.

### Scrollbar that stays slim

- Root cause: `Width=3` without `MinWidth` fell back to the ~17 px
  system minimum. Now a keyed `SlimBar` style (8 px hit area, 3 px
  centered thumb, 16 px minimum thumb height, transparent page buttons)
  with an overlay `ScrollViewer` template.
- Content reserves an 8 px gutter, so the overlay thumb never covers
  text. Detail and settings scrollers inherit the same template.

### Color discipline: status-only

- One color language everywhere: blue working, amber waiting/attention,
  rose error, muted gray idle/done. Ring, dot, and pill always match.
- Two rainbow experiments (per-bot palette on initials, session-pill
  tinting) were tried and fully reverted — too many colors, too many
  places, nothing matched. The palette code was deleted, not just
  unbound.
- The only motion is the working dot's subtle pulse; idle and done
  stay still and gray.

### Engineering notes

- `Agent` gained `SessionRoot` (team identity through ancestry
  resolution) and a neutral `AvatarInitial`; `AgentTree` gained
  `ReplyCount`/`HasReplies`/`ReplyLabel` and activity-aware seeding.
- Feed caps are per focused team (60 agents shown, oldest completed
  hidden first with an explicit overflow note); eviction protects active
  and focused teams.
- New docs: `docs/future.md` — Avalonia-first cross-platform plan that
  keeps the glass/bot design uncompromised, plus a Grok-companion UI
  scout (steal card roster, presence mode, semantic motion; avoid
  persona coupling).
