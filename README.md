# Mixion

User-mode audio mixer/router that runs on top of VB-CABLE / VAC, with a browser-based UI served by the same Windows process that runs the audio engine.

> **What this is:** a single Windows .exe that runs an audio mixer, exposes a routing matrix + channel strips + VU meters in your browser at `http://127.0.0.1:<port>/`, and persists presets as JSON.
>
> **What this is NOT:** This app does **not** install its own virtual audio devices. Other apps will not see "Mixion Input" in their device list. Instead, you route apps to VB-CABLE Input (via Windows audio settings or app-level output selection), and our mixer pulls from VB-CABLE Output. Shipping a kernel virtual audio driver on Windows 11 requires an EV code-signing certificate, which is out of scope for this project.

---

## Architecture

Single-process model. The .NET 8 host runs the audio engine, an HTTP server (Kestrel), and a WebSocket on the same port. The Angular UI is served as static files from inside that .exe and runs in any modern browser.

```
+-----------------------------+        http(s)://127.0.0.1:<port>/        +---------------------------+
|   Browser (Chrome/Edge/...) |  <----- HTTP: GET /  -> index.html ------ |   .NET 8 Host (.exe)      |
|   Angular SPA               |         GET /api/session                  |   - Kestrel HTTP server   |
|   - Routing matrix          |         GET /assets/*.js,*.css            |   - Static files (Angular |
|   - Channel strips          |  <----- WebSocket: /ws -------------->    |     embedded in assembly)|
|   - VU meters               |         JSON-RPC for control              |   - WebSocket endpoint    |
|   - Preset manager          |         Binary frames for meters          |   - Audio engine (WASAPI) |
+-----------------------------+                                           +---------------------------+
                                                                                       |
                                                                                       v
                                                                            Windows Audio (WASAPI)
                                                                            physical devices +
                                                                            VB-CABLE virtual devices
```

**Why single process**
- One artifact to ship and run.
- Same-origin: no CORS, no separate auth port. Browser hits one URL.
- Audio engine and UI server share a process; clean shutdown of one stops everything.
- Kestrel + WebSockets is in the .NET BCL — no additional shell/runtime dependency.

**Why browser-based UI**
- Without dragging in Electron's ~150 MB.
- The artifact stays small: one self-contained .exe with Angular embedded.
- Open in any browser — Chrome, Edge, Firefox. Bookmark the URL.

---

## Tech stack

| Layer | Choice | Why |
|---|---|---|
| Audio engine | **.NET 8 + NAudio (WASAPI)** | Mature WASAPI bindings, single-file publish, user knows C# |
| HTTP / WS server | **ASP.NET Core (Kestrel)** | Ships with .NET 8, mature WebSocket middleware, static files from embedded provider |
| Static file delivery | **`ManifestEmbeddedFileProvider`** — Angular `dist/` embedded as resources in the host assembly | Single-file .exe contains the entire UI; no `wwwroot` next to the binary |
| UI framework | **Angular 18+** (standalone components, signals) | User knows it well |
| IPC | **WebSocket on same origin (`/ws`)**, JSON-RPC 2.0 (text) + binary telemetry frames | Single transport, simple, fast enough |
| Auth | **HMAC token** issued by `GET /api/session`; required on WS connect | Prevents other local processes from connecting |
| Meters | **Canvas + requestAnimationFrame** | DOM/SVG can't sustain 16 meters at 60 fps without CD pressure |
| Persistence | **JSON files** in `%LOCALAPPDATA%\Mixion\presets\` | Trivial, human-editable |

---

## Repository layout

```
Mixion\
├── README.md                       # this file
├── CHANGELOG.md                    # user-facing changes per release (feeds the GitHub release notes)
├── CLAUDE.md                       # project notes for Claude Code: architecture rules, commands
├── .claude\                        # Claude Code skills (verify, add-rpc, diagnose, release) + agents (engine / FE reviewers)
├── .github\workflows\release.yml   # tag v* → build, test, publish a GitHub release with Mixion.exe
├── build.ps1                       # one-shot build script (-Mode portable | minimal, -Version)
├── icon.png / icon - no bg.png     # source artwork for the app icon
├── BE\
│   ├── README.md                   # backend tasks (.NET host)
│   └── Mixion.Host\                # .NET 8 audio engine + HTTP/WS server (created in M1)
│       ├── Resources\app.ico       # multi-resolution .exe / tray / taskbar icon
│       └── wwwroot\                # populated by build.ps1 from FE/angular/dist before publish
├── FE\
│   ├── README.md                   # frontend tasks (Angular)
│   └── angular\                    # Angular workspace (created in M1)
│       └── public\                 # favicon.ico + logo.png served as Angular assets
└── output\
    ├── README.md                   # describes what lands here
    └── Mixion.exe                  # produced by build.ps1
