# FlowGuard — Implementation Roadmap

Per-application bandwidth monitor, hard speed limiter and dynamic QoS for Windows.
Stack: C# / WPF, .NET CLI + VS Code, WinDivert (via `WinDivertSharp`), `iphlpapi.dll`, WPF-UI.

Status legend: `[x]` done · `[~]` in progress · `[ ]` not started

---

## Architecture at a glance

```
FlowGuard.sln
├── src/FlowGuard.Core      (class library, net8.0-windows, x64, unsafe allowed)
│   ├── Interop/            P/Invoke: iphlpapi (GetExtendedTcpTable / GetExtendedUdpTable)
│   ├── Mapping/            PortPidMapper: lock-free port→PID tables, periodic + on-miss refresh
│   ├── Packets/            PacketParser (Span<byte>, zero-alloc IPv4/IPv6 + TCP/UDP header parse)
│   ├── Engine/             PacketEngine: WinDivert recv → classify → (shape) → reinject loop
│   ├── Processes/          PID → exe name/path/creation time
│   ├── Shaping/            TokenBucket, FlowLane (bucket + delay queue), ShapingGroup, TrafficShaper
│   ├── Stats/              per-PID counters, ProcessTable, TrafficSnapshot
│   ├── FlowGuardService    composition root: sampler, limits API, sniff/divert switching
│   ├── Qos/                Priority rules, QosController (auto-throttle), ForegroundWatcher
│   └── Settings/           FlowGuardSettings + SettingsStore (JSON, auto-save)
├── tests/FlowGuard.Core.Tests  xunit
└── src/FlowGuard.UI        (WPF app, net8.0-windows, x64, requireAdministrator)
    ├── app.manifest
    ├── Views / ViewModels  (Phase 4) WPF-UI Fluent dark theme, process list, limit editor
    └── Tray                (Phase 4) Wpf.Ui.Tray NotifyIcon, minimise-to-tray
```

Data flow for every packet:
`WinDivertRecv` → `PacketParser` (direction, protocol, local port, length) → `PortPidMapper.TryGetPid`
→ `StatsCollector.Add(pid, dir, bytes)` → `Shaper.Admit(pid, dir, bytes)` (send now / delay / drop)
→ `WinDivertSend`.

---

## Phase 1 — Scaffolding, Port→PID mapping, basic packet capture  `[x]`

