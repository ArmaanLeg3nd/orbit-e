# Orbit E

Orbit E is a native Windows companion for seeing how Codex agents work together across your chats. It combines a compact desktop widget, live graphs for local workflows, and an on-demand list of Codex Cloud tasks.

Orbit E builds on ctx-o1’s Orbit, which supplied the Windows widget, tray controls, and local session reader. This fork adds project-wide chat browsing, formatted activity and file links, and a larger graph for following nested agent work. [Upstream Orbit](https://github.com/ctx-o1/subagent-view)

![Orbit E workflow window showing a selected branch, its delegates, and the agent details panel](docs/workflow-graph.png)

## Quick start

Use Windows 10 or 11 with the built-in 64-bit .NET Framework 4.x runtime and Windows PowerShell. The build uses Windows’ C# compiler and requires no package restore.

~~~powershell
git clone https://github.com/ArmaanLeg3nd/orbit-e.git
cd orbit-e
.\build.ps1 -Run
~~~

The build writes `bin/Orbit.exe`. For a preview with simulated agents, run `.\build.ps1 -Run -Demo`. To run the parser and Windows UI checks, use `.\build.ps1 -Test`.

## Using Orbit E

- **Browse your chats.** Open a project group and choose a chat. Orbit E includes local archives, groups automatically dated workspaces under **Projectless chats**, and hides setup-only duplicates when a matching chat contains activity.
- **Find cloud tasks.** Click **Cloud** in the project list to load or refresh recent Codex Cloud tasks using the Codex CLI’s existing sign-in. Computer icons mark local chats and cloud icons mark cloud tasks. Open a cloud entry to see status and change counts when provided, or continue in Codex in your browser.
- **Keep an eye on a run.** Use **Collapse** or press **Esc** in the chat list to shrink the widget to a pill. It shows active work and opens the relevant chat when you click it. Right-click the tray icon to show, hide, switch to preview, or quit.
- **Follow a delegation.** Select **Graph** in a chat to open its workflow. Click a node to inspect it, or use the parent and delegate links to jump between agents. Drag the canvas to pan, use **Ctrl + scroll** to zoom, and use **Fit** to see the whole graph. Click the zoom percentage to return to 100%.
- **Read the results.** The details view formats public progress and final reports, keeps local file references clickable, and puts routine reads behind **Show file reads**.

## Session scope

The default view covers locally recorded Codex chats. Orbit E reads `sessions` and `archived_sessions` under `CODEX_HOME`, which defaults to `%USERPROFILE%\.codex`. It uses local Codex project and title metadata when available. Local monitoring is read-only and keeps the rollout data on your machine.

~~~powershell
.\bin\Orbit.exe --workspace 'D:\projects\my-project'
.\bin\Orbit.exe --workspace 'D:\projects\my-project' --session '<thread-id>'
.\bin\Orbit.exe --events 'D:\logs\agent-events.jsonl'
.\bin\Orbit.exe --cloud
~~~

The cloud catalog uses `codex cloud list --json` and loads up to 100 recent tasks. Use `--cloud` to load it on launch. Orbit E finds the native CLI on `PATH` or in the Windows Codex app package. Use `--codex-cli <path-to-codex.exe>` to select a different installation. The CLI provides task summaries; workflow graphs use locally recorded delegation activity. Hosted ChatGPT and Work chat history is separate from the Codex Cloud task list. [Codex Cloud command documentation](https://learn.chatgpt.com/docs/developer-commands?surface=cli#codex-cloud-list)

If you need to sign in, run `codex login` and complete ChatGPT’s browser sign-in. The browser returns to the official Codex CLI through its callback. Codex handles authentication and credential storage; Orbit E receives only the cloud task list. [Codex CLI authentication](https://learn.chatgpt.com/docs/auth?surface=cli)

Quit the running instance from the tray before relaunching with different arguments. Widget settings are stored in the workspace’s `.orbit\preferences.json`. For the custom event format, see [the event publisher](scripts/publish-event.ps1).

## Development

Start with [the contributor instructions](AGENTS.md) for the source map and checks. Read [project direction](docs/future.md) for the fork’s priorities and [the changelog](CHANGELOG.md) for changes made in the fork and the original project.