```

See [BE/README.md](BE/README.md) for backend tasks and [FE/README.md](FE/README.md) for frontend tasks.

---

## Prerequisites

- Windows 11 (or Windows 10 22H2+)
- **Basic VB-CABLE** installed (https://vb-audio.com/Cable/) — the free single-cable download labelled *"VB-CABLE Virtual Audio Device"*. Higher-tier products (VB-CABLE A+B, VB-CABLE C+D, the Voicemeeter family) are **not** sufficient on their own — see [Driver presence check](#driver-presence-check-runtime) below.
- **.NET 8 SDK** (for development; end users with the `portable` build need nothing)
- **Node.js 20+** and **npm**
- A modern browser (Chrome / Edge / Firefox) — already on every Windows install

---

## Development workflow

Two terminals during development. Angular runs under `ng serve` with HMR; it proxies `/api` and `/ws` calls to the running host.

```powershell
# Terminal 1 — audio host (HTTP + WS + audio)
cd BE/Mixion.Host
dotnet run
# Listens on http://127.0.0.1:<port> (printed at startup).
# Visit that URL directly to use the production-style UI from embedded files.

# Terminal 2 — Angular dev server with HMR (preferred during FE work)
cd FE/angular
npm start          # ng serve --proxy-config proxy.conf.json
# Visit http://localhost:4200
# proxy.conf.json forwards /api/* and /ws to the .NET host port
```

In production (post-`build.ps1`), there is no `ng serve` — the user double-clicks `Mixion.exe` and the .NET host serves Angular from its embedded `wwwroot`.

---

## Build & ship — `build.ps1`

A single PowerShell script at the repo root produces one .exe in [output/](output/). It builds Angular for production, copies the bundle into `BE/Mixion.Host/wwwroot/`, then publishes the .NET host with `wwwroot` embedded into the assembly.

```powershell
# default: self-contained portable .exe (~70 MB, runs anywhere on Windows 10+)
.\build.ps1

# explicit
.\build.ps1 -Mode portable

# framework-dependent .exe (~5 MB, requires .NET 8 Desktop Runtime on the target machine)
.\build.ps1 -Mode minimal

