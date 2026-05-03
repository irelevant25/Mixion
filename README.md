# VoicemeterAlt

A personal Voicemeter Potato alternative for Windows 11. User-mode audio mixer/router that runs on top of VB-CABLE / VAC, with a browser-based UI served by the same Windows process that runs the audio engine.

> **What this is:** a single Windows .exe that runs an audio mixer, exposes a routing matrix + channel strips + VU meters in your browser at `http://127.0.0.1:<port>/`, and persists presets as JSON.
>
> **What this is NOT:** a full Voicemeter replacement. This app does **not** install its own virtual audio devices. Other apps will not see "VoicemeterAlt Input" in their device list. Instead, you route apps to VB-CABLE Input (via Windows audio settings or app-level output selection), and our mixer pulls from VB-CABLE Output. Shipping a kernel virtual audio driver on Windows 11 requires an EV code-signing certificate (~$300–600/year), which is out of scope for this project.

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
- Already meets the user's "I know Angular very well" goal without dragging in Electron's ~150 MB.
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
| Persistence | **JSON files** in `%LOCALAPPDATA%\VoicemeterAlt\presets\` | Trivial, human-editable |

---

## Repository layout

```
Voicemeter\
├── README.md                       # this file
├── build.ps1                       # one-shot build script (-Mode portable | minimal)
├── BE\
│   ├── README.md                   # backend tasks (.NET host)
│   └── VoicemeterAlt.Host\         # .NET 8 audio engine + HTTP/WS server (created in M1)
│       └── wwwroot\                # populated by build.ps1 from FE/angular/dist before publish
├── FE\
│   ├── README.md                   # frontend tasks (Angular)
│   └── angular\                    # Angular workspace (created in M1)
└── output\
    ├── README.md                   # describes what lands here
    └── VoicemeterAlt.exe           # produced by build.ps1
```

See [BE/README.md](BE/README.md) for backend tasks and [FE/README.md](FE/README.md) for frontend tasks.

---

## Prerequisites

- Windows 11 (or Windows 10 22H2+)
- **VB-CABLE** installed (https://vb-audio.com/Cable/) — provides the virtual audio endpoints the mixer routes to/from
- **.NET 8 SDK** (for development; end users with the `portable` build need nothing)
- **Node.js 20+** and **npm**
- A modern browser (Chrome / Edge / Firefox) — already on every Windows install

---

## Development workflow

Two terminals during development. Angular runs under `ng serve` with HMR; it proxies `/api` and `/ws` calls to the running host.

```powershell
# Terminal 1 — audio host (HTTP + WS + audio)
cd BE/VoicemeterAlt.Host
dotnet run
# Listens on http://127.0.0.1:<port> (printed at startup).
# Visit that URL directly to use the production-style UI from embedded files.

