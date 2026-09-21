# VRC Quick Launcher

A small launcher for VRChat. Set your launch options once, pick which profiles you want, and start one or several VRChat clients with a click, so you don't need a desktop full of shortcuts.

It doesn't include auto-updates, game installation, or an embedded website yet, since it isn't at that level, but it will get there.

Based off:
- https://github.com/hollyntt/MeowNet-Launcher

Inspired by the official [VRC Quick Launcher](https://vcc.docs.vrchat.com/tools/vrc-quick-launcher/) from the VRChat Creator Companion.

## THIS IS NOT AN OFFICIAL LAUNCHER, THIS IS A FANMADE LAUNCHER.

## Requirements

- Windows 10 or 11 (64-bit). The Browse, Open and Save As dialogs and auto-layout are Windows only.
- VRChat already installed. Point the launcher at `start_protected_game.exe` (or use **Browse**).
- Optional: SteamVR. If it's installed, the launcher registers itself so it can be listed and started from SteamVR.

## Features

- **Profiles.** Add as many as you like. Each one maps to VRChat's own `--profile=X` flag, so every profile keeps its own login and settings and you can run multiple accounts side by side.
    - Give each profile a description.
    - Launch profiles one at a time, or tick several and use **Launch all selected**.
    - Choose per profile whether it starts in VR or desktop mode (`--no-vr`).
- **Launch options** available straight from the UI:
    - Debug GUI, SDK log, UDON log
    - Watch worlds and Watch avatars
    - MIDI device, OSC config (defaults to `9000:localhost:9001`), and max FPS
    - Custom parameters, for any flag not listed above
- **Instance info.**
    - *Create* a new instance from a world ID, with access type (Public, Friends+, Friends, Invite+, Invite), region (US West, US East, Europe, Japan) and owner ID.
    - *Join* an existing instance by pasting its link.
    - *None* if you just want to open VRChat.
- **Auto-layout.** Tiles the VRChat windows across your main monitor when you launch several profiles at once.
- **Auto-close.** Closes the launcher once your clients have been launched.
- **CPU affinity.** Restrict launched clients to specific cores (Edit menu, comma-separated, e.g. `0,1,2,3`).
- **Themes.** Light, Colourful Light, Dark and Colourful Dark, from the Themes menu.
- **Saved settings.** Everything is stored in a `vrcql_config.json` next to the exe.
    - **File > Save** / **Save As...** / **Open...** let you keep and switch between configurations.
    - **Save on Exit** saves automatically when you close the app (on by default).
    - **Open Save Location** opens the folder containing your config.
- **Tiny footprint.** A single exe, plus the JSON config it creates.
- The window minimizes while VRChat is running and comes back when you close it.

## Instance links

Creating an instance builds a `vrchat://launch` link for you. Because VRChat's instance format can change, if a created instance stops resolving, try joining with a link copied from VRChat itself instead.

Also note that Friends, Invite+ and Invite instances are tied to the owner ID you enter, so that account needs to be the one launching them.

## Building

The project is C# on .NET 8 and uses [Raylib-cs](https://github.com/raylib-cs/raylib-cs), [ImGui.NET](https://github.com/ImGuiNET/ImGui.NET), rlImGui_cs and OpenVR (`Valve.VR`). Define `WINDOWS_BUILD` to enable the Windows file dialogs and auto-layout.

## Links

- [Documentation](https://github.com/hollyntt/VRChat-Quick-Launcher/wiki)
- [VRChat launch options](https://wiki.vrchat.com/wiki/Launch_Options)