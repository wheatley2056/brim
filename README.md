<p align="center">
  <img src="docs/assets/header.png" alt="Brim — Everything at the edge." width="820">
</p>

<p align="center">
  <b>A notch for Windows.</b> Media, files, sharing and system at a glance —<br>
  and gone the moment a window needs the space.
</p>

<p align="center">
  <a href="https://github.com/wheatley2056/brim/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/wheatley2056/brim?style=flat-square&color=FF8A66&label=release"></a>
  <a href="https://github.com/wheatley2056/brim/actions/workflows/release.yml"><img alt="Release build" src="https://img.shields.io/github/actions/workflow/status/wheatley2056/brim/release.yml?style=flat-square&label=build"></a>
  <img alt="Windows 10 and 11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-1C1D21?style=flat-square&logo=windows">
  <img alt=".NET 10 · WinUI 3" src="https://img.shields.io/badge/.NET%2010-WinUI%203-8C7BFF?style=flat-square">
  <a href="LICENSE"><img alt="License GPL-3.0" src="https://img.shields.io/github/license/wheatley2056/brim?style=flat-square&color=5BD98A"></a>
</p>

<p align="center">
  <a href="https://github.com/wheatley2056/brim/releases/latest/download/BrimNotch-win-Setup.exe"><b>⬇ Download for Windows</b></a>
  &nbsp;·&nbsp;
  <a href="#features">Features</a>
  &nbsp;·&nbsp;
  <a href="#build-from-source">Build</a>
  &nbsp;·&nbsp;
  <a href="README.de.md">Deutsch</a>
</p>

---

Brim puts a living notch at the top of your screen — the way the Dynamic Island feels on a phone, built natively for Windows with official APIs only. It stays tiny while nothing happens, grows when something does, and gets out of the way the moment a window needs the space.

<p align="center">
  <img src="docs/assets/player.png" alt="Brim expanded at the top of the screen: now playing with cover, progress and controls" width="760"><br>
  <sub>Screenshots of the real app with demo content. The interface is German for now.</sub>
</p>

## Features

### 🎵 Now playing
Cover, title, progress and controls for anything that talks to Windows media controls — Spotify, browsers, Media Player, games. The notch borrows its glow from the cover. Seek by dragging, switch the audio output, or change the volume of every app right inside the notch.

### ✨ One body, many shapes
Every state — idle clock, music, volume, timer, alerts — is the same shape morphing with real spring physics (SwiftUI-style response and damping), rendered by the compositor at your monitor's refresh rate: 60, 144 or 240 Hz. Hover and it swells; linger and it opens from that motion. The clock glides into the header instead of jumping.

<p align="center">
  <img src="docs/assets/states.png" alt="Three compact states: idle clock, now playing, and an update notice with the music split off as a bead" width="820"><br>
  <sub>Idle · now playing · two activities — the second one splits off as a bead.</sub>
</p>

### 📁 Shelf
Drag files or folders onto the notch and they stay there until you remove them — even after a restart. Drag them out into Explorer, mail, Discord or a browser upload. Files from temporary places (downloads, attachments) are copied so they don't vanish; missing ones are marked, not hidden.

<p align="center">
  <img src="docs/assets/shelf.png" alt="Shelf tab with five files and folders, plus receive, share and clear buttons" width="760">
</p>