- [x] `dotnet new sln`, `classlib` (Core), `wpf` (UI), wire with `dotnet sln add` / `dotnet add reference`
- [x] Shared `Directory.Build.props` (nullable, latest C#, x64, `AllowUnsafeBlocks`)
- [x] NuGet: `WinDivertSharp` (Core), `WPF-UI` + `WPF-UI.Tray` (UI)
- [x] `WinDivert.dll` + `WinDivert64.sys` copied to the output directory on build
- [x] `app.manifest` with `requireAdministrator`
- [x] `IpHelperApi` P/Invoke (`LibraryImport`, source-generated, no marshalling allocations)
- [x] `PortPidMapper`: TCP + UDP, IPv4 + IPv6 (QUIC/UDP 443 covered), double-buffered `int[65536]`
      tables swapped atomically → O(1), lock-free lookups from the packet thread; periodic refresh
      plus rate-limited on-miss refresh for brand-new sockets
- [x] `PacketParser`: zero-allocation `ReadOnlySpan<byte>` parse of IPv4/IPv6 + TCP/UDP
- [x] `PacketEngine` skeleton: open WinDivert, recv loop on a dedicated thread, classify to PID,
      reinject (or sniff-only mode), raise lightweight counters
- [x] `dotnet build` succeeds

**Exit criteria:** solution builds; mapper returns correct PIDs for live sockets (verified against
`netstat -ano`).

**Verified 2026-11-21:**
- `dotnet build` → 0 warnings, 0 errors. Output contains x64 `WinDivert.dll` + `WinDivert64.sys`;
  `FlowGuard.exe` embeds `requireAdministrator`.
- Mapper probe vs `netstat -ano`: 198/218 live sockets match; all 20 differences are UDP 5353 (mDNS),
  shared by several processes (see port-collision note below). Lookup ≈ 11 ns; full refresh 1–9 ms.
- `PacketEngine` verified against the real driver during Phase 2 (see below).

## Phase 2 — Speed calculation & Token Bucket throttling  `[x]`

- [x] Per-PID counters: `Stats/ProcessTrafficState` (Interlocked down/up/dropped) in a lock-free
      `Stats/ProcessTable`; exe name resolved once per PID (`Processes/ProcessResolver`,
      `QueryFullProcessImageName`), PID reuse detected via process creation time
- [x] Sampler in `FlowGuardService` (1 s): B/s with EMA smoothing (α 0.6, snaps to 0 when idle),
      evicts processes idle > 60 s or exited, publishes immutable `TrafficSnapshot` +
      `SnapshotUpdated` event, sorted by speed
- [x] `Shaping/TokenBucket`: debt semantics (admit while balance ≥ 0) so 64 KB LSO frames pass
      any burst size with an exact long-run rate; explicit tick time → deterministic tests;
      `Configure` retunes without resetting debt (QoS-safe)
- [x] `Shaping/FlowLane`: bucket + strict-FIFO delay queue (`ArrayPool<byte>` copies), tail-drop
      beyond max(500 ms at cap, 16 KB) or 4 MB
- [x] `Shaping/ShapingGroup`: **one cap per executable name, shared by all its processes**;
      `EffectiveLimit = min(HardLimit, DynamicCap)` — `DynamicCap` is the Phase 3 QoS hook
- [x] `Shaping/TrafficShaper` (`IPacketHandler`): copy-on-write group map + config version so the
      hot path never locks for unlimited processes; scheduler thread re-injects queued packets
- [x] Engine: `PacketVerdict.Queued`, `IPacketInjector` (engine re-injects from the shaper thread,
      WinDivert types stay inside `PacketEngine`), `Restart(sniffOnly)`
- [x] Hard caps API: `FlowGuardService.SetLimit / SetLimitKBps / ClearLimit / GetLimits`
      (`"steam"` and `"Steam.exe"` normalise to `steam.exe`)
- [x] Auto mode switch: divert while any cap is active or packets are queued; back to sniff after
      a 10 s hold (avoids handle churn when QoS toggles)
- [x] `tests/FlowGuard.Core.Tests` (xunit, 32 tests): TokenBucket math, RateLimit, PacketParser
      (IPv4/IPv6, fragments, extension headers, truncation), FlowLane FIFO / tail-drop / drain,
      TrafficShaper per-exe targeting, sniff safety, scheduler release rate

**Verified 2026-11-21 (elevated probe, real WinDivert 1.4 driver on Windows 11 26200):**
- Driver loads and diverts; sniff run: 28,388 packets, 4 unattributed.
- `curl.exe` capped ↓300 KB/s → measured 287 KB/s; ↑150 KB/s → measured 143 KB/s (curl's average
  includes TLS setup). Cap lifted → 5.6 MB/s; engine fell back to sniff after the hold time.
- 0 send failures, 0 bytes left queued, normal connectivity after `Stop()`.

**Follow-ups noted (not blocking):**
- Scheduler sleeps use the default ~15.6 ms timer; fine for caps, but a high-resolution waitable
  timer would smooth very low caps.
- In divert mode *all* IP traffic transits user mode (single worker); measure CPU at gigabit
  rates in Phase 4 and raise `WorkerThreads` if needed.

## Phase 3 — Dynamic Priority & Smart Focus Mode  `[x]`

- [x] `Qos/Priority` (`High`, `Normal`, `Low`) + rules keyed by exe name in `Qos/QosController`
      (`Normal` = no rule). **Everything is per executable, not per PID**: a browser's foreground
      window and the process moving bytes (Chrome's network service) are different PIDs of one exe
- [x] Smart Auto-Throttle state machine, evaluated every sample (sampler now 500 ms):
      activate when any effective-High exe's down+up > 150 KB/s; stay active while it is
      > 50 % of that (hysteresis); release after 5 s hold below it. While active every
      effective-Low exe gets `ShapingGroup.DynamicCap` = 50 KB/s each way
- [x] Effective limit = `min(hardLimit, dynamicCap)` → recovery returns Low apps to their own hard
      limit or full speed; a hard limit stricter than the fallback is kept while throttling
- [x] Smart Focus Mode: `Qos/ForegroundWatcher` — `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` on
      its own message-loop thread (no UI dependency, no polling); foreground exe → effective High,
      even if its rule is Low. FlowGuard's own windows are skipped (`WINEVENT_SKIPOWNPROCESS`);
      shell surfaces (explorer, Start, search, lock screen…) never steal focus
- [x] UWP: ApplicationFrameHost frames resolved to the hosted app — child `CoreWindow`, else the
      detached top-level `Windows.UI.Core.CoreWindow` with the frame's title, plus a 100 ms × 15
      retry for the activation race
- [x] Settings: `Settings/FlowGuardSettings` + `SettingsStore` (System.Text.Json source-gen,
      camelCase, enums as strings, comments/trailing commas tolerated, names normalised,
      atomic temp+move save, corrupt file → `settings.json.bad`). `FlowGuardService` loads from
      `FlowGuardOptions.SettingsPath` and auto-saves 500 ms after any change (flushed on Dispose)
- [x] Snapshot carries QoS: `ProcessTrafficInfo.Priority / IsForeground / IsQosThrottled /
      HardLimit`, `TrafficSnapshot.Qos` (throttling, triggering + throttled exes, foreground)
- [x] Tests: 19 new (51 total) — thresholds, hysteresis, hold time, hard-limit interplay,
      demotion, disable, focus promotion/protection/ignore list, settings round-trip + service
      persistence

**Verified 2026-11-21:**
- Foreground watcher live: reports focus switches; UWP frames resolve (Calculator →
  `CalculatorApp.exe`, Settings → `SystemSettings.exe`). The initial bug (frame reported as
  `ApplicationFrameHost.exe`) was found live and fixed with the detached-CoreWindow fallback.
- Elevated auto-throttle probe on the real driver: Low download 1.2 MB/s → **50 KB/s** within 0.5 s
  of the High download starting; released 3.4 s after High stopped (3 s hold + EMA), recovered to
  full speed; engine switched sniff → divert → sniff automatically.
- Not verified live: Focus Mode end-to-end with a user-driven focus change (Windows' foreground
  lock prevents a background process from taking focus); covered by unit tests + watcher probe.

**Notes:**
- Games are latency- not bandwidth-bound and often stay under 150 KB/s; with Focus Mode the
  focused game is High but may never *trigger*. Lower the threshold (settings) if needed — a
  per-app "always protect while running" option is a candidate for later.

## Phase 4 — WPF-UI interface & System Tray  `[x]`

- [x] WPF-UI `FluentWindow`, Mica backdrop, dark theme, MVVM (`CommunityToolkit.Mvvm` 8.4)
- [x] Process list, **one row per executable** (limits/priorities are per exe in the core; the PID
      column shows the PID or "N processes" with the list in a tooltip): icon
      (`ExtractAssociatedIcon`, cached per path), name + status pills (Focused / Throttled / Capped),
      PID, ↓/↑ speed, priority dropdown, ↓/↑ limit boxes (KB/s, also accept "2 MB"; empty = none;
      Enter commits, Esc reverts); sorted by speed; search box; "Show idle apps" (off by default)
      adds every process with an open socket plus every app that has a rule
- [x] Order freezes while the pointer is moving over the list, a limit box is focused or a
      priority dropdown is open, so rows don't jump under the cursor
- [x] Global controls: summary cards (total ↓/↑, engine on/off + mode, auto-throttle on/off +
      live status and the exe that triggered it, Focus Mode on/off + focused exe); settings card
      with threshold, fallback, hold time, close-to-tray, start-in-tray, open settings/log
- [x] UI refresh: 1 s `DispatcherTimer` reading `FlowGuardService.Snapshot`, diffed into an
      `ObservableCollection` (rows reused, Move/Insert/Remove); list work skipped while hidden
- [x] Tray (`Wpf.Ui.Tray` NotifyIcon): close/minimise to tray (setting, on by default), left-click
      opens, menu (Open, Focus Mode, Auto-throttle, Pause/Resume, Exit), tooltip with state and
      totals. Start-in-tray renders once off-screen so the icon registers, then hides
- [x] Single instance (named mutex; a second launch asks the first to show its window)
- [x] Graceful shutdown on Exit / sign-out: tray removed, engine stopped (normal networking),
      pending settings flushed. Startup failures show a message box instead of hanging;
      engine start failures (e.g. not elevated) show an in-window error bar
- [x] Diagnostics log `%LocalAppData%\FlowGuard\flowguard.log` (1 MB rotation); app icon
      `Assets/flowguard.ico`. Debug builds accept `--render/--show-idle/--set-limit/...` hooks
      (`DebugHooks.cs`) for automated smoke tests

### Verified (2026-11-21)
- Elevated: engine starts, live rows with icons (chrome, curl at ~1 MB/s, Discord ×2 processes…),
  tray icon registered both when shown and when started in the tray, clean exit code 0.
- Not elevated: clear "must run as Administrator" error bar, UI still usable.
- Editing through the view model: "2 MB" on chrome.exe → 2048 KB/s hard limit, Discord → Low;
  both auto-saved to `settings.json` (test file removed afterwards); Show idle listed 28 apps.
- Not verified by automation: real mouse/keyboard interaction, tray menu clicks, tooltip text,
  Focus Mode with a real focus change. Check these by hand.

## Phase 5 — Single-file release packaging  `[x]`

- [x] Publish profile `src/FlowGuard.UI/Properties/PublishProfiles/win-x64.pubxml`: Release,
      win-x64, self-contained, `PublishSingleFile`, `IncludeNativeLibrariesForSelfExtract`
      (WPF natives extracted once to `%TEMP%\.net\FlowGuard\`), compression on, embedded PDBs
      (log stack traces keep line numbers), `SatelliteResourceLanguages=en`. Bundles .NET 10.0.15
      (was 8.0.34 before the .NET 10 retarget)
- [x] WinDivert stays **outside** the bundle (`KeepWinDivertBesideExe` target sets
      `ExcludeFromSingleFile`): `WinDivert64.sys` can't load from a bundle and `WinDivert.dll`
      loads it from its own folder; `WinDivert.dll` + `WinDivertSharp.dll` are LGPL-3.0 (or
      GPL-2.0), so they stay user-replaceable. Release = `FlowGuard.exe` + those 3 files
- [x] ReadyToRun / trimming evaluated (trimming unsupported for WPF). Not elevated, start →
      first frame (first run / later runs), measured on .NET 8:

      | variant                | exe      | first   | later  |
      |------------------------|----------|---------|--------|
      | plain                  | 145.3 MB | 3.0 s   | 1.46 s |
      | **compressed** (chosen)| 65.5 MB  | 3.2 s   | 1.80 s |
      | ReadyToRun             | 163.9 MB | 5.3 s   | 1.32 s |
      | ReadyToRun + compressed| 70.6 MB  | 4.8 s   | 1.97 s |

      The framework is already precompiled, so R2R only helps our code + WPF-UI (~0.15 s).
- [x] Version 1.0.0 + product metadata in `Directory.Build.props` (exe file version 1.0.0.0);
      app icon from Phase 4
- [x] `README.md` (requirements, install, usage, caveats, build) and `THIRD-PARTY-NOTICES.md`
- [x] `build/publish.ps1`: tests → publish → checks the file set → stages
      `artifacts/FlowGuard-<ver>-win-x64/` (+ README, notices) → zip + SHA-256. `-Version`, `-SkipTests`
- [x] Startup now logs each step (core ready, window created, engine starting/started + mode)

### Verified (2026-11-21)
- `build/publish.ps1`: 51 tests pass, zip 60.2 MB, exe 65.5 MB.
- Release exe elevated, from the staged folder: WinDivert.dll/WinDivertSharp.dll load from that
  folder; monitor-only start; with a 300 KB/s cap on curl.exe in settings, curl measured
  283 KB/s. Debug build same result. Not elevated: clear error bar, UI works.
- **Unexplained:** the very first elevated run of the release exe logged "starting" and nothing
  else (no crash record, no capping) and did not recur in 3 later elevated runs. The new
  startup-step log lines will show where it stops if it happens again.
- WinDivert 1.4 leaves its driver loaded (idle, start type Disabled) after FlowGuard exits until
  reboot; README documents `sc stop WinDivert1.4`.
- Full LGPL-3.0 / GPL-3.0 / GPL-2.0 texts (from gnu.org) live in `licenses/` and ship in the zip.

---

## Known risks / decisions log

- **WinDivert filter:** `inbound and outbound and ip` matches *nothing* (a packet is never both).
  We use `ip and !loopback` (both directions; IPv4 only) — or `(ip or ipv6) and !loopback` when
  IPv6 support is enabled.
- **WinDivertSharp** wraps the legacy WinDivert **1.4** API. If the 1.4 driver fails to load on a
  current Windows 11 build (driver signing / HVCI), fall back to a thin custom P/Invoke over
  WinDivert 2.2 — the `PacketEngine` keeps the WinDivert calls isolated to make that swap cheap.
- Port→PID lookup keys on (protocol, local port) only, ignoring local address — collisions are
  rare on a desktop and it keeps the lookup a single array index. Known exception: shared
  multicast ports (UDP 5353 mDNS, 5355 LLMNR, 1900 SSDP) bound by many processes resolve to just
  one of them; their traffic is negligible.
- **WinDivert natives:** the package's own `.targets` (copies x86 + x64 into subfolders) is
  disabled with `ExcludeAssets="build"`; `FlowGuard.Core.csproj` copies only the x64
  `WinDivert.dll` + `WinDivert64.sys` into the output root, and the copy flows to `FlowGuard.UI`.
- **Target framework: `net10.0-windows` (LTS)** since 2026-11-21. The spec asked for .NET 8, but
  .NET 8 support ended 2026-11-10; all three projects were retargeted (tests no longer need
  `RollForward`). The release bundles .NET 10.0.15.
- Engine starts in **sniff mode** (`PacketEngineOptions.SniffOnly = true`) so Phase 1 can never
  disrupt traffic; Phase 2 switches to divert mode when a limit is active.

## Session log

- 2026-11-21 — Phase 1 implemented (scaffold, packages, manifest, PID mapper, capture skeleton).
- 2026-11-21 — Phase 2 implemented: stats sampler, token bucket shaper, exe-keyed hard caps,
  auto sniff/divert switching, 32 unit tests; live-verified caps against the real driver.
  Next: Phase 3 (priorities, auto-throttle via `ShapingGroup.DynamicCap`, focus mode).
- 2026-11-21 — Phase 3 implemented: priority rules, Smart Auto-Throttle (hysteresis + hold),
  Smart Focus Mode (WinEvent hook, UWP-aware), JSON settings with auto-save; 51 tests;
  auto-throttle live-verified. Next: Phase 4 (WPF-UI + tray) on top of `FlowGuardService`.
- 2026-11-21 — Phase 4 implemented: Fluent dark UI (per-exe rows, editors, QoS/Focus cards),
  tray, single instance, graceful shutdown, log; smoke-tested elevated and non-elevated.
  Next: Phase 5 (single-file publish).
- 2026-11-21 — Phase 5 implemented: single-file self-contained compressed exe with WinDivert
  beside it, R2R evaluated (not used), version info, README, third-party notices, release
  script; release exe verified elevated (cap measured 283 KB/s for 300 KB/s).
- 2026-11-21 — Retargeted all projects to `net10.0-windows` (.NET 8 is out of support): 0 warnings,
  51 tests pass, release exe 61.2 MB / zip 55.9 MB with .NET 10.0.15 bundled. Installed locally
  to `%LocalAppData%\Programs\FlowGuard` with Start menu + desktop shortcuts for manual testing.
- 2026-11-21 — **Auto-throttle fix (user report):** Steam (Low) saturated the line, so Chrome
  (High) never got near the threshold (user had set 5000 KB/s) and throttling never started.
  Added a *contention trigger*: a High app above `ActiveBytesPerSecond` (16 KB/s) while Low apps
  together pull > 2 x the Low cap throttles at once; throttling now holds while a High app stays
  above min(16 KB/s, release level). Also: list order no longer freezes on an idle caret in a
  limit box (5 s after the last key), filter changes always resync, frozen rows show "Idle"
  instead of "Not running", throttle on/off is logged. 55 tests. Live (Low = 8 parallel
  downloads, High = 20 MB, threshold 5000): High 465 KB/s before -> 3,830 KB/s after.
  Open: the user's Explorer-launched instance (15:05) wrote nothing to the log; instances
  started by the test script log normally.

- 2026-11-21 — **Smart sharing v2 (user design):** replaced High/Normal/Low + fixed 50 KB/s
  throttle with capacity-based sharing:
  - `LineCapacity` learns line speed (fastest sustained 1 s of internet bytes; manual override;
    persisted). LAN traffic, DNS and port-less packets (ICMP) are never shaped or counted.
  - Priority numbers 1..128 (`Rank`; none = left alone). `BandwidthAllocator`: strict priority,
    lower numbers capped to `capacity × 92 % − use above − 12 % headroom`, max-min fair within a
    number, floor 50 KB/s. v1 settings migrate (High → 10, Low → 100).
  - Game mode: games first, fixed reserve (100 KB/s) + 7 % never filled, all else capped while a
    game runs; `PingMonitor` (1 s pings, baseline = min of last 60) + `PingGuard` (×0.85 on
    +30 ms, +0.02 when calm, floor 0.4).
  - Movie mode: pre-reserved space while a video target was active in the last 20 s (auto =
    1.5 × 10 s average, 1 MB/s..60 % of line, or fixed).
  - Per-site traffic: `SiteClassifier` (flow table; DNS answers, TLS ClientHello SNI with TCP
    reassembly, QUIC v1/v2 Initial decryption with CRYPTO reassembly) routes tracked sites to
    their own `ShapingGroup` + synthetic counters (negative ids). Built-in video platforms.
  - Sampling 500 → 250 ms. UI: 6 cards (traffic, ping, engine, sharing, game, movie), priority
    column, tabbed settings (drag-and-drop priority order, games, platforms, websites, line).
  - 89 tests, incl. SNI from real SChannel ClientHellos and from a real msquic QUIC Initial.
