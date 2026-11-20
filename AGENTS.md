# Working on Orbit E(volved)

Orbit E(volved) is a Windows-native, local-first Codex workflow viewer. Keep the compact widget suitable for a quick glance. Put deeper inspection in the workflow window or the agent details view. [Original project by ctx-o1](https://github.com/ctx-o1/subagent-view)

## Build and verify

- Run `.\build.ps1 -Test` from the repository root for reader and native WPF checks.
- Run `.\build.ps1 -Run -Demo` to check layout and interactions with simulated data.
- For graph changes, check a nested branch, a graph larger than the viewport, and an update while an agent is selected. Check the compact widget and graph at narrow widths when changing shared activity rendering.
- Use synthetic rollout fixtures and screenshots in commits. Keep local builds, test artifacts, and personal tool configuration out of commits.

## Source map

| Area | Files |
| --- | --- |
| Session catalog, local and archived discovery, project grouping, duplicate setup records | `src/ChatCatalog.cs` |
| Per-user Windows startup registration | `src/StartupRegistration.cs` |
| Incremental rollout parsing, agent state, public activity | `src/Activity.cs` |
| Compact widget, tray, routing between views | `src/Program.cs`, `src/Widget.xaml`, `src/AgentTree.cs` |
| Graph geometry, navigation, agent inspector | `src/AgentGraphLayout.cs`, `src/AgentGraphWindow.cs` |
| Activity formatting and local file references | `src/ActivityMarkdown.cs`, `src/ActivityLinks.cs` |
| Reader and native UI checks | `src/ActivityReaderChecks.cs`, `src/SelfTests.cs` |

## Project conventions

- Use native WPF and the existing build script. Compare a proposed dependency against the built-in Windows and .NET capabilities before adding it.
- Treat Codex rollouts as read-only input. Handle missing, partial, older, and unfamiliar records conservatively.
- Draw delegation edges from recorded parent relationships. Keep partial data visible and distinguish unavailable links from recorded ones.
- Surface public progress, results, and meaningful action summaries. Keep raw commands, private reasoning, and generic tool chatter out of the activity UI.
- Preserve selection, expansion, and graph position during live refreshes. Honor the motion setting and keep the UI usable by keyboard.
- Keep upstream credit and historical notes when updating the project’s public documentation.
