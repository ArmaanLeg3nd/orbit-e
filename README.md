# Orbit

A small, native Windows companion for watching your agents work. A neutral translucent surface, compact spacing, an expandable agent tree, and a small status pill. Color is reserved for status labels: blue for working, amber for waiting or attention, rose for errors, and muted neutral text for completed work.

Built with C# and Windows Presentation Foundation. No browser, webview, Electron, Node runtime, external packages, account, or network connection.

## Open

Double-click `bin/Orbit.exe` after building. It opens in the saved corner (bottom-right on first launch) and follows local Codex sessions for this project. Right-click the tray icon for show, hide, live activity, preview, and quit. Launch is manual: Orbit does not currently start with Windows or launch automatically when a Codex session starts.

```powershell
.\build.ps1 -Run        # Build and open live activity
.\build.ps1 -Run -Demo  # Build and open the animated preview
.\build.ps1 -Test       # Build and run the integration and native layout checks
```

The Windows .NET Framework 4.x desktop runtime/compiler is used directly; a separate .NET SDK is not required. The portable executable embeds its interface and can run independently. When moved outside `bin`, pass `--workspace` to select the intended project. Build outputs are ignored by Git; a fresh clone does not include `bin/Orbit.exe`.

## Interactions

- Drag the header or collapsed pill to move it. Near an edge, it snaps to a corner.
- Click the minus button or press Escape to collapse. Click the pill to expand.
- Expand the session tree to see agents nested under the parent that spawned them.
- Expand an agent once to see its subagents and five most recent timestamped events directly. Open branches and selection survive live updates; the detail view retains the longer activity history.
- Click an agent's arrow to inspect its full task, parent, elapsed time, and activity.
- Settings control always-on-top, motion, attention expansion, and corner position.
- Attention requests expand a collapsed widget without taking keyboard focus. This is disabled in preview mode.
- Ctrl+Q quits. Opening the executable a second time reveals the existing widget.

## Live activity

