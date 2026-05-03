# Backend — .NET 8 Host (audio engine + HTTP/WS server)

Single-process Windows .exe. Runs the audio engine, an ASP.NET Core (Kestrel) HTTP server bound to `127.0.0.1`, and a WebSocket endpoint on the same port. Serves the Angular SPA from an embedded `wwwroot` so the .exe is the entire app.

See the [root README](../README.md) for the overall architecture.

---

## Project layout

```
BE/
├── VoicemeterAlt.Host/
│   ├── VoicemeterAlt.Host.csproj
│   ├── Program.cs                    # entry: driver check → build WebApplication → start audio
│   ├── wwwroot/                      # populated by build.ps1 from FE/angular/dist; embedded into assembly
│   ├── Web/
│   │   ├── HttpServer.cs             # Kestrel host configuration (listen on 127.0.0.1:<free port>)
│   │   ├── StaticFiles.cs            # ManifestEmbeddedFileProvider wiring + SPA fallback to /index.html
│   │   ├── SessionEndpoint.cs        # GET /api/session — returns {token, csrf, mixerStateInit}
│   │   ├── HealthEndpoint.cs         # GET /api/health — diagnostics
│   │   └── WebSocketEndpoint.cs      # GET /ws — upgrade, validate token, hand off to dispatcher
│   ├── Audio/
│   │   ├── DeviceEnumerator.cs       # WASAPI endpoint discovery
│   │   ├── DriverProbe.cs            # detects VB-CABLE among WASAPI endpoints
│   │   ├── IAudioCaptureSource.cs    # common shape every input source feeds the engine through
│   │   ├── CaptureDevice.cs          # WasapiCapture wrapper, format-converts to internal float mono
│   │   ├── ProcessLoopbackCapture.cs # per-process loopback (Win10 20348+/Win11) — capture Chrome/Spotify/etc. directly
│   │   ├── AudioProcessEnumerator.cs # walks IAudioSessionManager2 to list audio-producing processes
│   │   ├── EngineFactory.cs          # builds a MixEngine from current device + process state; reused by startup AND runtime refresh
│   │   ├── RenderDevice.cs           # WasapiOut wrapper, reads from output ring buffer
│   │   ├── MixEngine.cs              # the heart: read state, mix, write — capture/render arrays nullable to support missing-device slots
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
└── VoicemeterAlt.Host.Tests/         # xUnit
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

- **.NET 8 SDK** (target: `net8.0`, `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` on publish)
- **`Microsoft.AspNetCore.App`** framework reference — Kestrel, static files, WebSocket middleware, minimal API
- **`Microsoft.Extensions.FileProviders.Embedded`** — serves embedded Angular `wwwroot` from the assembly
- **NAudio** (`NAudio.Wasapi`) — WASAPI capture/render
- **`System.Text.Json`** (built-in) — preset & RPC serialization (use source-gen for AOT-friendliness)
- Tests: `xunit`, `xunit.runner.visualstudio`, `BenchmarkDotNet`

`csproj` excerpt:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <GenerateEmbeddedFilesManifest>true</GenerateEmbeddedFilesManifest>
    <AssemblyName>VoicemeterAlt</AssemblyName>
    <OutputType>Exe</OutputType>
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
cd BE/VoicemeterAlt.Host
dotnet run --port 54812
# 1. Probes WASAPI for VB-CABLE; if missing, shows MessageBox and exits.
# 2. Binds Kestrel to 127.0.0.1:<random free port>.
# 3. Starts audio engine.
# 4. Logs the URL (e.g., http://127.0.0.1:54812/) and opens the default browser unless --no-browser.
# 5. Logs go to %LOCALAPPDATA%\VoicemeterAlt\logs\host-*.log.
```

### Hot reload

