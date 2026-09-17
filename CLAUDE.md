# Mixion — project knowledge for Claude

Windows-only audio mixer/router. One .NET 8 process runs a WASAPI mix engine, an HTTP + WebSocket server bound to `127.0.0.1`, and a system-tray icon; it serves an Angular SPA the user opens in a browser. Requires the basic single-cable VB-CABLE at runtime.

Code is the source of truth. `README.md`, `BE/README.md` and `FE/README.md` are the original plans/task tables and are updated with larger changes, but can lag. **Keep this file, `.claude/skills/` and `.claude/agents/` current when you change what they describe.**

## Layout

| Path | What lives there |
|---|---|
| `BE/Mixion.Host/Program.cs` | Entry: driver probe → DI → Kestrel start → engine build → last-preset auto-load → tray loop → graceful shutdown |
| `BE/Mixion.Host/Audio/` | `MixEngine`, device classes, `Dsp/`, device/app tracking (`AudioDeviceWatcher`, `TopologyReconciler`, `TopologyPlanner`, `ProcessSnapshot`, `AudioProcessEnumerator`, `ProcessChannelId`) |
| `BE/Mixion.Host/Ipc/` | `EngineHost` (current engine + topology lock), JSON-RPC dispatcher, `Handlers/`, DTOs, binary frame packers |
| `BE/Mixion.Host/Web/` | Kestrel binding + `PortPreference`, session tokens, `/ws` endpoint, telemetry/spectrum broadcasters, embedded SPA |
| `BE/Mixion.Host/State/` | Immutable `MixerState`, presets, audio settings |
| `BE/Mixion.Host/Shell/` | Tray, console window, single-instance gate, browser launcher |
| `BE/Mixion.Host.Tests/` | xUnit |
| `FE/angular/src/app/core/` | `IpcService`, `MixerStateStore`, `SlotsStore`, `SessionService`, `HostLifecycleService`, preset services |
| `FE/angular/src/app/features/` | Channel strips + slot picker, processing (EQ/comp/gate/pan), presets, signal flow, audio settings |
| `FE/angular/src/app/shell/` | Connection banner, error page, retired-tab screen |
| `build.ps1` | Production build → `output/Mixion.exe` (git-ignored). Version: `-Version`, else from git tags (`1.2.0` exactly at a clean tag, `1.2.0-3-gabc1234[-dirty]` after it); `AppVersion` reports it to `/api/health`, `ping`, the log and the tray |
| `.github/workflows/release.yml` | Tag `v*` → build, test, GitHub release with `Mixion.exe` and notes from `CHANGELOG.md` |
| `CHANGELOG.md` | User-facing changes per version; `## [Unreleased]` collects what's coming |

Runtime data: `%LOCALAPPDATA%\Mixion\` — `presets\`, `audio-settings.json`, `host-port.txt`, `logs\`.

## Commands

```powershell
dotnet build Mixion.sln
dotnet test BE/Mixion.Host.Tests/Mixion.Host.Tests.csproj
cd FE/angular; npx ng build --configuration development      # compile + template type-check
cd FE/angular; npx ng test --watch=false --browsers=ChromeHeadless
.\build.ps1                                                   # portable single-file exe → output/
```

Dev loop: run the host with the `Mixion.Host (no-driver-check, fixed port)` launch profile (`--no-driver-check --port 54812 --no-browser`), then `npm start` in `FE/angular` (proxies `/api` + `/ws` to 54812) and open http://localhost:4200.

Skills and agents for this repo:
- **`mixion-verify`** skill — verify a change end to end, including a live run of the real host. Use before reporting work as done.
- **`mixion-engine-reviewer`** agent — after touching the audio path, device/app tracking or state mutation.
- **`mixion-fe-reviewer`** agent — after touching `FE/angular/src`.
- **`mixion-add-rpc`** skill — adding or changing an RPC or a server push.
- **`mixion-diagnose`** skill — something doesn't work at runtime: no sound, a device or app missing, dropouts, the UI not connecting, a crash.
- **`mixion-release`** skill — cutting a GitHub release, the changelog, a failed release run.

## Releases

Pushing a `vX.Y.Z` tag runs `.github/workflows/release.yml`: `build.ps1 -Version`, backend tests except `Requires=AudioDevices`, frontend tests, then a GitHub release with `Mixion.exe` and the version's `CHANGELOG.md` section as notes. Record user-visible changes under `## [Unreleased]` in `CHANGELOG.md` as they land. Details: `mixion-release`.