By default, Orbit reads the local `CODEX_HOME/sessions` directory (or the current user's `.codex/sessions`) and shows every active root session in the selected workspace, focusing the newest. It tracks each coordinator and its subagents, excluding internal guardian tasks. It reads files only; it does not start agents, modify Codex, submit approvals, or use the network.

```powershell
.\bin\Orbit.exe --workspace 'D:\projects\my-project'
.\bin\Orbit.exe --workspace 'D:\projects\my-project' --session '<root-thread-id>'
.\bin\Orbit.exe --demo
```

Discovery checks recent local session folders plus a flat session directory. New files are discovered every five seconds once a team is showing (faster while waiting for the first team) and events are tailed about every 1.5 seconds on a background thread. When a new session appears it takes focus; the footer counts sessions (`2 sessions · 1 working`). A `--session` filter pins a single tree and also searches up to 400 older date directories for that exact session, then discovers its same-day team. `--sessions-dir` selects a different local session directory. Sessions launched in a workspace subdirectory join the feed; a sibling path that merely shares a prefix does not. A quiet agent becomes idle after five minutes without recorded activity; silence is never reported as completion.

Agent names prefer an explicitly recorded task name, then the final segment of the assigned agent path (such as `session_tracking`), then a Codex nickname, then an ID fallback. Orbit does not invent roles or names for live agents. Preview names are sample data. Parent relationships are resolved through the full ancestry, including grandchildren; malformed cycles and missing parents do not silently hide branches. Input requests stay visible until the matching request is resolved.

The coordinator and subagents use the same activity parser. Public progress text comes from assistant commentary, legacy `agent_message` events, and completed `AgentMessage` items. Command and file-change metadata provide compact labels for checks, builds, inspections, and edits. Consecutive identical actions refresh their timestamp instead of adding another row; mirrored public messages with the same ID are deduplicated. A finished command does not finish the agent's turn, and a failed command can be followed by recovery work.

Reasoning, user prompts, raw command/tool arguments, diffs, and tool output are not displayed. Command text is classified locally into generic labels; public assistant messages are displayed as recorded and may themselves mention project details. Live mode is read-only, not a process attachment or agent-control connection.

Preview mode identifies **simulated activity** in the footer and the collapsed pill. Live mode identifies its local source in the footer. The main header contains only the workspace and team status.

## Other agent systems

Point the widget at a local JSONL event stream:

```powershell
.\bin\Orbit.exe --events 'D:\projects\subagent-view\.orbit\activity.jsonl'
.\scripts\publish-event.ps1 -AgentId 'builder-1' -State started -Name Builder -Task 'Implementing the interface'
.\scripts\publish-event.ps1 -AgentId 'reviewer-1' -State started -Name Reviewer -Task 'Reviewing changes' -ParentId 'builder-1' -Role reviewer
.\scripts\publish-event.ps1 -AgentId 'builder-1' -State completed -Task 'Interface complete'
```

Each newline-terminated JSON record has `type`, `agent_id`, an ISO 8601 `timestamp`, and optional `name`, `task`, `message`, `parent_id`, and `role`. Supported types: `agent.started`, `agent.updated`, `agent.waiting`, `agent.attention`, `agent.completed`, `agent.error`. Unknown records are ignored. Partial lines and split UTF-8 writes are buffered. Out-of-order events do not overwrite newer state.

```json
{"type":"agent.started","agent_id":"builder-1","name":"Builder","task":"Implementing the interface","timestamp":"2026-09-05T10:00:00Z"}
```

Close the existing instance before changing command-line source options. Switching Live/Preview in the widget is immediate. Preferences stay in the selected workspace's `.orbit/preferences.json`; no startup tasks, registry entries, or global shortcuts are installed.

## Current limitations

- **Best-effort local adapter.** Codex rollouts are an internal format, not a stable public API. The reader recognizes locally observed `session_meta`, `event_msg`, and `response_item` shapes and may need changes after a Codex upgrade. Unknown activity shapes are ignored; action classification is heuristic, not a complete command parser.
- **Local sessions only.** Cloud-only sessions and agents that do not write accessible local rollouts are invisible. The recorded workspace must match the selected directory or a subdirectory of it; agents in another worktree/directory can be omitted. Use `--sessions-dir` when logs belong to a different profile or location. Other agent systems require the JSONL adapter above.
- **No session picker or control plane.** Automatic mode lists every active session and focuses the newest; use `--session` to pin one. Source arguments are read at startup, and only one widget instance runs per Windows desktop session. Orbit cannot start/stop agents, answer input requests, or approve actions.
- **Observed, delayed status.** Polling and buffered log writes introduce delay. Large logs can take multiple polls to catch up at startup, shown as `Catching up local Codex…`. The footer shows `Synced with local Codex`, `Synced … Xs ago`, or `last write HH:mm` so staleness is visible. “Synced” means local records were found, not that the Codex process is alive. Five-minute idle detection is a silence heuristic, not proof that work stopped; completion requires an explicit lifecycle or final-answer record.
- **Bounded discovery and history.** Automatic discovery covers recent date folders (today, the previous three days, and tomorrow for date-boundary tolerance) and a flat directory, considering up to 160 recent files per scan. Historical pins search at most 400 date directories plus the pinned session's same-day files; older cross-day descendants can be missed. The UI shows at most 60 agents, keeps up to 40 events per agent in memory, and shows five inline. Initial reads start within the last 2 MiB of large logs, so earlier context may be absent. This is a live companion, not a complete audit archive.

## Platform limitations

- **Windows desktop only.** This is WPF on .NET Framework, targeting Windows 10/11. There is no browser, macOS, Linux, mobile, or headless UI build. The current build script expects the 64-bit Windows Framework compiler path; Windows ARM64 and 32-bit installations have not been validated.
- **Translucency, not backdrop blur.** The glass appearance uses a transparent window, gradients, borders, and shadow. It does not use Windows Acrylic/Mica or blur whatever is behind it; contrast varies with the desktop background.
- **Portable development build.** There is no installer, code signing, auto-update service, or login auto-start. Keep the project writable for building and saving preferences. Attention expansion works while the widget is running; it does not launch Orbit or reveal a window hidden in the tray.
- **Desktop behavior still needs broader testing.** Automated checks exercise the native controls and render WPF layouts, but do not establish compatibility with every multi-monitor, mixed-DPI, Remote Desktop, accessibility, or Windows theme configuration.

## Project

- `src/Widget.xaml`: native templates, colors, layout, and controls.
- `src/Program.cs`: tray integration, native window behavior, animation, and UI updates.
- `src/Activity.cs`: incremental local activity adapters and preview sequence.
- `src/AgentTree.cs`: stable tree nodes, parent/child grouping, and nested activity.
- `src/ActivityReaderChecks.cs`: public activity, action summaries, naming, ancestry, correlation, and historical-session regressions.
- `src/SelfTests.cs`: event stream, scoping, lifecycle, and XAML rendering checks.
- `scripts/publish-event.ps1`: local event publishing helper.

Relevant platform references: [WPF window appearance](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/windows/), [Codex App Server](https://learn.chatgpt.com/docs/app-server), and [Codex subagents](https://learn.chatgpt.com/docs/agent-configuration/subagents). The local rollout reader is separate from the documented App Server protocol.
