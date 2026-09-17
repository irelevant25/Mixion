# Backend — .NET 8 Host (audio engine + HTTP/WS server)

Single-process Windows .exe. Runs the audio engine, an ASP.NET Core (Kestrel) HTTP server bound to `127.0.0.1`, and a WebSocket endpoint on the same port. Serves the Angular SPA from an embedded `wwwroot` so the .exe is the entire app.

See the [root README](../README.md) for the overall architecture.

---

## Project layout

```
BE/
├── Mixion.Host/
│   ├── Mixion.Host.csproj
│   ├── Program.cs                    # entry: driver check → build WebApplication → start audio
│   ├── wwwroot/                      # populated by build.ps1 from FE/angular/dist; embedded into assembly
│   ├── Web/
│   │   ├── HttpServer.cs             # Kestrel host configuration (listen on 127.0.0.1:<free port>)
│   │   ├── StaticFiles.cs            # ManifestEmbeddedFileProvider wiring + SPA fallback to /index.html
│   │   ├── SessionEndpoint.cs        # GET /api/session — returns {token, mixerStateInit, currentPreset, slots}
│   │   ├── SessionStore.cs           # /ws tokens: random, single-use, expire after 60 s
│   │   ├── LoopbackRequestGuard.cs   # 403 unless Host and Origin are loopback (foreign pages, DNS rebinding)
│   │   ├── HealthEndpoint.cs         # GET /api/health — diagnostics
│   │   ├── PortPreference.cs         # remembers the last listen port so the UI keeps its origin across restarts
│   │   └── WebSocketEndpoint.cs      # GET /ws — upgrade, validate token, dispatch; hostShutdown + close 1001 on exit
│   ├── Audio/
│   │   ├── DeviceEnumerator.cs       # WASAPI endpoint discovery
│   │   ├── DriverProbe.cs            # detects basic single-cable VB-CABLE among WASAPI endpoints
│   │   ├── IAudioCaptureSource.cs    # common shape every input source feeds the engine through
│   │   ├── CaptureDevice.cs          # WasapiCapture wrapper, format-converts to internal float stereo
│   │   ├── WaveFormatX.cs            # native sample formats → interleaved stereo float
│   │   ├── ProcessLoopbackCapture.cs # per-process loopback (Win10 20348+/Win11) on an app's root process tree
│   │   ├── ProcessChannelId.cs       # `process:<name>` channel-id scheme
│   │   ├── ProcessSnapshot.cs        # Toolhelp32 process table: names, parents, app root processes
│   │   ├── AudioProcessEnumerator.cs # raw-COM walk of IAudioSessionManager2 → apps producing audio
│   │   ├── EngineFactory.cs          # builds a MixEngine from current devices + apps; opens single sources for in-place attach
│   │   ├── AudioDeviceWatcher.cs     # background service: device notifications + 1 s app poll → reconcile
│   │   ├── TopologyPlanner.cs        # pure rules: which slots to re-bind, detach or add
│   │   ├── TopologyReconciler.cs     # applies a plan to the running engine without a rebuild
│   │   ├── ProcessSuppressions.cs    # apps the user detached this session
│   │   ├── RenderDevice.cs           # WasapiOut wrapper, reads from output ring buffer
│   │   ├── MixEngine.cs              # the heart: read state, mix, write — slots with spare capacity, hot swap / append
│   │   ├── RoutingMatrix.cs          # bool[inputs, outputs] + solo/mute logic
│   │   ├── MeterAggregator.cs        # peak + RMS over ~33 ms window
│   │   ├── RingBuffer.cs             # SPSC lock-free float ring buffer
│   │   └── Dsp/
│   │       ├── BiquadFilter.cs       # RBJ cookbook biquads (peaking, shelves, pass, notch)
│   │       ├── Equalizer.cs          # parametric EQ chain — N biquads in series
│   │       ├── Compressor.cs         # feed-forward peak compressor with soft knee + makeup
│   │       ├── NoiseGate.cs          # gate with open/close hysteresis
│   │       └── Pan.cs                # constant-power pan law
│   ├── Ipc/
│   │   ├── JsonRpc.cs                # 2.0 framing, dispatcher, error codes
│   │   ├── TelemetryFrame.cs         # pack/unpack binary meter frames
│   │   └── Handlers/
│   │       ├── DeviceHandlers.cs
│   │       ├── ProcessHandlers.cs    # listAudioProcesses + removeProcessLoopback (BE-105)
│   │       ├── StateHandlers.cs
│   │       ├── RoutingHandlers.cs
│   │       ├── ChannelHandlers.cs
│   │       ├── TelemetryHandlers.cs
│   │       ├── PresetHandlers.cs
│   │       ├── DspHandlers.cs
│   │       └── SystemHandlers.cs
│   ├── State/
│   │   ├── MixerState.cs             # single source of truth, mutated by control thread
│   │   ├── Preset.cs                 # versioned DTO
│   │   └── PresetStore.cs            # JSON I/O in %LOCALAPPDATA%
│   ├── Interop/
│   │   ├── Mmcss.cs                  # P/Invoke for AvSetMmThreadCharacteristics
│   │   └── MessageBox.cs             # P/Invoke for user32!MessageBoxW (driver-missing dialog)
│   └── Shell/
│       └── BrowserLauncher.cs        # Process.Start("...") with UseShellExecute=true
└── Mixion.Host.Tests/         # xUnit
    ├── MixEngineTests.cs
    ├── RoutingMatrixTests.cs
    ├── SoloLogicTests.cs
    ├── DriverProbeTests.cs           # uses a fake endpoint enumerator
    ├── JsonRpcTests.cs
    ├── DspTests.cs                   # biquad / EQ / compressor / gate / pan unit tests
    └── AllocationsTests.cs           # BenchmarkDotNet memory diagnoser smoke
```

---

## Dependencies

