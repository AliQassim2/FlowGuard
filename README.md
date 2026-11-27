# FlowGuard

Per-application bandwidth monitor, speed limiter and QoS for Windows.

- **See** which apps are using your connection, live, with download and upload speeds.
- **Cap** any app's download and/or upload speed (e.g. `steam.exe` at 500 KB/s).
- **Smart sharing by priority number** (1 = most important … 128). FlowGuard learns how fast
  your line is, gives number 1 what it needs, then 2 from what's left, and so on. Equal numbers
  share fairly. Apps without a number are left alone. Nothing is ever slowed below a minimum
  (50 KB/s by default), and a lone download still gets the whole line.
- **Game mode**: your games always come first. While one runs, a fixed amount (100 KB/s) stays
  free for it and the line is never filled completely (7 % left free), so packets don't queue
  and ping stays low. The **ping guard** slows downloads further if the measured ping rises.
- **Movie mode**: videos download in chunks every few seconds. Once a video plays, space is kept
  waiting for the next chunk (learned from the video's speed, or a fixed amount), so it arrives
  at full speed and never buffers. Built in: YouTube, Netflix, Twitch, Kick, Shahid, Prime
  Video, Disney+ and TikTok; add any site or player app.
- **Websites, not just apps**: every browser tab shares one process, so FlowGuard recognises
  websites by their address (from the TLS / QUIC handshake or DNS) and gives each tracked site
  its own row, priority and limit, in any browser.
- **Live ping** to 1.1.1.1 (or any address) once a second, against your line's normal ping.
- **Focus Mode**: the app in front of you counts as priority 1.

## Requirements

- Windows 10 or 11, **64-bit** (x64).
- **Administrator rights.** FlowGuard filters traffic with the WinDivert driver, which only an
  administrator can load. Windows asks for permission (UAC) each time it starts.
- Nothing else: .NET is built into `FlowGuard.exe`.

## Install / run

1. Unzip `FlowGuard-<version>-win-x64.zip` into a folder of its own, e.g. `C:\Tools\FlowGuard`.
2. Run `FlowGuard.exe`.

Keep the four program files together. `WinDivert.dll` and `WinDivert64.sys` must sit next to
`FlowGuard.exe`, because Windows loads the driver from that folder.

Nothing is installed. Quitting FlowGuard (tray icon → **Exit**) returns networking to normal
straight away. The WinDivert driver itself stays loaded but idle until the next restart: WinDivert
registers it so that it never starts at boot. To unload it sooner, run `sc stop WinDivert1.4` as
administrator. To remove FlowGuard, quit it and delete the folder, plus the settings in
`%AppData%\FlowGuard` if you don't want to keep them.

## Using it

| Where | What |
| --- | --- |
| Top cards | Speeds and line speed; ping; pause/resume; smart sharing, Game mode and Movie mode on/off and what they're doing. |
| Settings card | Tabs: **Priority order** (drag to reorder), **Game mode** (your games, reservation, ping guard), **Movie mode** (platforms, space kept for video), **Websites** (sites to track), **Line speed** (learned or typed), **General**. |
| App list | One row per program (all its processes together) or tracked website. Type a priority number, or a limit in KB/s (`2 MB` also works; empty = none), and press Enter. Right-click a row to mark it as a game or video, and more. |
| Show idle apps | Also lists programs with an open connection but no traffic, and every program or site you've set a rule for. |
| Tray icon | Closing or minimising the window keeps FlowGuard running in the tray. Left-click opens it; right-click for Game mode, Movie mode, smart sharing, Focus mode, Pause and Exit. |

Rules apply to the **program name** or **website**, so they also apply the next time it runs.
Settings from older versions are kept: High becomes 10, Low becomes 100.

Files:
- Settings: `%AppData%\FlowGuard\settings.json`. They save automatically; you can also edit the
  file by hand while FlowGuard is closed.
- Log: `%LocalAppData%\FlowGuard\flowguard.log` (errors and startup info).

## Good to know

- **Speeds are in KB/s, where 1 KB = 1024 bytes.** Limits allow a short burst (about 0.1 s) and
  queue up to about 0.5 s of traffic, then drop packets. TCP downloads settle at the limit within
  a second or two.
- **Sharing needs the line speed.** It is learned the first time something fills the line (a
  big download is enough) and remembered; until then nothing is slowed. Type it in under
  **Line speed** if you prefer, and use **Forget and learn again** after changing plans.
- **Only internet traffic is managed.** Local network traffic (NAS, printers, other PCs), DNS and
  pings are never slowed.
- **Website recognition has limits.** Two tabs of the same site count as one site, and sites that
  hide their name (Encrypted Client Hello together with secure DNS) can't be recognised.
- **Some anti-cheat systems and security tools block or flag packet drivers.** If the WinDivert
  driver can't load (anti-cheat, Memory Integrity / HVCI policy, or antivirus), FlowGuard shows
  an error bar at startup and nothing is filtered.
- **Shared ports are attributed to a single app.** A few system multicast ports (mDNS, LLMNR,
  SSDP) are opened by several programs at once, and their tiny traffic is counted for one of them.
- **Traffic that can't be matched to an app is shown as "Unattributed traffic".** This is usually
  a connection that closed within milliseconds.

## Building from source

Requires the .NET 10 SDK (the project targets `net10.0-windows`).

```powershell
dotnet build FlowGuard.sln            # debug build: src\FlowGuard.UI\bin\x64\Debug\...
dotnet test FlowGuard.sln             # unit tests
.\build\publish.ps1                   # release zip in artifacts\
```

`build\publish.ps1` runs the tests and publishes a self-contained, single-file, compressed
`FlowGuard.exe` (profile `src\FlowGuard.UI\Properties\PublishProfiles\win-x64.pubxml`). The
WinDivert files stay as separate files next to it. It then packs
`artifacts\FlowGuard-<version>-win-x64.zip` and prints its SHA-256. The version comes from
`Directory.Build.props`, or `-Version 1.2.3`.

Project layout: `src\FlowGuard.Core` (capture, port → process mapping, shaping, QoS, settings),
`src\FlowGuard.UI` (WPF-UI window and tray), `tests\FlowGuard.Core.Tests`. See `PLAN.md` for
the design notes.

## Licenses

FlowGuard is released under the [MIT License](LICENSE).

Third-party components: see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). WinDivert and WinDivertSharp are used under
the LGPL-3.0 and ship as separate, replaceable files; the full license texts are in
[licenses/](licenses/).