```powershell
cd BE/VoicemeterAlt.Host
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
| `--port <N>` | Bind to a specific port instead of a random free one. Useful for bookmarking. |
| `--no-browser` | Don't auto-open the default browser. |
| `--no-driver-check` | Skip the VB-CABLE probe (development convenience; not exposed in packaged builds). |

---

## Runtime device refresh

Devices and audio-producing processes can come and go after the host starts (user plugs in a USB mic, launches VLC, etc.). Restarting the host is heavy-handed; the FE can drive a runtime refresh instead.

**RPC**: `refreshDevices` (no params). Returns the new `MixerStateDto`. The handler in [`Ipc/Handlers/DeviceHandlers.cs`](VoicemeterAlt.Host/Ipc/Handlers/DeviceHandlers.cs) snapshots the current state, disposes the running engine, calls [`EngineFactory.Build(previousState)`](VoicemeterAlt.Host/Audio/EngineFactory.cs) to construct a new one, and publishes it through `EngineHost`. There's a brief audio gap (~100–300 ms) while WASAPI re-opens — acceptable for a user-driven action.

**Channel-identity discipline**:

- Physical devices use the WASAPI `MMDevice.ID` (a stable GUID-ish string).
- Process loopbacks use `process:<name>` — survives the target process being restarted (Chrome closes & reopens, the channel id stays the same).
- A channel id present in the previous state but no longer enumerable stays in the new state with `Available = false` and a `null` backing source. The mix engine treats null capture/render slots as silent (input) or discard (output); the FE renders the strip in red so the user knows audio isn't flowing without losing their gain/mute/EQ/route settings.
- Brand-new devices and processes are appended after the preserved channels. The routing matrix grows with `false` in the new positions, leaving existing routes untouched.

**Engine null-tolerance**: [`MixEngine`](VoicemeterAlt.Host/Audio/MixEngine.cs) takes `IAudioCaptureSource?[]` and `RenderDevice?[]`. The hot path checks for null per slot — silent input, no-op render. The single-source-of-truth `MixerState` still drives everything; missing channels just don't have a backing device this tick.

---

## Per-process loopback capture

Beyond physical mics and virtual cables, the engine can pull audio **directly from a single Windows process** — no virtual cable in the chain at all. This is the Win10 build 20348+ / Win11 process-loopback API (`ActivateAudioInterfaceAsync` with `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`), wrapped in [`Audio/ProcessLoopbackCapture.cs`](VoicemeterAlt.Host/Audio/ProcessLoopbackCapture.cs). NAudio doesn't expose this variant, so the class drives the COM activation flow directly: native `IAudioClient` / `IAudioCaptureClient` interop, an `IActivateAudioInterfaceCompletionHandler` CCW, and a dedicated capture thread that drains packets into the same `RingBuffer` the rest of the engine uses.

**Why it matters**: the user can route Chrome → headphones + a "fake mic" virtual cable using only **one** physical cable pair, instead of needing a second cable just to get Chrome's audio out of the OS. Voicemeeter Banana / Potato can't do this — it requires every source to enter via a virtual cable.

**Lifecycle**:

1. At startup, [`AudioProcessEnumerator`](VoicemeterAlt.Host/Audio/AudioProcessEnumerator.cs) walks every active render endpoint's `IAudioSessionManager2`, dedupes the resulting sessions by PID (system PID 0 and the host's own PID are filtered out), and resolves each PID to a process name via `Process.GetProcessById`.
2. `Program.OpenProcessLoopbacks` opens a `ProcessLoopbackCapture` per process and adds it to the engine's capture list with a friendly name like `"chrome (app)"`. The channel id format is `process:<pid>:<name>`.
3. Loopbacks are first-class `IAudioCaptureSource` instances — they appear in `MixerState.Inputs` next to physical captures and the FE wires them through the existing slot-config dialog, no extra UI required.

**Limitations** (worth surfacing to the FE later):

- **PID is bound at startup.** If the user closes Chrome and reopens it, the new Chrome gets a different PID and the existing capture goes silent. The host has to be restarted to pick up the new PID. A future revision can re-resolve by process name when the stream errors and rebuild the engine without a full restart.
- **DRM-protected streams refuse loopback.** Some Spotify Premium / Netflix configurations prevent any process from capturing their output — the same hard limit a virtual cable would hit.
- **Process must be alive at startup** to be enumerated. Apps launched after the host won't appear until the host is restarted.

---

## Threading model (the rule)

| Thread | Owner | Mutate `MixerState`? | Allocate? |
|---|---|---|---|
| WASAPI capture (one per input) | NAudio | ❌ read-only | ❌ |
| Mix thread (single) | `MixEngine` | ❌ read-only via `Volatile.Read` | ❌ |
| WASAPI render (one per output) | NAudio | ❌ read-only | ❌ |
| Telemetry (single) | `MeterAggregator` | ❌ read-only | only the binary frame buffer (pre-allocated) |
| Kestrel request thread | ASP.NET Core | ✅ via `Interlocked.Exchange` of the whole record | ✅ off the audio path |

Locks on the audio path are forbidden. State updates from RPCs swap the entire `MixerState` reference atomically.

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
| GET | `/ws` | WebSocket upgrade. Requires `?token=<from /api/session>`. |

`/api/session` returns the auth token plus the initial mixer state so the SPA can hydrate without a round-trip:

```jsonc
{
  "token": "<random 32-byte hex>",
  "mixerStateInit": { "channels": [...], "matrix": [[...]], "devices": {...} }
}
```

The token is held in memory; it changes on every host restart. The SPA stores it in memory only — never in `localStorage`.

---

## Tasks

Tasks are scoped to be picked up one at a time. **Acceptance** says how to know it's done.

### M1 — Walking skeleton

| ID | Task | Files | Acceptance |
|---|---|---|---|
| BE-001 | Init solution + ASP.NET Core web project, add NAudio + EmbeddedFileProvider packages | `VoicemeterAlt.Host.csproj`, `Program.cs` | `dotnet run` starts a `WebApplication`, prints "host alive", responds 200 to `/api/health` |
| BE-002 | `DeviceEnumerator`: enumerate WASAPI capture & render endpoints, return DTOs with id, friendly name, interface, mix format | `Audio/DeviceEnumerator.cs` | Unit test: returns at least one device on the dev machine |
| BE-003 | `DriverProbe`: scan endpoints, return `{ found, matchedDevice? }` based on case-insensitive friendly-name match against `"VB-Audio" \| "CABLE Input" \| "CABLE Output"` | `Audio/DriverProbe.cs`, `DriverProbeTests.cs` | Unit test with fake enumerator covers found / not-found / partial-name cases |
| BE-004 | `Interop/MessageBox.cs`: P/Invoke `user32.dll!MessageBoxW` with `MB_OK \| MB_ICONERROR` | `Interop/MessageBox.cs` | Calling it shows a real Windows dialog |
| BE-005 | `Program.cs` driver-miss path: if `DriverProbe.Found == false`, show the MessageBox with the install URL message and exit code 2 — **before** `WebApplication.Run()` | `Program.cs` | With VB-CABLE uninstalled, app shows the dialog and exits without binding any port |
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
| BE-061 | `PresetStore`: list, save, load, delete in `%LOCALAPPDATA%\VoicemeterAlt\presets\*.json` | `State/PresetStore.cs` | RPC-driven save creates a file; load returns its contents |
| BE-062 | Device re-resolution on load: presets store endpoint friendly name + interface name; find best match; warn on missing | `State/PresetStore.cs` | Unplugging a device and loading preset returns a structured warning, not an exception |
| BE-063 | RPCs: `listPresets()`, `savePreset(name)`, `loadPreset(name)`, `deletePreset(name)` | `Ipc/Handlers/PresetHandlers.cs` | UI can drive full preset lifecycle |

### M7 — Per-channel processing (gate / EQ / compressor / pan)

A small fixed DSP chain per channel — applied to **both inputs and outputs** (Voicemeter-style strips/buses). Each stage is independently bypassable. Order on the input side: `gate → EQ → compressor`. Order on the output side: `EQ → compressor → pan`. EQ is a parametric biquad cascade — the UI shows a frequency-response curve with one draggable point per band on a log frequency axis (20 Hz - 20 kHz, x) and dB axis (±18 dB, y).

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
| BE-105 *(future)* | RPC `addProcessLoopback(processName)` / `removeProcessLoopback(channelId)` for dynamic add/remove without the engine-rebuild gap. Atomic capture-array swap | `Ipc/Handlers/ProcessHandlers.cs`, `Audio/MixEngine.cs` | UI can attach a process mid-session without the brief refresh silence |
| BE-106 *(future)* | Auto-rebind on PID change: when a `ProcessLoopbackCapture` errors out (process gone), re-resolve by name and re-open with the new PID transparently — no Refresh click needed | `Audio/ProcessLoopbackCapture.cs` | Closing and reopening Chrome doesn't require any user action |

### M8 — Polish

| ID | Task | Files | Acceptance |
|---|---|---|---|
| BE-090 | Single-instance lock via named mutex (`Local\VoicemeterAlt`); second instance opens browser to existing URL and exits | `Program.cs` | Running twice opens a second browser tab; only one host process is alive |
| BE-091 | Crash logging to `%LOCALAPPDATA%\VoicemeterAlt\logs\host-yyyyMMdd.log`; rotate at 10 MB | `Program.cs` | Forced exception writes a log entry with stack trace |
| BE-092 | Graceful shutdown on SIGTERM/console close: stop accepting connections → stop render → stop mix → stop capture | `Program.cs`, `MixEngine.cs` | No glitches in last 100 ms; logs confirm clean stop |
| BE-093 | `Properties/PublishProfiles/win-x64-portable.pubxml` + `win-x64-minimal.pubxml` | `VoicemeterAlt.Host.csproj` | `dotnet publish -p:PublishProfile=...` produces a single .exe |
| BE-094 | `ping()` RPC for the UI to detect liveness | `Ipc/Handlers/SystemHandlers.cs` | UI gets a response in <50 ms |
| BE-095 | Wire `wwwroot` embedding: `<EmbeddedResource Include="wwwroot\**\*" />` and `<GenerateEmbeddedFilesManifest>true</GenerateEmbeddedFilesManifest>` | `VoicemeterAlt.Host.csproj` | Published .exe contains Angular files; no `wwwroot/` folder beside the .exe at runtime |

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

// per-channel processing (M7)
→ {"jsonrpc":"2.0","id":10,"method":"setPan","params":{"channel":1,"pan":-0.3}}
→ {"jsonrpc":"2.0","id":11,"method":"setGate","params":{"channel":1,"gate":{"enabled":true,"thresholdDb":-50,"attackMs":1,"holdMs":50,"releaseMs":120,"rangeDb":-40}}}
→ {"jsonrpc":"2.0","id":12,"method":"setCompressor","params":{"channel":1,"compressor":{"enabled":true,"thresholdDb":-18,"ratio":4,"attackMs":10,"releaseMs":100,"kneeDb":6,"makeupDb":3}}}
→ {"jsonrpc":"2.0","id":13,"method":"addEqBand","params":{"channel":1,"band":{"type":"peaking","frequency":1000,"gainDb":0,"q":1.0}}}
← {"jsonrpc":"2.0","id":13,"result":{"bandId":"<assigned id>"}}
→ {"jsonrpc":"2.0","id":14,"method":"setEqBand","params":{"channel":1,"bandId":"<id>","band":{"type":"peaking","frequency":2500,"gainDb":3.5,"q":1.4}}}
→ {"jsonrpc":"2.0","id":15,"method":"removeEqBand","params":{"channel":1,"bandId":"<id>"}}
```

Binary telemetry frame layout (little-endian):

```
[u32 frameId][u32 channelCount][float32 peak0][float32 rms0][float32 peak1][float32 rms1]...
```

---

## Definition of done (backend, v1)

- All M1–M8 tasks ✅
- 1-hour stability test: zero glitches, flat memory after warm-up
- BenchmarkDotNet memory diagnoser shows 0 B/tick on the mix path
- All unit tests pass; solo logic covered
- `build.ps1 -Mode portable` produces one `VoicemeterAlt.exe` < 80 MB containing the entire app