- **.NET 8 SDK** (target: `net8.0-windows` with `<UseWindowsForms>true</UseWindowsForms>`, `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`)
- **`Microsoft.AspNetCore.App`** framework reference — Kestrel, static files, WebSocket middleware, minimal API
- **`Microsoft.WindowsDesktop.App`** framework reference — pulled in by `UseWindowsForms` for the system-tray UI (`NotifyIcon`, `Microsoft.Win32.Registry` for the Run-on-startup entry)
- **`Microsoft.Extensions.FileProviders.Embedded`** — serves embedded Angular `wwwroot` from the assembly
- **NAudio** (`NAudio.Wasapi`) — WASAPI capture/render
- **`System.Text.Json`** (built-in) — preset & RPC serialization (use source-gen for AOT-friendliness)
- Tests: `xunit`, `xunit.runner.visualstudio`, `BenchmarkDotNet`

`csproj` excerpt:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <GenerateEmbeddedFilesManifest>true</GenerateEmbeddedFilesManifest>
    <AssemblyName>Mixion</AssemblyName>
    <!-- WinExe → no console flashes on double-click; the tray "Console"
         menu item allocates / shows a hidden console on demand. -->
    <OutputType>WinExe</OutputType>
    <ApplicationIcon>wwwroot\favicon.ico</ApplicationIcon>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="NAudio.Wasapi" Version="2.*" />
    <PackageReference Include="Microsoft.Extensions.FileProviders.Embedded" Version="8.*" />
  </ItemGroup>
  <ItemGroup>
    <!-- Angular dist gets copied here by build.ps1; everything inside is embedded -->
    <EmbeddedResource Include="wwwroot\**\*" />
  </ItemGroup>