## Invariants

### Audio thread (`MixEngine.Tick`)
- One mix thread. No allocations, locks, logging or LINQ on the hot path; pre-allocate in constructors.
- Every capture and render ring carries **interleaved stereo floats** (`L,R,L,R…`, 2 per frame). Write and read whole frames only — an odd count swaps the channels for the rest of the session.
- Captures convert native samples with `WaveFormatX.ConvertToStereo` (mono → both sides; channels beyond the first pair fold into both sides).
- Input chain: gate → EQ (one instance per side) → compressor → balance. Output chain: EQ → compressor → balance. Gate and compressor are stereo-linked (`ProcessStereo`).
- The tick waits for a live capture that's short of a block only up to `StarvedSourceTimeoutMs` (50 ms) — or twice that source's learned delivery rhythm, capped by `MaxDeliveryGapMs` — then mixes it as silence, so it can't stall the other channels; freshly attached sources start out "starved". `MixEngine.OnDelivery` learns the rhythm: a source's first delivery seeds it from the audio it carries; pauses, deliveries the mix had stopped waiting for, and deliveries carrying under half their gap in audio aren't learned. A hold-back of `StallTrimThresholdMs` or more trims each capture ring to what a rhythm explains (the late source's rhythm from before it caught up, or the ring's own; nearest block, at least one), so the wait can't become permanent latency while a source that merely delivers in bursts keeps its audio. The `Tick_*` tests in `MixEngineTopologyTests` pin these cases. Capture rings are sized by time (`EngineFactory.CaptureRingFrames`) to outlast the longest wait.
- MMCSS (`Mmcss.Begin`) belongs on the thread doing the device I/O — capture/render loops, NAudio callbacks — never in `Start()`, which the device watcher calls from thread-pool threads.
- Meters: two sides per channel, wire order `[in0 L, in0 R, …, out0 L, out0 R, …]` (`MixEngine.SnapshotMeters`).

### State
- `MixerState` is immutable and index-aligned with the engine's slots. Change it only via `engine.UpdateState(s => …)`; it serialises writers so RPC handlers and the device watcher can't overwrite each other. The mix thread only `Volatile.Read`s it.
- Change gain with `Channel.WithGainDb`, never `with { GainDb = … }` (the cached `GainLinear` would go stale).
- `UpdateState` rejects a state with more channels than live slots — attach slots first.

### Topology — which devices and apps are bound
- Every topology change goes through `EngineHost`: `RebuildAsync` (full rebuild, ~200 ms audio gap: startup, Rescan, audio-settings change, removing an app) or `ChangeTopologyAsync` (in place, no gap). One semaphore serialises both and `ShutdownAsync`; nothing else installs or disposes an engine. A rebuild that throws leaves no engine, and the watcher retries it from the last known state (`TryRecoverAsync`). `EngineHost.TopologyChanged` pushes `stateChanged` to every UI.
- `MixEngine` has spare slots. `ReplaceCapture` / `ReplaceRender` swap a slot's source and return the old one (caller disposes); `TryAppendCapture` / `TryAppendRender` add a slot and return -1 when full (→ rebuild).
- `AudioDeviceWatcher` runs `TopologyReconciler` every 1 s (apps), after `IMMNotificationClient` events (400 ms settle) and every 15 s (device safety scan). Decisions live in `TopologyPlanner`, which is pure — extend `TopologyPlannerTests` when changing a rule.
- Channel ids: physical = `MMDevice.ID`; app = `process:<exe name>` (`ProcessChannelId`), matched case-insensitively. Apps are bound **by name** with an include-process-tree loopback on the app's **root process** (`ProcessSnapshot.ResolveAppRoot`), so helper processes and restarts don't matter. A tree that contains the host itself is never captured (it would feed Mixion's output back): when Mixion was started from the app (e.g. opened from Chrome's downloads), the loopback targets the top-most process below the root whose tree doesn't contain the host — normally the one playing audio (`ProcessSnapshot.ResolveCaptureTarget`) — and an app playing from the process that started Mixion is left out.
- A missing device/app keeps its channel with `Available = false` and a null slot, so slot bindings and settings survive until the watcher re-attaches it.
- `removeProcessLoopback` adds the app to `ProcessSuppressions` (not re-attached this session); `refreshDevices` clears the list.
- Devices report a dead stream via `IsFaulted`; the watcher re-opens or detaches them.

