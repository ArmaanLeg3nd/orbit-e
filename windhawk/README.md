# Orbit E(volved) in the Windows taskbar

The Windhawk mod adds Orbit to the leading edge of the primary Windows 11 taskbar, in the area used by Widgets. Use a horizontal native x64 Windows 11 taskbar with centered buttons and Widgets turned off. Orbit uses the free space at the leading edge and hides its button if native taskbar buttons occupy that space.

The existing Orbit companion provides the live status and the Active, All, and workflow views. In taskbar mode it starts hidden and opens an anchored panel when you click the taskbar button. Close the panel with Escape or its close control. The taskbar button shows the same working, attention, and Done states as the floating build.

## Set up the local mod

1. Run the repository's build.ps1 on Windows, or use the companion executable bundled with the mod. Keep the executable named **Orbit E(volved).exe**.
2. Place it in **%LOCALAPPDATA%\OrbitEvolved\Windhawk**, or set **Companion executable** in the mod settings to its full path.
3. In Windhawk, select **Create a New Mod** and replace the generated source with **orbit-evolved-taskbar.wh.cpp**. Compile the mod, then enable it.
4. Keep Windows Widgets off and set **Taskbar alignment** to **Center**. Click the Orbit status on the left of the primary taskbar to open the panel.

To update a local installation, replace the existing mod's editor source and compile it again. Version 0.1.3 works with the existing companion executable.

## Customize the taskbar button

Open **Windhawk → Orbit E(volved) Taskbar → Settings**. Saved display changes apply on the next status refresh, usually within one second.

- **Button width** defaults to 144 logical pixels. Choose 96–256. If there is too little free space, reduce the width.
- **Horizontal offset** sets the distance from the left edge in logical pixels. Choose 0–96. The default is 4. If the button would overlap a native taskbar control, reduce the offset or width.
- **Vertical offset** moves the button up or down from the center of the taskbar. Choose −16 to 16 logical pixels. The default is 0. Orbit limits the movement to available space.
- **Headline only** hides the second line. Hover over the button to see the full status.
- **Hide when idle** hides the whole button when there are no active chats or unread completions. Leave it off if you want to open archived or idle chats from the taskbar at any time.

Windhawk starts the matching companion in the background. The companion can also be opened with the **--taskbar-open** argument for troubleshooting. Disable the mod in Windhawk to remove the taskbar control and stop the taskbar companion. The floating build has its own instance and remains available on the **orbit-evolved** branch.

Codex rollout parsing stays in the companion. Only the two status lines cross into Explorer over session-local shared memory. The taskbar mod uses Windhawk's symbol lookup for the native XAML taskbar host, so a Windows update can change compatibility. The host discovery approach is adapted from [Taskbar Media Widget by uMk0](https://windhawk.net/mods/taskbar-media-widget), which is MIT-licensed.