</Project>
```

---

## Run / debug

```powershell
cd BE/Mixion.Host
dotnet run --launch-profile "Mixion.Host (no-driver-check, fixed port)"
# 1. Probes WASAPI for VB-CABLE; if missing, shows MessageBox and exits.
# 2. Binds Kestrel to 127.0.0.1:<random free port>.
# 3. Starts audio engine.
# 4. Logs the URL (e.g., http://127.0.0.1:54812/) and opens the default browser unless --no-browser.
# 5. Logs go to %LOCALAPPDATA%\Mixion\logs\host-*.log.
```

### Hot reload

```powershell
cd BE/Mixion.Host
dotnet watch run -- --port 54812 --no-browser
# Rebuilds + restarts on .cs / .csproj changes. Anything after `--` is
# forwarded to the host as CLI args. `--no-browser` is recommended so a fresh
# tab doesn't pop on every reload — point your browser at http://127.0.0.1:54812
# once and refresh on demand. Run the FE separately (`npm start` in
# FE/angular) for a full edit-save loop on both sides.
```

### CLI flags

| Flag | Behavior |
|---|---|
| *(none)* | Normal run |
| `--port <N>` | Bind to a specific port. Without it the host reuses its previous run's port (`%LOCALAPPDATA%\Mixion\host-port.txt`) when free, otherwise a random free one. |
| `--no-browser` | Don't auto-open the default browser. |
| `--no-driver-check` | Skip the VB-CABLE probe (development convenience; not exposed in packaged builds). |

---

## Engine topology coordination

Every change to which devices and apps are bound goes through [`EngineHost`](Mixion.Host/Ipc/EngineHost.cs), serialised by one semaphore so two changes never race for the same WASAPI endpoints or slot indices:

- **In place** — `ChangeTopologyAsync`, used by the device watcher and preset loads. [`MixEngine`](Mixion.Host/Audio/MixEngine.cs) is allocated with spare slots (`SpareInputSlots` / `SpareOutputSlots`), so a source can be swapped behind an existing channel (`ReplaceCapture` / `ReplaceRender`) or a new channel attached (`TryAppendCapture` / `TryAppendRender`) while audio flows. Nothing else is re-opened and there is no gap.
- **Full rebuild** — `RebuildAsync` snapshots state, optionally transforms it (e.g. drop a removed channel), disposes the running engine and builds a new one via `EngineFactory.Build(seed)`. Used for the startup build, Rescan (`refreshDevices`), audio-settings changes, `removeProcessLoopback`, and as a fallback when the spare slots run out. Expect a ~200 ms audible gap. Shutdown goes through the same lock (`ShutdownAsync`), so a rebuild still in flight can't leave an engine running.

`EngineHost.TopologyChanged` fires after either path; `Program` forwards it to every UI as a `stateChanged` notification. Channel settings (gain, mute, routing, DSP) change through `MixEngine.UpdateState(s => …)`, which serialises writers so RPC handlers and the device watcher can't overwrite each other.

## Per-process loopback management (BE-105)

| RPC | Purpose |
|---|---|
| `listAudioProcesses` | Read-only enumeration of apps with an audio session. Returns `{processes: [{channelId, processId, processName, executablePath}]}` — one entry per app name, `processId` being the app's root process. `channelId` matches the `process:<name>` format so the FE can dedupe against `MixerState.Inputs`. |
| `removeProcessLoopback` | Drop a single app channel from `MixerState` and rebuild the engine without it. The app is added to `ProcessSuppressions`, so the device watcher doesn't attach it again for the rest of the session (a Rescan, or loading a preset that uses the app, lifts that). Idempotent. Returns the new `MixerStateDto`. |

There is no `addProcessLoopback`: the device watcher attaches every app that opens an audio session on its own.

## Automatic device & app tracking (BE-106 → BE-111)

[`AudioDeviceWatcher`](Mixion.Host/Audio/AudioDeviceWatcher.cs) is a background service that keeps the engine bound to whatever Windows offers, without a Refresh:

- **Every second** it takes a Toolhelp32 process snapshot ([`ProcessSnapshot`](Mixion.Host/Audio/ProcessSnapshot.cs)) and the list of apps with audio sessions ([`AudioProcessEnumerator`](Mixion.Host/Audio/AudioProcessEnumerator.cs) — raw COM, because NAudio's session manager registers a callback per construction and can't be polled).
- **After device notifications** (`IMMNotificationClient`: added, removed, state or format changed; 400 ms settle) and **every 15 s** as a safety net it also scans the active endpoints.

[`TopologyPlanner`](Mixion.Host/Audio/TopologyPlanner.cs) turns the snapshot into a plan (pure, unit-tested) and [`TopologyReconciler`](Mixion.Host/Audio/TopologyReconciler.cs) applies it in place:

- **Apps** are one channel per executable name. The loopback targets the app's *root* process (the top of its same-name process tree) in include-tree mode, so multi-process apps such as Chrome — whose audio comes from a helper process Chrome recycles — are captured whole. When the root exits the channel is detached (red strip, settings kept); as soon as the app runs again — preferring an instance with an audio session — it is re-attached, typically within a second. A healthy binding only moves when its instance never played audio while another instance does. Trees that contain the Mixion host (e.g. Explorer when Mixion was started from it) are never captured, since that would feed Mixion's output back into itself.
- **Devices** are re-attached when their endpoint id is active again at the engine sample rate, re-opened when their stream faulted (`IsFaulted`: device invalidated, format changed), detached when they disappear, and appended when new.
- Targets that fail to open back off (10 s, doubling up to 10 min; a device notification resets devices) and only the first failure logs a warning.
- The mix loop mixes a capture that delivered nothing for ~50 ms (`StarvedSourceTimeoutMs`; longer for sources that normally deliver in bigger bursts, such as some Bluetooth devices) as silence and drops what the other channels buffered while it waited, so a dead source costs a brief dropout instead of stalling the other channels or adding latency while the watcher catches up.

Passes never crash the host — a failing pass logs a warning and the next one retries. If a rebuild fails and leaves no engine, the watcher rebuilds it from the last known state: right after a device change, otherwise backing off from 10 s to 10 min.

---

## Manual rescan (last resort)

**RPC**: `refreshDevices` (no params) — the "Rescan" link at the bottom of the slot picker. The handler in [`Ipc/Handlers/DeviceHandlers.cs`](Mixion.Host/Ipc/Handlers/DeviceHandlers.cs) clears `ProcessSuppressions` and rebuilds the engine through `EngineHost.RebuildAsync` / [`EngineFactory.Build(previousState)`](Mixion.Host/Audio/EngineFactory.cs), with a brief audio gap (~100–300 ms) while WASAPI re-opens. Returns the new `MixerStateDto`. With the device watcher running it should rarely be needed.

**Channel-identity discipline** (shared by rebuilds and the watcher):

- Physical devices use the WASAPI `MMDevice.ID` (a stable GUID-ish string).
- Apps use `process:<name>` ([`ProcessChannelId`](Mixion.Host/Audio/ProcessChannelId.cs)), matched case-insensitively — the channel survives the app restarting under a new PID.
- A channel id no longer present stays in state with `Available = false` and a `null` backing source. The mix engine treats null capture/render slots as silent (input) or discard (output); the FE renders the strip in red so the user knows audio isn't flowing without losing their gain/mute/EQ/route settings.
- New devices and apps are appended after the existing channels. The routing matrix grows with `false` in the new positions, leaving existing routes untouched.

---

## Per-process loopback capture

Beyond physical mics and virtual cables, the engine can pull audio **directly from a single Windows process** — no virtual cable in the chain at all. This is the Win10 build 20348+ / Win11 process-loopback API (`ActivateAudioInterfaceAsync` with `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`), wrapped in [`Audio/ProcessLoopbackCapture.cs`](Mixion.Host/Audio/ProcessLoopbackCapture.cs). NAudio doesn't expose this variant, so the class drives the COM activation flow directly: native `IAudioClient` / `IAudioCaptureClient` interop, an `IActivateAudioInterfaceCompletionHandler` CCW, and a dedicated capture thread that drains packets into the same `RingBuffer` the rest of the engine uses.

**Why it matters**: the user can route Chrome → headphones + a "fake mic" virtual cable using only **one** physical cable pair, instead of needing a second cable just to get Chrome's audio out of the OS. Voicemeeter Banana / Potato can't do this — it requires every source to enter via a virtual cable.

**Lifecycle**:

1. [`AudioProcessEnumerator`](Mixion.Host/Audio/AudioProcessEnumerator.cs) walks every active render endpoint's `IAudioSessionManager2`, drops the system-sounds session and anything in the host's own process tree, and resolves each session's process to its name and app root via a [`ProcessSnapshot`](Mixion.Host/Audio/ProcessSnapshot.cs).
2. [`EngineFactory`](Mixion.Host/Audio/EngineFactory.cs) (startup, rebuilds) or the device watcher (at runtime) opens a `ProcessLoopbackCapture` on the root process, with a friendly name like `"chrome (app)"` and channel id `process:chrome`.
3. Loopbacks are first-class `IAudioCaptureSource` instances — they appear in `MixerState.Inputs` next to physical captures and the FE wires them through the existing slot-config dialog, no extra UI required.

**Limitations**:

- **One channel per executable name.** Different apps that share an executable name (for example several apps hosting `msedgewebview2`) share one channel, bound to one of their process trees.
- **DRM-protected streams refuse loopback.** Some Spotify Premium / Netflix configurations prevent any process from capturing their output — the same hard limit a virtual cable would hit.

---

## Threading model (the rule)

| Thread | Owner | Mutate `MixerState`? | Allocate? |
|---|---|---|---|
| WASAPI capture (one per input) | NAudio / capture classes | ❌ | ❌ |
| Mix thread (single) | `MixEngine` | ❌ read-only via `Volatile.Read` | ❌ |
| WASAPI render (one per output) | NAudio / render classes | ❌ | ❌ |
| Telemetry (single) | `TelemetryBroadcaster` | ❌ read-only | only the binary frame buffer (reused) |
| Kestrel request thread | ASP.NET Core | ✅ via `MixEngine.UpdateState` | ✅ off the audio path |
| Device watcher (thread pool) | `AudioDeviceWatcher` | ✅ via `EngineHost.ChangeTopologyAsync` + `UpdateState` | ✅ off the audio path |

Locks on the audio path are forbidden. `UpdateState` serialises writers with a control-plane lock and swaps the entire `MixerState` reference; the mix thread never takes that lock. Every capture and render ring carries interleaved stereo floats and is strictly single-producer / single-consumer.

---

## HTTP surface

The host exposes a tiny HTTP API plus the Angular SPA:

| Method | Path | Purpose |
|---|---|---|
| GET | `/` | `index.html` from embedded `wwwroot` |
| GET | `/*.js`, `/*.css`, `/assets/*` | Angular static files (embedded) |
| GET | `/{anything else}` | SPA fallback → `index.html` (Angular handles routing) |
| GET | `/api/session` | Returns `{ token, mixerStateInit }`. Token is required on `/ws`. |
| GET | `/api/health` | `{ ok: true, version, uptimeSeconds }` |
| GET | `/ws` | WebSocket upgrade. Requires `?token=<from /api/session>`; each token works once, within 60 s. |

`LoopbackRequestGuard` answers every request with 403 unless its `Host` is loopback (`localhost`, `127.0.0.1`, `[::1]`) and its `Origin`, when present, is a loopback origin. That keeps browser pages out, including cross-origin WebSockets and DNS rebinding. It doesn't keep out local processes, which can call `/api/session` just as the UI does. See *Security model* in the root README.

`/api/session` returns the auth token plus the initial mixer state so the SPA can hydrate without a round-trip:

```jsonc
{
  "token": "<random 32-byte hex>",
  "mixerStateInit": { "channels": [...], "matrix": [[...]], "devices": {...} }
}
```

The token is 32 random bytes held in memory, so a host restart revokes every token. It works once and expires 60 s after issue, so the SPA fetches a fresh one before every connect (first load and each reconnect). The SPA keeps it in memory only, never in `localStorage`.

Kestrel serves requests before the audio engine is up, so `/api/session` and the `getState` RPC wait (at most 15 s) until the engine has started and the last preset is applied — a page that loads early doesn't render an empty mixer. `index.html` is served with `Cache-Control: no-cache` because the host reuses its port across restarts.

---

## Tasks

Tasks are scoped to be picked up one at a time. **Acceptance** says how to know it's done.

### M1 — Walking skeleton

| ID | Task | Files | Acceptance |
|---|---|---|---|
| BE-001 | Init solution + ASP.NET Core web project, add NAudio + EmbeddedFileProvider packages | `Mixion.Host.csproj`, `Program.cs` | `dotnet run` starts a `WebApplication`, prints "host alive", responds 200 to `/api/health` |
| BE-002 | `DeviceEnumerator`: enumerate WASAPI capture & render endpoints, return DTOs with id, friendly name, interface, mix format | `Audio/DeviceEnumerator.cs` | Unit test: returns at least one device on the dev machine |
| BE-003 | `DriverProbe`: scan endpoints, return `{ found, matchedDevice? }`. Friendly-name must **start with** `"CABLE Input"` or `"CABLE Output"` (case-insensitive, with a whitespace or `(` boundary) so only the basic single-cable VB-CABLE qualifies — VB-CABLE A+B (`CABLE-A Input`), C+D, and Voicemeeter VAIOs are explicitly rejected | `Audio/DriverProbe.cs`, `DriverProbeTests.cs` | Unit tests cover: basic render endpoint, basic capture endpoint, A+B/C+D rejected, Voicemeeter VAIOs rejected, basic-alongside-higher-tier accepted, empty/non-VB rejected, case-insensitive |
| BE-004 | `Interop/MessageBox.cs`: P/Invoke `user32.dll!MessageBoxW` with `MB_OK \| MB_ICONERROR` | `Interop/MessageBox.cs` | Calling it shows a real Windows dialog |
| BE-005 | `Program.cs` driver-miss path: if `DriverProbe.Found == false`, show the MessageBox with the install URL message and exit code 2 — **before** `WebApplication.Run()`. Message names the basic VB-CABLE explicitly so users on A+B/C+D/Voicemeeter understand what's missing | `Program.cs` | With basic VB-CABLE uninstalled (even if A+B/C+D is present), app shows the dialog and exits without binding any port |
| BE-006 | Bind Kestrel to `127.0.0.1` on a random free port; log the chosen URL | `Web/HttpServer.cs` | Two consecutive runs land on different ports; `netstat -an` confirms 127.0.0.1-only |
| BE-007 | Static-file middleware backed by `ManifestEmbeddedFileProvider`; SPA fallback to `/index.html` for unknown paths | `Web/StaticFiles.cs` | A placeholder `wwwroot/index.html` is served at `/`; navigating to `/foo/bar` also returns it |
| BE-008 | `GET /api/session` endpoint: returns `{ token: <new random hex>, mixerStateInit: <stub> }` and stores token in an in-memory set keyed by issue time | `Web/SessionEndpoint.cs` | Two consecutive GETs produce two distinct tokens; both are accepted by `/ws` |
| BE-009 | WebSocket endpoint at `/ws`; rejects connections without a valid `?token=` (close code 1008) | `Web/WebSocketEndpoint.cs` | `wscat ws://127.0.0.1:<port>/ws` closes immediately; with token it stays open |
| BE-010 | `BrowserLauncher.OpenAsync(url)` via `Process.Start { UseShellExecute = true }`; gated by `--no-browser` | `Shell/BrowserLauncher.cs` | Default run opens a browser to the chosen URL; `--no-browser` doesn't |
| BE-011 | Capture-to-render bypass smoke: default mic → default output, started after Kestrel is listening | `Program.cs`, `Audio/CaptureDevice.cs`, `Audio/RenderDevice.cs` | Speak into mic, hear yourself in headphones with no obvious lag |

### M2 — Mix engine MVP

| ID | Task | Files | Acceptance |
|---|---|---|---|
| BE-020 | Define `MixerState` (channels, gain, mute, solo, NxM matrix) — immutable record swapped via `Interlocked.Exchange` | `State/MixerState.cs` | Compiles; record-equality test passes |
| BE-021 | SPSC lock-free `RingBuffer<float>` (power-of-two capacity, `Volatile` reads/writes) | `Audio/RingBuffer.cs` | Unit test: 1M write/read pairs across two threads, no data loss, no allocations after init |
| BE-022 | `CaptureDevice`: open `WasapiCapture`, convert format to internal float mono, write to ring buffer | `Audio/CaptureDevice.cs` | 48 kHz int16 stereo mic captures to ring buffer in float mono |
| BE-023 | `RenderDevice`: open `WasapiOut`, drain ring buffer into output stream | `Audio/RenderDevice.cs` | Plays a sine wave fed via ring buffer |
| BE-024 | `MixEngine` thread loop: read all input rings → apply matrix → sum to output rings | `Audio/MixEngine.cs` | Two inputs summed to one output, audible |
| BE-025 | `Mmcss.cs` P/Invoke for `AvSetMmThreadCharacteristics`/`AvRevertMmThreadCharacteristics` | `Interop/Mmcss.cs` | Returns non-null handle for "Pro Audio" |
| BE-026 | Apply MMCSS to capture, render, and mix threads | `MixEngine.cs`, `CaptureDevice.cs`, `RenderDevice.cs` | Process Explorer shows threads at "Pro Audio" priority |
| BE-027 | Pre-allocate all `float[]` at startup; verify zero alloc on hot path | all `Audio/*` | BenchmarkDotNet memory diagnoser shows 0 B allocated per mix tick |
| BE-028 | Detect sample-rate mismatch across selected devices; refuse to start with a clear error | `MixEngine.cs` | Selecting a 44.1 kHz device alongside 48 kHz devices fails fast with named device in error |
| BE-029 | 30-minute manual stability run with audio flowing | — | No glitches, no memory growth in Task Manager |

### M3 — Routing matrix + JSON-RPC

| ID | Task | Files | Acceptance |
|---|---|---|---|
| BE-030 | `JsonRpc.cs`: 2.0 framing (request/response/error), id correlation, registry of handlers | `Ipc/JsonRpc.cs` | Unit test: parses request, dispatches, returns response with same id |
| BE-031 | `WebSocketEndpoint` routes incoming text frames to `JsonRpc`; ignores binary frames inbound | `Web/WebSocketEndpoint.cs` | Sending a request gets a response |
| BE-032 | `setRoute(input, output, enabled)` RPC handler — atomically swap `MixerState` with new matrix | `Ipc/Handlers/RoutingHandlers.cs` | Toggling a route audibly enables/disables that path within one mix tick |
| BE-033 | `listDevices()` RPC — returns capture + render endpoints | `Ipc/Handlers/DeviceHandlers.cs` | Response contains VB-CABLE entries |
| BE-034 | `getState()` RPC — full snapshot of current `MixerState` | `Ipc/Handlers/StateHandlers.cs` | UI can hydrate from a single call (used by `/api/session.mixerStateInit` too) |
| BE-035 | `subscribe()` / `unsubscribe()` RPCs — toggle telemetry per connection | `Ipc/Handlers/TelemetryHandlers.cs` | Telemetry frames stop within 100 ms of unsubscribe |
| BE-036 | Confirm thread-safe state mutation: control writes via `Interlocked.Exchange`; mix reads via `Volatile.Read` | `MixerState.cs`, `MixEngine.cs` | Stress test: 10k RPC mutations during audio playback, no glitches, no torn reads |

### M4 — Gain / mute / solo

| ID | Task | Files | Acceptance |
|---|---|---|---|
| BE-040 | `setGain(channel, db)` RPC; engine converts dB → linear once on state swap | `Ipc/Handlers/ChannelHandlers.cs`, `MixEngine.cs` | Sliding gain audibly changes level |
| BE-041 | `setMute(channel, bool)` RPC | `Ipc/Handlers/ChannelHandlers.cs` | Mute silences channel within one tick |
| BE-042 | `setSolo(channel, bool)` RPC | `Ipc/Handlers/ChannelHandlers.cs` | Solo silences other non-soloed channels in same bus |
| BE-043 | Solo logic: if any channel in a bus is soloed, all non-soloed channels in that bus become effectively muted | `MixEngine.cs` | Unit tests for: no solo, one solo, multiple solos, solo+mute, solo across buses |
| BE-044 | Smooth gain ramping (one-pole) to avoid clicks on rapid changes | `MixEngine.cs` | No audible click when sliding gain quickly |
| BE-045 | Unit test suite for solo edge cases | `MixEngineTests.cs`, `SoloLogicTests.cs` | All cases covered |

### M5 — VU meters telemetry

| ID | Task | Files | Acceptance |
|---|---|---|---|
| BE-050 | `MeterAggregator`: per-channel peak (max abs) + RMS over ~33 ms window | `Audio/MeterAggregator.cs` | Unit test with sine input: RMS ≈ 0.707 × peak |
| BE-051 | `TelemetryFrame.cs`: pack `[u32 frameId][u32 channelCount][float32 peak0][float32 rms0]...` little-endian | `Ipc/TelemetryFrame.cs` | Round-trip pack/unpack test |
| BE-052 | Telemetry thread emits binary frame every ~33 ms to all subscribed sockets | `Web/WebSocketEndpoint.cs` | Wireshark/UI shows ~30 Hz cadence |
| BE-053 | Backpressure: if a socket's send buffer is full, drop the frame for that socket; never block the audio path | `Web/WebSocketEndpoint.cs` | Slow client doesn't stall audio |

### M6 — Presets

| ID | Task | Files | Acceptance |
|---|---|---|---|
| BE-060 | `Preset.cs` DTO with `schemaVersion: 1` field; (de)serialized via `System.Text.Json` source-gen | `State/Preset.cs` | JSON file readable + parseable round-trip |
| BE-061 | `PresetStore`: list, save, load, delete in `%LOCALAPPDATA%\Mixion\presets\*.json` | `State/PresetStore.cs` | RPC-driven save creates a file; load returns its contents |
| BE-062 | Device re-resolution on load: presets store endpoint friendly name + interface name; find best match; warn on missing | `State/PresetStore.cs` | Unplugging a device and loading preset returns a structured warning, not an exception |
| BE-063 | RPCs: `listPresets()`, `savePreset(name)`, `loadPreset(name)`, `deletePreset(name)` | `Ipc/Handlers/PresetHandlers.cs` | UI can drive full preset lifecycle |
| BE-064 | Add `createdAt` to the preset DTO (nullable for legacy v1 files) and preserve it across overwrites; `savedAt` continues to track the last edit | `State/Preset.cs`, `State/PresetStore.cs` | Saving an existing preset bumps `savedAt` but leaves `createdAt` anchored to the original save |
| BE-065 | `listPresets` returns metadata rows `{name, createdAt, editedAt}` instead of raw names so the FE can render and sort the list by edited-at | `Ipc/Handlers/PresetHandlers.cs`, `State/PresetStore.cs` | RPC response carries timestamps for every preset; legacy presets without `createdAt` fall back to `savedAt` |
| BE-066 | `renamePreset(oldName, newName)` RPC — moves the file, rewrites the in-JSON `name`, retargets the last-preset pointer and `CurrentPresetState` if needed; rejects when target exists | `Ipc/Handlers/PresetHandlers.cs`, `State/PresetStore.cs` | Renaming an active preset updates the on-disk file and the FE's "current preset" badge in one call |
| BE-067 | `clearCurrentPreset` RPC — clears `CurrentPresetState` and the persisted last-preset pointer without touching mixer state, so the FE "New" button can detach without a reload | `Ipc/Handlers/PresetHandlers.cs` | Calling it leaves the engine running unchanged but a subsequent host restart no longer auto-loads the previously bound preset |

### M7 — Per-channel processing (gate / EQ / compressor / pan)

A small fixed DSP chain per channel — applied to **both inputs and outputs** (strips/buses). Each stage is independently bypassable. Order on the input side: `gate → EQ → compressor`. Order on the output side: `EQ → compressor → pan`. EQ is a parametric biquad cascade — the UI shows a frequency-response curve with one draggable point per band on a log frequency axis (20 Hz - 20 kHz, x) and dB axis (±18 dB, y).

| ID | Task | Files | Acceptance |
|---|---|---|---|
| BE-070 | Extend `MixerState` channel record with `pan: float`, `gate: GateState?`, `compressor: CompressorState?`, `eq: EqState?` (each nullable; `null` = bypass). Extend `Preset` DTO so DSP state round-trips | `State/MixerState.cs`, `State/Preset.cs` | Existing M2 atomic-swap test still passes; preset save/load round-trips DSP state |
| BE-071 | `BiquadFilter`: RBJ-cookbook coefficients for `peaking`, `lowShelf`, `highShelf`, `lowPass`, `highPass`, `notch`. Sample-rate aware. Provides `Process(float)` and block `Process(Span<float>)` | `Audio/Dsp/BiquadFilter.cs` | Unit test: 1 kHz sine through a +6 dB peaking band at 1 kHz produces ~2× output amplitude |
| BE-072 | `Equalizer`: cascade of N biquads (default 5 bands), recompute coefficients on state swap, smooth via short crossfade to avoid zipper noise on band moves | `Audio/Dsp/Equalizer.cs` | Sliding band gain rapidly produces no clicks; FFT of pink-noise output matches expected response within 1 dB |
| BE-073 | `Compressor`: feed-forward peak detector with separate attack/release time constants, soft knee, makeup gain. Pre-allocated, zero allocation per sample. Exposes a per-channel "gain reduction in dB" readout for telemetry | `Audio/Dsp/Compressor.cs` | Sine above threshold is reduced by `(input − threshold) × (1 − 1/ratio)` after the attack window; GR readout is non-zero |
| BE-074 | `NoiseGate`: open/close thresholds with hysteresis (close ~6 dB below open to prevent chatter), attack / hold / release, `range` (max attenuation when closed) | `Audio/Dsp/NoiseGate.cs` | Signal below close threshold is attenuated by `range`; signal above open threshold passes; chatter test confirms no flap when input hovers near threshold |
| BE-075 | `Pan`: constant-power pan law (-3 dB at center) producing per-output L/R gain pair from `[-1, +1]` position | `Audio/Dsp/Pan.cs` | pan = 0 → L = R = 1/√2; pan = +1 → L = 0, R = 1; pan = −1 → L = 1, R = 0 |
| BE-076 | Wire DSP into `MixEngine`: per-input chain `gate → EQ → compressor`, per-output chain `EQ → compressor → pan`. Each stage individually bypassable via `null` state. No allocations on the hot path | `Audio/MixEngine.cs` | Toggling each stage audibly engages/disengages it; CPU stays flat with full chain on 8 channels |
| BE-077 | RPC handlers — atomically swap `MixerState`: `setPan`, `setGate`, `setCompressor`, `addEqBand`, `setEqBand`, `removeEqBand` | `Ipc/Handlers/DspHandlers.cs` | UI driving these RPCs hears the change within one mix tick |
| BE-078 | Coefficient/gain interpolation: when a state swap changes biquad coefficients or compressor parameters, fade across ~10 ms to avoid clicks | `Audio/Dsp/Equalizer.cs`, `Audio/Dsp/Compressor.cs` | Rapid slider movement produces no audible clicks |
| BE-079 | Unit + allocation tests for the full chain (gate + 5-band EQ + compressor + pan) | `DspTests.cs`, `AllocationsTests.cs` | All DSP unit tests pass; BenchmarkDotNet diagnoser shows 0 B/tick with full chain enabled |

### M9 — Per-process loopback capture (Win10 20348+/Win11)

Per-process WASAPI loopback so the user can route Chrome / Spotify / OBS / a game directly into the mixer without first redirecting Windows audio output to a virtual cable. This is what makes the app meaningfully better than Voicemeeter for the "music + mic into one fake mic, music also in headphones" workflow with only one VB-CABLE pair.

| ID | Task | Files | Acceptance |
|---|---|---|---|
| BE-100 | `IAudioCaptureSource` interface — common shape (Id, FriendlyName, SampleRate, Ring, Start/Stop/Dispose) for every input source. `CaptureDevice` retrofitted to implement it. `MixEngine` ctor + `Captures` property re-typed to the interface | `Audio/IAudioCaptureSource.cs`, `Audio/CaptureDevice.cs`, `Audio/MixEngine.cs` | Build green; existing tests still pass |
| BE-101 | `ProcessLoopbackCapture` — full COM interop wrapper around `ActivateAudioInterfaceAsync` + `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`. Native `IAudioClient` / `IAudioCaptureClient` definitions, `IActivateAudioInterfaceCompletionHandler` CCW, dedicated event-driven capture thread, mono conversion via shared `WaveFormatX.ConvertToMono`, zero-allocation hot path | `Audio/ProcessLoopbackCapture.cs` | Per-process capture for `chrome.exe` delivers audible samples through the engine; ring underrun → silence, no crash |
| BE-102 | `AudioProcessEnumerator` — list every PID currently holding an active session on a render endpoint via `IAudioSessionManager2`. Dedupe by PID; filter system (PID 0) and the host's own PID; resolve process name via `Process.GetProcessById`; sort alphabetically | `Audio/AudioProcessEnumerator.cs` | Returns Chrome / Spotify when they're playing; doesn't return idle background processes |
| BE-103 | `Program.OpenProcessLoopbacks` — at startup, enumerate audio processes and open a `ProcessLoopbackCapture` per process. Append to the engine's capture list. Failures log a warning and skip; engine still starts | `Program.cs` | Host log shows "Opened process loopback for 'chrome' (PID …)" lines for every audio app running; `MixerState.Inputs` contains them |
| BE-104 | Channel id stability across host restarts: encode as `process:<pid>:<name>`. Document PID-rebinding limitation in `BE/README.md` | `Audio/ProcessLoopbackCapture.cs`, `BE/README.md` | Restart the host with the same Chrome process running → same channel id; restart Chrome → new id (documented) |
| BE-107 | `refreshDevices` RPC: re-enumerate devices + audio-producing processes, rebuild the engine via `EngineFactory.Build(previousState)`. Channel ids preserved, missing channels kept with `Available = false`, new ones appended. Routing matrix grows with `false`. Returns updated `MixerStateDto` | `Ipc/Handlers/DeviceHandlers.cs`, `Audio/EngineFactory.cs` | UI Refresh button → VLC started after host launch shows up; unplugged mic stays in state, marked unavailable |
| BE-108 | `Channel.Available` flag (defaults `true`). Engine accepts nullable `IAudioCaptureSource?[]` / `RenderDevice?[]` and treats null slots as silent input / discard output. Sample-rate validation skips null slots | `State/MixerState.cs`, `Audio/MixEngine.cs`, `Ipc/StateDto.cs` | A slot whose device disappeared still has its gain/mute/EQ/route preserved; mix loop runs at the same allocation cost |
| BE-109 | Stable process-loopback ids: switch from `process:<pid>:<name>` to `process:<name>` so a Chrome close/reopen keeps the same channel id. Dedupe at enumeration time when multiple PIDs share a name | `Audio/ProcessLoopbackCapture.cs`, `Audio/EngineFactory.cs` | Slot bound to "chrome" survives a Chrome restart through one Refresh click |
| BE-105 | `listAudioProcesses` + `removeProcessLoopback` RPCs in a new `ProcessHandlers.cs`. Both go through `EngineHost.RebuildAsync` with auto-discover off for remove, so the just-removed channel doesn't re-appear via enumeration. `addProcessLoopback` deliberately omitted — refresh already covers it | `Ipc/Handlers/ProcessHandlers.cs`, `Ipc/EngineHost.cs`, `Audio/EngineFactory.cs` | FE picker shows currently-audio-producing apps; "×" detach button on process channels removes them and the channel is gone after rebuild |
| BE-106 *(superseded by BE-111)* | `ProcessHealthMonitor` IHostedService polls every 3 s; on a dead PID, calls `EngineHost.RebuildAsync` (auto-discover on) so `EngineFactory` re-resolves the channel id `process:<name>` against the new PID. Channel + slot binding survive across the restart | `Audio/ProcessHealthMonitor.cs`, `Program.cs` | Close Chrome, reopen Chrome — within ~3-5 s the channel is back to green, slot binding intact, no Refresh click |
| BE-110 | In-place topology changes: `MixEngine` slots with spare capacity (`ReplaceCapture` / `TryAppendCapture` and render counterparts), `EngineHost.ChangeTopologyAsync`, serialised `MixEngine.UpdateState`. Background attach / detach / re-attach no longer rebuilds the engine | `Audio/MixEngine.cs`, `Ipc/EngineHost.cs` | Chrome closing and reopening or a headset being plugged in causes no gap on other channels; Rescan, settings changes and removing an app still rebuild |
| BE-111 | `AudioDeviceWatcher` + `TopologyPlanner` + `TopologyReconciler` replace the PID watchdog: apps bound by name on their root process tree, devices followed via `IMMNotificationClient`, faulted streams re-opened, failed opens back off, host's own process tree never captured; `stateChanged` pushed to every UI | `Audio/AudioDeviceWatcher.cs`, `Audio/TopologyPlanner.cs`, `Audio/TopologyReconciler.cs`, `Audio/ProcessSnapshot.cs`, `Audio/AudioProcessEnumerator.cs` | Close and reopen Chrome: its strip turns red, then back within ~1–2 s, no Refresh |
| BE-112 | Stereo end to end: captures fold to interleaved stereo rings (`WaveFormatX.ConvertToStereo`), per-side EQ, stereo-linked gate/compressor, balance pan on inputs, per-side input meters | `Audio/WaveFormatX.cs`, capture classes, `Audio/MixEngine.cs`, `Audio/Dsp/*` | Audio panned hard left moves only the left meter and stays on the left output |
| BE-113 | Starved-source guard: a capture silent for ~50 ms (twice its learned delivery rhythm for bursty sources) is mixed as silence instead of stalling every channel; after a stall each ring is trimmed to what the sources' rhythms explain | `Audio/MixEngine.cs` | Killing an app mid-playback costs other channels a brief dropout and no lasting latency |
| BE-114 | Presets persist slot channel ids; a slot whose channel is absent at load gets an unavailable placeholder channel that the watcher attaches later | `State/Preset.cs`, `State/PresetStore.cs`, `Ipc/Handlers/PresetHandlers.cs` | A preset with a Chrome slot loads with Chrome closed, keeps the slot, and re-attaches when Chrome starts |

### M8 — Polish

| ID | Task | Files | Acceptance |
|---|---|---|---|
| BE-090 | Single-instance lock via named mutex (`Local\Mixion`); second instance opens browser to existing URL and exits | `Program.cs` | Running twice opens a second browser tab; only one host process is alive |
| BE-091 | Crash logging to `%LOCALAPPDATA%\Mixion\logs\host-yyyyMMdd.log`; rotate at 10 MB | `Program.cs` | Forced exception writes a log entry with stack trace |
| BE-092 | Graceful shutdown on SIGTERM/console close: stop accepting connections → stop render → stop mix → stop capture | `Program.cs`, `MixEngine.cs` | No glitches in last 100 ms; logs confirm clean stop |
| BE-093 | `Properties/PublishProfiles/win-x64-portable.pubxml` + `win-x64-minimal.pubxml` | `Mixion.Host.csproj` | `dotnet publish -p:PublishProfile=...` produces a single .exe |
| BE-094 | `ping()` RPC for the UI to detect liveness | `Ipc/Handlers/SystemHandlers.cs` | UI gets a response in <50 ms |
| BE-095 | Wire `wwwroot` embedding: `<EmbeddedResource Include="wwwroot\**\*" />` and `<GenerateEmbeddedFilesManifest>true</GenerateEmbeddedFilesManifest>` | `Mixion.Host.csproj` | Published .exe contains Angular files; no `wwwroot/` folder beside the .exe at runtime |
| BE-096 | On shutdown every WebSocket gets a `hostShutdown` notification, then close 1001 `host-shutdown`, so the SPA closes its tab and Kestrel's graceful stop doesn't wait on open sockets | `Web/WebSocketEndpoint.cs` | Tray Exit closes the browser tab; the host exits within a second or two |
| BE-097 | Remember the last listen port (`host-port.txt`) and prefer it on launch; `index.html` served no-cache; `/api/session` + `getState` wait for engine startup | `Web/PortPreference.cs`, `Web/StaticFiles.cs`, `Ipc/EngineHost.cs` | Restarting Mixion reuses the port; a page loaded during startup shows the full mixer |

---

## RPC contract (canonical)

All control over JSON-RPC 2.0 on `/ws`. Telemetry over binary frames on the same socket.

```jsonc
// list devices
→ {"jsonrpc":"2.0","id":1,"method":"listDevices"}
← {"jsonrpc":"2.0","id":1,"result":{"capture":[...],"render":[...]}}

// get full state (also returned in /api/session.mixerStateInit)
→ {"jsonrpc":"2.0","id":2,"method":"getState"}
← {"jsonrpc":"2.0","id":2,"result":{"channels":[...],"matrix":[[...]]}}

// set route
→ {"jsonrpc":"2.0","id":3,"method":"setRoute","params":{"input":0,"output":2,"enabled":true}}

// channel ops
→ {"jsonrpc":"2.0","id":4,"method":"setGain","params":{"channel":1,"db":-6}}
→ {"jsonrpc":"2.0","id":5,"method":"setMute","params":{"channel":1,"muted":true}}
→ {"jsonrpc":"2.0","id":6,"method":"setSolo","params":{"channel":2,"soloed":true}}

// telemetry
→ {"jsonrpc":"2.0","id":7,"method":"subscribe"}
← {"jsonrpc":"2.0","id":7,"result":{"ok":true}}
← <binary frame> <binary frame> ...   // ~30 Hz

// presets
→ {"jsonrpc":"2.0","id":8,"method":"savePreset","params":{"name":"streaming"}}
→ {"jsonrpc":"2.0","id":9,"method":"loadPreset","params":{"name":"streaming"}}
→ {"jsonrpc":"2.0","id":16,"method":"listPresets"}
← {"jsonrpc":"2.0","id":16,"result":{"presets":[
    {"name":"streaming","createdAt":"2024-01-01T12:00:00Z","editedAt":"2024-02-15T09:30:00Z"}
  ]}}
→ {"jsonrpc":"2.0","id":17,"method":"renamePreset","params":{"oldName":"streaming","newName":"podcast"}}
→ {"jsonrpc":"2.0","id":18,"method":"clearCurrentPreset"}    // detach the host's "current preset" pointer (FE "New" button)

// per-channel processing (M7)
→ {"jsonrpc":"2.0","id":10,"method":"setPan","params":{"channel":1,"pan":-0.3}}
→ {"jsonrpc":"2.0","id":11,"method":"setGate","params":{"channel":1,"gate":{"enabled":true,"thresholdDb":-50,"attackMs":1,"holdMs":50,"releaseMs":120,"rangeDb":-40}}}
→ {"jsonrpc":"2.0","id":12,"method":"setCompressor","params":{"channel":1,"compressor":{"enabled":true,"thresholdDb":-18,"ratio":4,"attackMs":10,"releaseMs":100,"kneeDb":6,"makeupDb":3}}}
→ {"jsonrpc":"2.0","id":13,"method":"addEqBand","params":{"channel":1,"band":{"type":"peaking","frequency":1000,"gainDb":0,"q":1.0}}}
← {"jsonrpc":"2.0","id":13,"result":{"bandId":"<assigned id>"}}
→ {"jsonrpc":"2.0","id":14,"method":"setEqBand","params":{"channel":1,"bandId":"<id>","band":{"type":"peaking","frequency":2500,"gainDb":3.5,"q":1.4}}}
→ {"jsonrpc":"2.0","id":15,"method":"removeEqBand","params":{"channel":1,"bandId":"<id>"}}
```

Binary telemetry frame layout (little-endian), see [`TelemetryFrame`](Mixion.Host/Ipc/TelemetryFrame.cs):

```
[u8 'M'][u8 pad ×3][u32 frameId][u32 sideCount][float32 peak0][float32 rms0][float32 peak1][float32 rms1]...
```

One (peak, RMS) pair per meter side, in the order `in0 L, in0 R, in1 L, …, out0 L, out0 R, …`. Spectrum frames start with `'S'` instead.

Server → client notifications (JSON-RPC, no `id`):

```jsonc
// channel set or availability changed (app restarted, device plugged in) or the engine was rebuilt
← {"jsonrpc":"2.0","method":"stateChanged","params":{"inputs":[...],"outputs":[...],"matrix":[[...]]}}

// the host applied the last preset after pages loaded (the audio engine started late) — re-read /api/session
← {"jsonrpc":"2.0","method":"sessionChanged"}

// the host is exiting; the socket is closed right after with status 1001, reason "host-shutdown"
← {"jsonrpc":"2.0","method":"hostShutdown"}
```

---

## Definition of done (backend, v1)

- All M1–M8 tasks ✅
- 1-hour stability test: zero glitches, flat memory after warm-up
- BenchmarkDotNet memory diagnoser shows 0 B/tick on the mix path
- All unit tests pass; solo logic covered
- `build.ps1 -Mode portable` produces one `Mixion.exe` < 80 MB containing the entire app