### Presets
- Slots persist the channel id (`PresetSlot.DeviceId`) plus friendly/interface names. On load a slot whose channel isn't present gets an unavailable placeholder (`PresetStore.Apply`); `PresetHandlers.ApplyPresetAsync` attaches the slots before publishing the state. Apps that aren't running are never reported as missing devices.

### COM / NAudio
- WASAPI COM runs on MTA threads (`RunOnMta` helpers); the entry thread is STA for WinForms.
- Don't poll NAudio's `AudioSessionManager` — it registers a callback per construction. `AudioProcessEnumerator` uses raw COM for that reason.
- `CaptureDevice`, `RenderDevice`, `ExclusiveRenderDevice` own their `MMDevice`; the `LowLatency*` devices don't.

### Web and UI lifecycle
- `/ws` carries JSON-RPC 2.0 text (requests, plus server notifications `stateChanged`, `sessionChanged` and `hostShutdown`) and binary frames (`'M'` meters, `'S'` spectrum). `sessionChanged` means the host applied the last preset after pages loaded (its engine started late); the FE re-reads `/api/session` as on reconnect.
- On host shutdown every socket gets `hostShutdown`, then close 1001 `host-shutdown` — this also keeps Kestrel's graceful shutdown from waiting on open sockets. `HostLifecycleService` closes the tab in production builds; under `ng serve` (`isDevMode()`) the tab keeps reconnecting because the host restarts constantly.
- Browsers only let a script close a tab with a single history entry, so nav links use `replaceUrl`. A new Mixion tab retires older ones on the same origin (BroadcastChannel `mixion-ui`).
- Access: `LoopbackRequestGuard` (the first middleware) answers 403 unless `Host` is loopback and `Origin`, when present, is a loopback origin. It is what stops cross-origin `/ws` upgrades and DNS rebinding. `/ws` tokens (`SessionStore`) are random, single-use and expire after 60 s, so the FE fetches `/api/session` before every connect. Nothing here keeps out local processes; don't document it as if it did.
- The host prefers its previous port (`host-port.txt`, skipped with `--port`) and serves `index.html` with `Cache-Control: no-cache`.
- Kestrel is up before the engine: `/api/session` and `getState` wait for `EngineHost.CompleteStartup()` (called after the last-preset auto-load, at most 15 s), so an early page never hydrates an empty mixer.
- FE applies `stateChanged` with `MixerStateStore.applyTopology`: the host owns the channel list, names and availability; the tab keeps gain/mute/solo/pan/DSP/routes of channels it already knows (a push can race the tab's own RPCs).
- Slots exist only in the UI (`SlotsStore`); the host knows channels and routes. Components change slots through `SlotActionsService`, which first switches off a channel's routes when it leaves its last slot (removed or given another device) — otherwise audio keeps flowing through a channel nobody can see or unroute.

## Conventions
- C#: file-scoped namespaces, aligned assignments, XML doc comments that explain *why*. `BE-0xx` / `FE-0xx` ids refer to the task tables in the READMEs.
- Angular 19: standalone components, `OnPush`, signals for state (RxJS only at the IPC boundary), separate `.ts` / `.html` / `.css` per component, canvas for anything high-frequency, no browser storage for state or tokens.
- Tests: pure logic gets unit tests (`TopologyPlannerTests`, `PresetSlotPersistenceTests`); engine behaviour runs against fake sources/renders (`MixEngineTopologyTests` — pacing logic on a synthetic clock via `MixEngine.Tick(now)` on an engine that was never started); `ControlSocketTests` use real Kestrel; `AudioProcessEnumeratorTests` compares raw COM against NAudio on the machine. A test that needs real audio devices carries `[Trait("Requires", "AudioDevices")]` — the release workflow's runners have none and skip it.
- Don't commit unless asked. `output/`, `*.exe`, `bin/`, `obj/`, `wwwroot/` are generated.