# Terminal 2 — Angular dev server with HMR (preferred during FE work)
cd FE/angular
npm start          # ng serve --proxy-config proxy.conf.json
# Visit http://localhost:4200
# proxy.conf.json forwards /api/* and /ws to the .NET host port
```

In production (post-`build.ps1`), there is no `ng serve` — the user double-clicks `VoicemeterAlt.exe` and the .NET host serves Angular from its embedded `wwwroot`.

---

## Build & ship — `build.ps1`

A single PowerShell script at the repo root produces one .exe in [output/](output/). It builds Angular for production, copies the bundle into `BE/VoicemeterAlt.Host/wwwroot/`, then publishes the .NET host with `wwwroot` embedded into the assembly.

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
| `portable`| `output/VoicemeterAlt.exe` — self-contained single file            | ~70 MB | Nothing — just Windows |
| `minimal` | `output/VoicemeterAlt.exe` — framework-dependent single file       | ~5 MB  | .NET 8 Runtime (`winget install Microsoft.DotNet.Runtime.8`) |

The script:
1. Checks prerequisites (`dotnet`, `node`, `npm`).
2. `cd FE/angular && npm ci && npm run build -- --configuration=production` → `FE/angular/dist/voicemeter-alt/browser/`.
3. Copies the Angular bundle into `BE/VoicemeterAlt.Host/wwwroot/`.
4. `dotnet publish BE/VoicemeterAlt.Host -c Release -r win-x64 --self-contained:<true|false> -p:PublishSingleFile=true`.
5. Copies the resulting `VoicemeterAlt.exe` into `output/`.

The Angular files travel inside the assembly as embedded resources (via `ManifestEmbeddedFileProvider`), so `output/VoicemeterAlt.exe` is genuinely a single file with no companion `wwwroot/` folder.

---

## Driver presence check (runtime)

The packaged app **requires VB-CABLE to be installed** to be useful — otherwise there are no virtual endpoints to mix. The check runs on every launch, before any HTTP server starts:

1. The host enumerates WASAPI render+capture endpoints and looks for a VB-Audio device (matches `"VB-Audio"`, `"CABLE Input"`, or `"CABLE Output"` in the friendly name, case-insensitive).
2. **Found** → continue: bind Kestrel to a free port, start audio engine, log the URL, optionally open the default browser to it, run.
3. **Not found** → show a native Windows error dialog (`MessageBoxW` via P/Invoke):
   > *VB-CABLE driver not detected. VoicemeterAlt requires VB-CABLE to provide virtual audio endpoints. Install it from https://vb-audio.com/Cable/ and relaunch the app.*
   After the user dismisses the dialog, the process exits cleanly. No HTTP server, no audio engine, no leftover process.

This happens entirely inside the .exe. There is no separate launcher. Whether the user double-clicks the portable .exe or runs `dotnet run`, the behavior is the same.

---

## v1 feature scope

| Feature | In v1 | Notes |
|---|---|---|
| Multi-input → multi-output routing matrix | ✅ | NxM toggle grid |
| Per-channel gain (dB) | ✅ | Log-scale slider |
| Per-channel mute | ✅ | |
| Per-channel solo | ✅ | Soloing any channel mutes non-soloed channels in same bus |
| Real-time peak + RMS VU meters | ✅ | 30 Hz telemetry, 60 fps canvas redraw |
| Save/load JSON presets | ✅ | Re-resolves devices by friendly name on load |
| Auto-open default browser on launch | ✅ | `--no-browser` flag to disable |
| EQ / compressor / gate | ❌ | Future v1.x |
| ASIO support | ❌ | Future v1.x |
| MIDI control | ❌ | Future v1.x |
| Sample-rate conversion across mismatched devices | ❌ | v1 detects mismatch and refuses to start with a clear error |
| System tray icon | ❌ | Future v1.x — for now, the host runs in a console window |
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
| M6 | Presets | JSON I/O, device re-resolve | Preset manager UI |
| M7 | Polish | Single-instance, crash logs, embedded `wwwroot`, single-file publish | Reconnect, error states, prod build wired into `build.ps1` |

Each milestone is independently shippable to yourself. **Detailed tasks** for each side are in:
- [BE/README.md](BE/README.md) — backend
- [FE/README.md](FE/README.md) — frontend

---

## Critical implementation notes

- **MMCSS is not optional.** Without `AvSetMmThreadCharacteristics("Pro Audio")` on the audio threads, you'll get glitches under any system load. One P/Invoke call.
- **Zero allocation on the audio path.** Pre-allocate every `float[]` at startup. Verify with BenchmarkDotNet's memory diagnoser before each release.
- **Pin Kestrel to `127.0.0.1`.** Never bind to `0.0.0.0` or any public interface — this app should never be reachable from the LAN. HMAC-token-on-WS gives an extra layer.
- **HTTPS is not used.** The whole stack is loopback-only on the same machine; certificates would just add friction. Browsers permit unencrypted WebSockets to `127.0.0.1` without warnings.
- **Angular bundle goes inside the assembly.** `ManifestEmbeddedFileProvider` keeps the .exe truly portable. `dotnet publish` settings: `<GenerateEmbeddedFilesManifest>true</GenerateEmbeddedFilesManifest>` and `<EmbeddedResource Include="wwwroot\**\*" />`.
- **Sample-rate mismatch is the #1 WASAPI bug source.** Detect on start; refuse with a clear error rather than silently glitching.
- **Solo logic gotcha:** soloing any channel forces all non-soloed channels in the same bus to be muted. Easy to get backwards. Unit test it.
- **Device GUIDs aren't stable across reboots.** Resolve presets by endpoint friendly name + interface name; warn on missing device.

---

## License & status

Personal project. No public release planned.