# wipe ./output before building
.\build.ps1 -Clean
```

Modes:

| `-Mode`   | Produces                                                           | Size  | Requires on target |
|-----------|--------------------------------------------------------------------|-------|--------------------|
| `portable`| `output/Mixion.exe` — self-contained single file            | ~70 MB | Nothing — just Windows |
| `minimal` | `output/Mixion.exe` — framework-dependent single file       | ~5 MB  | .NET 8 Runtime (`winget install Microsoft.DotNet.Runtime.8`) |

The script:
1. Checks prerequisites (`dotnet`, `node`, `npm`).
2. `cd FE/angular && npm ci && npm run build -- --configuration=production` → `FE/angular/dist/mixion/browser/`.
3. Copies the Angular bundle into `BE/Mixion.Host/wwwroot/`.
4. `dotnet publish BE/Mixion.Host -c Release -r win-x64 --self-contained:<true|false> -p:PublishSingleFile=true`.
5. Copies the resulting `Mixion.exe` into `output/`.

The Angular files travel inside the assembly as embedded resources (via `ManifestEmbeddedFileProvider`), so `output/Mixion.exe` is genuinely a single file with no companion `wwwroot/` folder.

### Releases

GitHub builds releases with [.github/workflows/release.yml](.github/workflows/release.yml). Pushing a version tag starts it:

```powershell
git tag v1.0.0
git push irelevant25 v1.0.0
```

It can also be started from GitHub (**Actions → Release → Run workflow**, entering the version). The workflow runs `build.ps1 -Version <version>` and the backend and frontend tests, then creates the GitHub release with `Mixion.exe` attached. The release notes are that version's section of [CHANGELOG.md](CHANGELOG.md), followed by the commits since the previous tag. Versions with a suffix (`1.1.0-beta.1`) are marked as prereleases.

---

## Driver presence check (runtime)

The packaged app **requires the basic VB-CABLE to be installed** to be useful — otherwise there are no virtual endpoints to mix. The check runs on every launch, before any HTTP server starts:

1. The host enumerates WASAPI render+capture endpoints and looks for an endpoint whose friendly name **starts with** `"CABLE Input"` or `"CABLE Output"` (case-insensitive, with a whitespace or `(` boundary). That's the signature of the free single-cable VB-CABLE — render endpoint *"CABLE Input (VB-Audio Virtual Cable)"*, capture endpoint *"CABLE Output (VB-Audio Virtual Cable)"*.
2. **Found** → continue: bind Kestrel to a free port, start audio engine, log the URL, optionally open the default browser to it, run.
3. **Not found** → show a native Windows error dialog (`MessageBoxW` via P/Invoke) telling the user to install the basic VB-CABLE and exit cleanly with code 2. No HTTP server, no audio engine, no leftover process.

**Why specifically the basic version.** VB-Audio also ships VB-CABLE A+B, VB-CABLE C+D, and the Voicemeeter family. These install endpoints with distinct prefixes — `CABLE-A Input`, `CABLE-B Output`, `VoiceMeeter VAIO`, etc. Those are deliberately rejected by the probe: this app's mental model is "one virtual cable, in and out", and accepting any VB-Audio variant would let the user end up with multiple cables to choose between, plus inconsistent friendly names in saved presets across machines. If a user installs both basic VB-CABLE and a higher-tier variant, the probe still passes — the basic endpoint is enough.

This happens entirely inside the .exe. There is no separate launcher. Whether the user double-clicks the portable .exe or runs `dotnet run`, the behavior is the same. `--no-driver-check` is available as a development convenience and is not exposed in packaged builds.

---

## v1 feature scope

| Feature | In v1 | Notes |
|---|---|---|
| Multi-input → multi-output routing matrix | ✅ | NxM toggle grid |
| Per-channel gain (dB) | ✅ | Log-scale slider |
| Per-channel mute | ✅ | |
| Per-channel solo | ✅ | Soloing any channel mutes non-soloed channels in same bus |
| Stereo signal path | ✅ | Every input and output is stereo end to end; mono sources feed both sides; pan acts as balance; gate and compressor are stereo-linked |
| Real-time peak + RMS VU meters | ✅ | Separate L/R meters; 30 Hz telemetry, 60 fps canvas redraw |
| Per-channel DSP — gate, parametric EQ, compressor, pan | ✅ | Independently bypassable; canvas-based EQ curve with draggable bands |
| Per-process loopback capture (Chrome / Spotify / OBS / games) | ✅ | Win10 20348+/Win11; an app is one channel by name — close and reopen it and it re-attaches on its own |
| Automatic device & app tracking | ✅ | Devices and apps attach, detach and re-attach by themselves without interrupting other channels; the slot picker's Rescan is a last resort |
| Save/load JSON presets | ✅ | Re-resolves devices by id, then friendly name; DSP state and slot layout round-trip; slots bound to an app or device that isn't present stay bound and reconnect when it appears |
| Preset metadata (created / edited timestamps, rename) | ✅ | List view sorts by edited desc; "current preset" pointer auto-loaded on next host start |
| Auto-open default browser on launch | ✅ | `--no-browser` flag to disable; the host reuses its previous port |
| One browser tab | ✅ | The tab closes itself when Mixion exits; a newer Mixion tab retires older ones |
| ASIO support | ❌ |  |
| MIDI control | ❌ |  |
| Sample-rate conversion across mismatched devices | ❌ | Devices at a different sample rate than the default output are left out |
| System tray icon | ✅ | Console, UI, Run on startup, Exit |
| Own virtual audio driver | ❌ | Rejected (EV cert cost) |

---

## Milestones (high level)

| # | Milestone | Backend | Frontend |
|---|---|---|---|
| M1 | Walking skeleton | Kestrel serves a "hello" page on a free port; driver check + MessageBox on miss; auto-open browser | Angular workspace builds; renders a "connected" page; reads `/api/session` |
| M2 | Mix engine MVP | N inputs → N outputs, hardcoded routing, no glitches over 30 min | — |
| M3 | Routing matrix + JSON-RPC | RPC handlers + state mutation over `/ws` | NxM grid wired to RPCs |
| M4 | Gain / mute / solo | Engine applies, RPCs to set | Channel strips |
| M5 | VU meters end-to-end | Telemetry frames at 30 Hz over `/ws` | Canvas meters at 60 fps |
| M6 | Presets | JSON I/O, device re-resolve, created/edited timestamps, rename, "current preset" detach | Preset manager UI: list with timestamps, sort by edited; shell Save / Save As; "New" button |
| M7 | Per-channel DSP | Gate, 5-band parametric EQ, compressor, pan — applied per input strip and per output bus | Processing drawer: gate / EQ / compressor / pan controls; canvas-based EQ curve |
| M9 | Per-process loopback capture | Win10 20348+/Win11 process loopback; auto-rebind on PID change; `refreshDevices` runtime refresh | Slot picker lists processes alongside devices; per-row detach button; `available=false` red strips |
| M8 | Polish | Single-instance, crash logs, embedded `wwwroot`, single-file publish | Reconnect, error states, prod build wired into `build.ps1` |

Each milestone is independently shippable to yourself. **Detailed tasks** for each side are in:
- [BE/README.md](BE/README.md) — backend
- [FE/README.md](FE/README.md) — frontend

---

## Critical implementation notes

- **MMCSS is not optional.** Without `AvSetMmThreadCharacteristics("Pro Audio")` on the audio threads, you'll get glitches under any system load. One P/Invoke call.
- **Zero allocation on the audio path.** Pre-allocate every `float[]` at startup. Verify with BenchmarkDotNet's memory diagnoser before each release. The DSP chain (gate → EQ → compressor → pan) and per-process loopback ring buffers all comply.
- **Pin Kestrel to `127.0.0.1`.** Never bind to `0.0.0.0` or any public interface — this app should never be reachable from the LAN. HMAC-token-on-WS gives an extra layer.
- **HTTPS is not used.** The whole stack is loopback-only on the same machine; certificates would just add friction. Browsers permit unencrypted WebSockets to `127.0.0.1` without warnings.
- **Angular bundle goes inside the assembly.** `ManifestEmbeddedFileProvider` keeps the .exe truly portable. `dotnet publish` settings: `<GenerateEmbeddedFilesManifest>true</GenerateEmbeddedFilesManifest>` and `<EmbeddedResource Include="wwwroot\**\*" />`.
- **Sample-rate mismatch is the #1 WASAPI bug source.** Detect on start; refuse with a clear error rather than silently glitching. Channels with no live device (unplugged mic, dead process loopback) survive in `MixerState` with `Available = false` and contribute silence — they don't take down the engine.
- **Solo logic gotcha:** soloing any channel forces all non-soloed channels in the same bus to be muted. Easy to get backwards. Unit test it.
- **Device GUIDs aren't stable across reboots.** Resolve presets by endpoint id, then friendly name + interface name; warn on missing device. Process loopbacks use `process:<name>` (PID-free) and capture the app's whole process tree, so a Chrome restart re-attaches to the same channel.
- **Topology changes funnel through one place.** `EngineHost` serialises full rebuilds (Rescan, audio-settings changes, `removeProcessLoopback` — ~200 ms audible gap) and the in-place changes the device watcher makes (spare engine slots, no gap), so two changes never race for the same WASAPI endpoints.
- **One source must never stall the mix.** A capture that stops delivering is mixed as silence after ~50 ms and the backlog the other channels built up meanwhile is dropped, so a closed app or an unplugged device costs a brief dropout — it can't take the other channels down or leave extra latency behind.
- **Driver probe is intentionally narrow.** Only the basic single-cable VB-CABLE satisfies it (see [Driver presence check](#driver-presence-check-runtime)). Don't loosen the matcher to "any VB-Audio device" — the routing UX depends on having exactly one canonical *CABLE Input* / *CABLE Output* pair.

---

## License & status

Personal project. Builds are published as [GitHub releases](https://github.com/irelevant25/Mixion/releases).
