# Orbit E(volved) in the Windows taskbar

The Windhawk mod adds Orbit to the leading edge of the primary Windows 11 taskbar, in the area used by Widgets. Use a horizontal native x64 Windows 11 taskbar with centered buttons and Widgets turned off. Orbit uses the free space at the leading edge and hides its button if native taskbar buttons occupy that space.

The Orbit companion provides the live status and the Active, All, and workflow views. It starts hidden. Click the taskbar button to open the anchored panel. Click again, click outside the panel, or press Escape from the chat list to close it. The button and expanded panel show an activity indicator while agents are working. Orbit and Windows animation settings control whether it spins.

## Set up the local mod

1. Run the repository's build.ps1 on Windows, or use the companion executable bundled with the mod. Keep the executable named **Orbit E(volved).exe**.
2. Place it in **%LOCALAPPDATA%\OrbitEvolved\Windhawk**, or set **Companion executable** in the mod settings to its full path.
3. In Windhawk, select **Create a New Mod** and replace the generated source with **orbit-evolved-taskbar.wh.cpp**. Compile the mod, then enable it.
4. Keep Windows Widgets off and set **Taskbar alignment** to **Center**. Click the Orbit status on the left of the primary taskbar to open the panel.

To update to version 0.1.5, update the companion executable, then replace the existing Windhawk mod's editor source and compile it again.

## Customize the taskbar button

Open **Windhawk → Orbit E(volved) Taskbar → Settings**. Saved display changes apply on the next status refresh, usually within one second.

- **Button width** defaults to 144 logical pixels. Choose 96–256. If there is too little free space, reduce the width.
- **Horizontal offset** moves the button left or right from the taskbar's left edge. Choose −96 to 96 logical pixels. The default is 4. Negative values tuck the button toward the edge while keeping the status visible and clickable. If the button would overlap a native taskbar control, reduce the offset or width.
- **Vertical offset** moves the button up or down from the center of the taskbar. Choose −16 to 16 logical pixels. The default is 0. Orbit limits the movement to available space.
- **Headline only** hides the second line. Hover over the button to see the full status.
- **Hide when idle** hides the whole button when there are no active chats or unread completions. Leave it off if you want to open archived or idle chats from the taskbar at any time.

For the expanded panel, open Orbit's gear menu and find **Panel position**. Set the horizontal offset from the left edge and the gap from the taskbar, then choose **Apply**. **Reset** restores the eight-pixel defaults. These controls use the updated companion executable.

Windhawk starts the matching companion in the background. The companion can also be opened with the **--taskbar-open** argument for troubleshooting. Disable the mod in Windhawk to remove the taskbar control and stop the taskbar companion. The floating build has its own instance and remains available on the **orbit-evolved** branch.

Codex rollout parsing stays in the companion. The two status lines and activity indicator state cross into Explorer over session-local shared memory. The taskbar mod uses Windhawk's symbol lookup for the native XAML taskbar host, so a Windows update can change compatibility. The host discovery approach is adapted from [Taskbar Media Widget by uMk0](https://windhawk.net/mods/taskbar-media-widget), which is MIT-licensed.
