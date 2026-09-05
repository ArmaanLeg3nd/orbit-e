# Future concerns

## 1. Cross-platform without compromising the design

Orbit is WPF on .NET Framework 4.x (`src/Widget.xaml`, `src/Program.cs`).
The look is non-negotiable: translucent dark glass (not backdrop blur),
`380x484` + `260x76` pill, bot avatar cards, slim overlay scrollbar,
Segoe/Consolas/MDL2 typography, 270ms collapse motion.

Plan (cheapest slice first):

1. Extract `Orbit.Core` (`net8.0`): `Json`, `Agent`/`ActivityEvent`/`Snapshot`
   DTOs (hex colors, no `Brush`), `JsonlTail`, `ActivityReader`,
   `AgentTree` model, `DemoFeed`. `System.Web.Script.Serialization` ->
   `System.Text.Json`; `SpecialFolder.UserProfile` -> `$HOME` on Unix;
   `WorkspaceMatches` must handle `/` + case-sensitive FS.
2. Port `ActivityReaderChecks` + `SelfTests` to `dotnet test`; assert
   snapshot parity on Windows before any UI work.
3. UI spike in **Avalonia** (closest WPF descendant): `Window.axaml`
   ports `Widget.xaml` ~85% mechanically (`TreeView`, `ScrollViewer`,
   templates). `TransparencyLevelHint="Transparent"` + `NoChrome`
   reproduces the fake glass exactly — do NOT require Acrylic/Mica
   (missing on KDE/Wayland-native). Bundle `Inter` + Fluent/Codicons;
   remap `E713/E73E/E7BA/E721/E9D9/E8D7/E943` explicitly.
4. Per-OS shell: tray (`TrayIcon`, AppIndicator on GNOME), `Topmost`
   as hint (ignored by some Wayland compositors — keep X11 path for v1),
   lockfile single-instance (not `Local\` mutex), `.orbit/preferences.json`
   stays. Publish `win-x64` / `linux-x64` / `osx-arm64` self-contained;
   installers later (Parcel / `dpkg-deb`+`.desktop` / `create-dmg`).

Rejected for v1: Electron (120MB+, Node, against README), Tauri
(sidecar IPC + WebView drift — fallback only), Qt (language break),
Uno (no built-in systray), MAUI (no Linux desktop).

## 2. Companion UI direction (Grok bots, 2025-2026)

Scouted xAI Grok companions (`Ani`, `Bad Rudi`): character-card roster,
full-screen presence mode, status-vs-personality split, semantic motion
triggers, Chat/Call duality, explicit Stop/barge-in.

Steal for Orbit's monochrome glass:

- Card roster with mode pill; click expands to presence/transcript view.
- `dot + pill = live status`, `avatar = identity` — never moodiness.
- Motion only on state transitions (pulse dot, pill change, subtle tint).
- Compact card = status + last line; expanded = activity bubbles.
- Pill-click to interrupt; never auto-steal focus or autoplay voice.

Avoid: sexualized/edgy personas, coupling utility to a 3D layer
(Grok retired full 3D in July 2026 to refocus on memory/chat depth),
burying companions behind Settings opt-in.
