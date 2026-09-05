# Orbit

A small native Windows widget for watching your Codex agents work. Proof of concept: no installer, no account, no network — just build and run.

```powershell
.\build.ps1 -Run        # build and watch live Codex activity
.\build.ps1 -Run -Demo  # build and watch the simulated preview
.\build.ps1 -Test       # build and run the checks (116, all must pass)
```

Requires Windows 10/11 with .NET Framework 4.x (preinstalled). No SDK, no packages. The build drops `bin/Orbit.exe`; double-click it to open.

## Use it

- It follows the newest Codex session in this project and shows its team: `/root` lead plus subagents, each with its latest activity underneath. Click any row for details.
- Drag the header to move it (it snaps to corners). Minus or Esc collapses to a pill; click the pill to expand. Ctrl+Q quits.
- Colors mean status only: blue working, amber waiting/needs-you, rose error, gray idle/done.
- The footer tells you how fresh the data is (`Synced`, `12s ago`, `Catching up…`). Quiet agents go idle after 5 minutes; silence is never shown as done.
- Right-click the tray icon for show, hide, live, preview, and quit. Running it twice just reveals the open widget.

## Point it elsewhere

```powershell
.\bin\Orbit.exe --workspace 'D:\projects\my-project'
.\bin\Orbit.exe --workspace 'D:\projects\my-project' --session '<thread-id>'  # pin one session
.\bin\Orbit.exe --demo                                                        # simulated preview
```

Close the widget before relaunching with different flags. Settings live in the workspace's `.orbit/preferences.json`.

## Limits (it's a PoC)

- Reads local `CODEX_HOME/sessions` rollouts only — an internal Codex format that can change. Cloud-only sessions are invisible. Read-only: it can't start, stop, or approve anything.
- One widget per desktop; newest session takes focus automatically.
- Windows only, unsigned dev build, no auto-start. Other agent systems can feed it JSONL via `--events` (see `scripts/publish-event.ps1`).

## Project

- `src/Widget.xaml`: layout and styles.
- `src/Program.cs`: tray, window behavior, live updates.
- `src/Activity.cs`: session file tailing and activity parsing.
- `src/AgentTree.cs`: tree grouping and expansion state.
- `src/ActivityReaderChecks.cs`, `src/SelfTests.cs`: regression and UI checks.
- `CHANGELOG.md`: what changed and why. `docs/future.md`: where this could go.