### 📡 Share — nearby and anywhere
- **Nearby:** other Brims on your network appear automatically (DNS-SD). Files go directly, encrypted with mutual TLS and verified certificate fingerprints. Nothing is received without your OK.
- **Anywhere, by code:** send to anyone with a short code like `7-guitarist-revenge`. Brim speaks the open [Magic Wormhole](https://magic-wormhole.readthedocs.io) protocol, so the other side can be Brim — or `wormhole receive`, Warp, Winden and friends. End-to-end encrypted (SPAKE2), works behind NAT via relay and through HTTP or SOCKS5 proxies.
- **Windows share** is still one click away for mail, Teams and other apps.

### 📊 System at a glance
The System tab shows CPU, RAM, GPU and network with one-minute history, plus the three busiest processes — read from the same performance counters as Task Manager, so the numbers match. It only measures while the tab is open. On laptops, Brim shows battery level and time left, a charging HUD when you plug in, and warns once when the battery runs low. Brightness changes get their own HUD, and a green or orange dot tells you when an app is using your camera or microphone.

### 🫥 Gets out of the way
When a window sits under the notch — maximized, fullscreen, or just pushed up — Brim melts into the edge. Dragging a window toward the top for Snap Layouts hides it too. While you share your screen in Teams, Zoom, Meet or Webex it stays out of the picture. Per-app rules (always show for OBS, always hide for Premiere), a desktop-only mode, hiding when idle and a hide delay are in the settings. Rest your mouse at the top edge or press Win+Alt+N and it drops back down.

### 🔄 Updates you won't notice
Brim updates itself from GitHub releases in the background, downloads only what changed, and restarts only when you're away for a few minutes. Afterwards the notch tells you what's new.

<p align="center">
  <img src="docs/assets/update.png" alt="Changelog in the notch after an update, with a Got it button" width="760">
</p>

### 🎮 Games
Brim measures FPS the way PresentMon does, from Windows' own DXGI and Direct3D 9 events, for the game in front of you: frame rate, 1 % low, a frametime graph and optionally the ping to your game server. While you play, the notch steps aside. If you like, a small translucent pill with "FPS · ms" stays at the top, or Win+Alt+F shows it for five seconds. Turn it on under Settings → Games. The first time, Brim asks once for admin rights to add you to the "Performance Log Users" group. After that, measuring runs without admin rights.

### 🧩 Plugins
Add your own content to the notch: a compact status, a HUD, or cards in the Plugins tab. Plugins run as separate processes in any language and describe their UI as data, so they can't break the notch or freeze it. A plugin that crashes is restarted and switched off after the third crash. New plugins start switched off, and the Plugins tab shows what each one may do. See [`docs/plugins.md`](docs/plugins.md) and the [samples](samples). A first real plugin is [`plugins/SwyxIt`](plugins/SwyxIt). It shows incoming SwyxIt! calls with Answer and Decline, running calls, and chat messages.

### ♿ Built for everyone
UI Automation names for everything, screen reader announcements for HUDs, keyboard access with <kbd>Win</kbd>+<kbd>Alt</kbd>+<kbd>N</kbd>, and a gentle fade instead of motion when Windows animations are turned off.

## Install

1. Download **[BrimNotch-win-Setup.exe](https://github.com/wheatley2056/brim/releases/latest/download/BrimNotch-win-Setup.exe)** from the latest release.
2. Run it. Brim installs for your user only (no admin rights), starts right away and with Windows.

> [!NOTE]
> The installer isn't code-signed yet, so Windows SmartScreen may say *"Windows protected your PC"*. Click **More info → Run anyway**. Signing is on the roadmap.

**Requirements:** Windows 10 22H2 or Windows 11, x64. Everything else is included.

**Getting around**

| | |
|---|---|
| Hover the notch | It swells, then opens |
| <kbd>Win</kbd>+<kbd>Alt</kbd>+<kbd>N</kbd> | Open/close from the keyboard |
| Drag a file onto it | Keep it on the shelf |
| Mouse to the top edge | Bring a hidden notch back |
| Tray icon | Settings file, logs, quit |

Settings open from the tray icon (right-click → Settings) and are stored in `%LocalAppData%\Brim\settings.json`. The interface is German for now; English is planned.

## Privacy & security

- **No telemetry, no accounts, no Brim servers.** Brim talks to the internet only to check GitHub for updates and — when you send or receive by code — to the public Magic Wormhole servers, which only ever see encrypted data.
- **Nearby sharing** uses mutual TLS with per-install certificates; the fingerprint announced on the network must match the connection.
- **Incoming files** always need your confirmation; paths are sanitized and archives are checked against path traversal and zip bombs.
- One deliberate exception to "official APIs only": switching the default audio device uses the undocumented `IPolicyConfig` interface (as EarTrumpet and SoundSwitch do), because Windows offers no public API for it.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). No Visual Studio needed.

```bash
git clone https://github.com/wheatley2056/brim.git
cd brim
dotnet build Brim.slnx
dotnet run --project src/Brim
```

```bash
dotnet test Brim.slnx
```

Network and interoperability tests against the original Magic Wormhole run when `BRIM_WORMHOLE_CLI` points to a `wormhole.exe` (`pip install magic-wormhole`).

**Screenshots:** `pwsh build/screenshots/make.ps1` rebuilds the images in `docs/assets` from the real app in demo mode (`BRIM_DEMO`: sample song and files, separate data folder, no network).

**Releasing:** add a `## <version>` section to [`CHANGELOG.md`](CHANGELOG.md), then push a tag `v<version>`. GitHub Actions builds, packs with [Velopack](https://velopack.io) (including delta updates) and publishes the release; installed Brims pick it up on their own.

<details>
<summary><b>Architecture</b></summary>

| Project | What's inside |
|---|---|
| `src/Brim.Core` | Activities and the activity hub, the notch state machine, declarative widgets, shape geometry, spring physics, shelf store, settings. No Windows dependencies, fully unit-tested. |
| `src/Brim.Modules` | Built-in modules — clock, media, audio, battery, resources, brightness, camera/mic, sharing (LAN and Magic Wormhole), updates. They describe their UI as widgets; no WinUI. |
| `src/Brim.Plugins` | Plugin host: finds plugins, starts them as processes, speaks JSON-RPC over named pipes, validates their widgets, restarts after crashes. |
| `src/Brim.Telemetry` | FPS measurement via ETW in its own process (`Brim.exe --telemetry`), plus the one-time permission setup. |
| `src/Brim.Sdk` | SDK for .NET plugins. The protocol itself is documented in [`docs/plugins.md`](docs/plugins.md). |
| `src/Brim` | The WinUI 3 shell: one window per monitor, the compositor-driven morph engine, widget renderer, shelf, auto-hide, tray and updater. |
| `tests/` | State machine, springs, geometry, shelf, sharing protocol, SPAKE2 test vectors from the reference implementation, live interop with the original Magic Wormhole. |

Built-in features are modules that use the same interface plugins will: they post activities with a priority and describe their UI as data — the shell decides what to show. See [`plan.md`](plan.md) (German) for the full design.
</details>

## Roadmap

- [x] **Foundation** — notch, clock, media, volume, spring morph, multi-monitor
- [x] **Shelf** — drag in, keep, drag out
- [x] **Sharing** — nearby (LAN) and by code (Magic Wormhole), proxy support
- [x] **Silent updates** with in-notch changelog
- [x] **Auto-hide** — fullscreen, maximized, overlap, desktop-only, idle, per-app rules, screen-sharing detection, top-edge peek *(settings window comes with 1.0)*
- [x] **System** — battery, CPU, RAM, GPU, network, brightness, camera/mic indicators *(disk and external monitors via DDC/CI still to come)*
- [x] **Plugins** — open protocol and SDK for your own notch content *(shelf actions and a plugin gallery still to come)*
- [x] **Games** — FPS, 1 % low, frametime and ping, as a mini pill or on demand *(display latency like PresentMon and Vulkan/OpenGL titles still to come)*
- [x] **Timer, stopwatch, Bluetooth connect HUD**
- [ ] **More** — calendar and notifications (need MSIX), settings window, code signing, winget

## Credits

Built on [Windows App SDK / WinUI 3](https://github.com/microsoft/WindowsAppSDK), [Windows Community Toolkit](https://github.com/CommunityToolkit/Windows), [WinUIEx](https://github.com/dotMorten/WinUIEx), [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon), [NAudio](https://github.com/naudio/NAudio), [Win2D](https://github.com/microsoft/Win2D), [libsodium](https://libsodium.org), [CsWin32](https://github.com/microsoft/CsWin32) and [Velopack](https://velopack.io). Sharing by code implements the [Magic Wormhole](https://github.com/magic-wormhole/magic-wormhole) protocol by Brian Warner and contributors.

## License

Brim is free software under the [GNU General Public License v3.0](LICENSE).
