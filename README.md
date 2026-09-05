# Orbit

A small, native Windows companion for watching your agents work. A neutral translucent surface, compact spacing, an expandable agent tree, and a small status pill. Color is reserved for status labels: blue for working, amber for waiting or attention, rose for errors, and muted neutral text for completed work.

Built with C# and Windows Presentation Foundation. No browser, webview, Electron, Node runtime, external packages, account, or network connection.

## Open

Double-click `bin/Orbit.exe`. It opens in the bottom-right corner and follows local Codex sessions for this project. Right-click the tray icon for show, hide, live activity, preview, and quit.

```powershell
.\build.ps1 -Run        # Build and open live activity
.\build.ps1 -Run -Demo  # Build and open the animated preview
.\build.ps1 -Test       # Build and run the integration and native layout checks
```

The Windows .NET Framework 4.x desktop runtime/compiler is used directly; a separate .NET SDK is not required. The portable executable embeds its interface and can run independently. When moved outside `bin`, pass `--workspace` to select the intended project.

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

By default, Orbit reads the local `CODEX_HOME/sessions` directory (or the current user's `.codex/sessions`) and follows the most recently active root session in the selected workspace. It tracks the coordinator and its subagents, excluding internal guardian tasks. It reads files only; it does not start agents, modify Codex, submit approvals, or use the network.

```powershell
.\bin\Orbit.exe --workspace 'D:\projects\my-project'
.\bin\Orbit.exe --workspace 'D:\projects\my-project' --session '<root-thread-id>'
.\bin\Orbit.exe --demo
```

Discovery checks recent local session folders plus a flat session directory. New files are discovered every five seconds and events are tailed about every 1.5 seconds on a background thread. A `--session` filter pins a tree and also searches up to 400 older date directories for that exact session, then discovers its same-day team. `--sessions-dir` selects a different local session directory. A quiet agent becomes idle after five minutes without recorded activity; silence is never reported as completion.

Agent names prefer an explicitly recorded task name, then the final segment of the assigned agent path (such as `session_tracking`), then a Codex nickname, then an ID fallback. Orbit does not invent roles or names for live agents. Preview names are sample data. Parent relationships are resolved through the full ancestry, including grandchildren; malformed cycles and missing parents do not silently hide branches. Input requests stay visible until the matching request is resolved.

**Compatibility:** the local Codex rollout format is an implementation detail, not a stable public event API. The adapter is based on locally observed `session_meta`, `event_msg`, and `response_item` records and may need updating after a Codex upgrade. It cannot see cloud-only agents or sessions that do not write local rollouts. Status changes are observed from records, not from process inspection. Reasoning, user prompts, tool arguments, and tool outputs are not displayed. Public assistant progress messages may appear in the local feed.

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

## Project

- `src/Widget.xaml`: native templates, colors, layout, and controls.
- `src/Program.cs`: tray integration, native window behavior, animation, and UI updates.
- `src/Activity.cs`: incremental local activity adapters and preview sequence.
- `src/AgentTree.cs`: stable tree nodes, parent/child grouping, and nested activity.
- `src/ActivityReaderChecks.cs`: naming, ancestry, correlation, and historical-session regressions.
- `src/SelfTests.cs`: event stream, scoping, lifecycle, and XAML rendering checks.
- `scripts/publish-event.ps1`: local event publishing helper.

Relevant platform references: [WPF window appearance](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/windows/), [Codex App Server](https://learn.chatgpt.com/docs/app-server), and [Codex subagents](https://learn.chatgpt.com/docs/agent-configuration/subagents). The local rollout reader is separate from the documented App Server protocol.
