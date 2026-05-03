# Frontend — Angular SPA (browser)

Plain Angular web app served by the .NET host. The user opens `http://127.0.0.1:<port>/` in any modern browser. **No Electron, no preload, no Node runtime in production.**

See the [root README](../README.md) for the overall architecture and the [backend README](../BE/README.md) for the RPC contract this frontend consumes.

---

## Project layout

```
FE/
└── angular/                          # Angular workspace (created in M1)
    ├── angular.json
    ├── package.json
    ├── proxy.conf.json               # `ng serve` proxy: forwards /api/* and /ws to the .NET host
    ├── src/
    │   ├── index.html
    │   ├── main.ts
    │   ├── styles.css
    │   └── app/
    │       ├── app.component.ts
    │       ├── app.config.ts         # bootstrap with provideRouter, etc.
    │       ├── app.routes.ts
    │       ├── core/
    │       │   ├── session.service.ts          # GET /api/session at app init; holds token + initial state
    │       │   ├── ipc.service.ts              # WS client, JSON-RPC, telemetry stream
    │       │   └── mixer-state.store.ts        # signals-based store
    │       ├── features/
    │       │   ├── routing-matrix/
    │       │   │   ├── routing-matrix.component.ts
    │       │   │   └── routing-matrix.component.html
    │       │   ├── channel-strip/
    │       │   │   ├── channel-strip.component.ts
    │       │   │   └── channel-strip.component.html
    │       │   ├── vu-meter/
    │       │   │   └── vu-meter.component.ts   # canvas + RAF
    │       │   ├── processing/
    │       │   │   ├── processing-panel.component.ts   # drawer hosting Gate / EQ / Comp / Pan
    │       │   │   ├── pan-control.component.ts
    │       │   │   ├── gate.component.ts
    │       │   │   ├── compressor.component.ts
    │       │   │   └── eq-curve.component.ts           # canvas, log-freq x-axis, dB y-axis, draggable bands
    │       │   └── preset-manager/
    │       │       └── preset-manager.component.ts
    │       └── shell/
    │           ├── connection-banner.component.ts
    │           └── error-page.component.ts     # shown when /api/session fails (host not running)
    └── tsconfig.json
```

---

## Dependencies

- **angular** ^18 — standalone components, signals, `provideRouter`, `provideHttpClient(withFetch())`
- **rxjs** — only for the WebSocket stream and the telemetry subject
- **typescript** ^5.4

No state library beyond signals. No `@ngrx/*`. No `ngx-*` UI kit unless the user wants one — keep dependencies minimal. **No Electron, no electron-builder.**

---

## Run / debug

```powershell
cd FE/angular
npm install
npm start                # ng serve --proxy-config proxy.conf.json --port 4200
# Open http://localhost:4200
```

`proxy.conf.json` forwards `/api/*` and `/ws` to the running .NET host (which prints its port at startup):

```json
{
  "/api": { "target": "http://127.0.0.1:54812", "secure": false, "changeOrigin": true },
  "/ws":  { "target": "ws://127.0.0.1:54812",   "secure": false, "ws": true }
}
```

Because the host's port is random per launch, dev workflow is:
1. `dotnet run` in `BE/VoicemeterAlt.Host` and **read the printed port**.
2. Update `proxy.conf.json` with that port, OR start the host with `--port 54812` (fixed) for stable dev.
3. `npm start` in `FE/angular`.

In production (post-`build.ps1`), there is no proxy and no `ng serve`. The user opens whatever URL the host prints; Angular and the WS share the same origin.

---

## How the SPA talks to the host

The whole app runs on a single origin. Bootstrap looks like this:

```
APP_INITIALIZER
  └─ SessionService.init()
       ├─ fetch GET /api/session
       │    → { token, mixerStateInit }
       ├─ MixerStateStore.hydrate(mixerStateInit)
       └─ IpcService.connect(token)
            └─ new WebSocket(`/ws?token=${token}`)
                 ├─ text frames → JSON-RPC 2.0 (call/response)
                 └─ binary frames → Float32Array meter telemetry
```

### `IpcService` surface

```ts
class IpcService {
  readonly status = signal<'idle' | 'connecting' | 'connected' | 'disconnected'>('idle');
  readonly telemetry$: Observable<Float32Array>;     // hot, multicast, ~30 Hz
  connect(token: string): Promise<void>;
  call<T>(method: string, params?: unknown): Promise<T>;
  notify(method: string, params?: unknown): void;    // no id, no response expected
}
```

The token is held in memory only — never written to `localStorage` or `sessionStorage`. On reload, the SPA re-fetches `/api/session` and gets a fresh token.

### Why hydrate from `/api/session.mixerStateInit`?

Saves a WebSocket round-trip on first paint. The HTTP response includes the snapshot the SPA would otherwise have to fetch via `getState()` over `/ws`. This makes the first-paint feel instant.

---

## Tasks

Each task is scoped to a single PR-sized unit. **Acceptance** says how to know it's done.

### M1 — Walking skeleton

| ID | Task | Files | Acceptance |
|---|---|---|---|
| FE-001 | Init Angular workspace `voicemeter-alt` (standalone, signals, no zone.js if Angular version supports it) | `FE/angular/` | `ng build` produces `dist/voicemeter-alt/browser/` |
| FE-002 | `proxy.conf.json` for dev: forwards `/api` and `/ws` to a configurable host port | `FE/angular/proxy.conf.json` | `npm start` proxies cleanly to a running host |
| FE-003 | `SessionService` with `init(): Promise<void>` that calls `GET /api/session`, stores `token` and `mixerStateInit` | `core/session.service.ts` | Unit test mocks `fetch`, asserts service state |
| FE-004 | `APP_INITIALIZER` runs `SessionService.init()` and `IpcService.connect(token)` before bootstrap completes | `app.config.ts` | App reaches `connected` status before first render |
| FE-005 | `IpcService.connect(token)` opens `WebSocket('/ws?token=' + token)`, sends a `ping` RPC, sets `status` signal | `core/ipc.service.ts` | Status shows `connected` within 1 s |
| FE-006 | App shell shows a "connected" page with the host URL and the version reported by `ping`/`/api/health` | `app.component.ts` | Visiting the URL after `dotnet run` shows the connected page |
| FE-007 | `error-page.component.ts`: shown when `SessionService.init()` rejects (host not reachable). Provides "Retry" button | `shell/error-page.component.ts` | Stopping the host shows the error page on next reload |

### M2 — (no FE work — backend only)

### M3 — Routing matrix + JSON-RPC

| ID | Task | Files | Acceptance |
|---|---|---|---|
| FE-010 | `IpcService.call<T>(method, params)` — JSON-RPC client with id correlation, 5 s timeout, typed error | `core/ipc.service.ts` | Unit test: returns response, rejects on error, rejects on timeout |
| FE-011 | `MixerStateStore` (signals): hydrated by `mixerStateInit` from `SessionService`; selectors for `channels`, `matrix`, `devices` | `core/mixer-state.store.ts` | Store contains data on first render — no loading flicker |
| FE-012 | `RoutingMatrixComponent`: NxM grid, click toggles `setRoute`, optimistic update with rollback on RPC error | `features/routing-matrix/*` | Clicking a cell flips both UI state and audio routing |
| FE-013 | Display device list: inputs (rows) and outputs (columns) with friendly names | `routing-matrix.component.html` | VB-CABLE entries visible |

### M4 — Gain / mute / solo

| ID | Task | Files | Acceptance |
|---|---|---|---|
| FE-020 | `ChannelStripComponent`: gain slider, mute, solo, channel name | `features/channel-strip/*` | Strip renders for each channel |
| FE-021 | dB scale slider with a perceptual curve (e.g., `[-60, +12] dB`, exponential mapping) | `channel-strip.component.ts` | Sliding feels natural, not bunched at the top |
| FE-022 | Wire slider/buttons to `setGain`, `setMute`, `setSolo` — debounce slider to ~30 Hz to avoid RPC flood | `channel-strip.component.ts` | Sliding produces RPCs at most every ~33 ms |
| FE-023 | Visual state for solo: dim non-soloed channels in same bus | `channel-strip.component.ts` | Soloing one channel visually dims the others |

### M5 — VU meters

| ID | Task | Files | Acceptance |
|---|---|---|---|
| FE-030 | `IpcService` decodes incoming binary frames into `Float32Array` and emits via `telemetry$` Subject | `core/ipc.service.ts` | Subscriber receives ~30 frames/sec |
| FE-031 | `VuMeterComponent`: takes `channelIndex` input, draws on `<canvas>` via `requestAnimationFrame`, reads latest value imperatively from a service-held `Float32Array` (not via Angular CD) | `features/vu-meter/vu-meter.component.ts` | 16 meters on screen at 60 fps with <5% renderer CPU |
| FE-032 | Peak hold: peak indicator decays at e.g. -20 dB/sec; RMS bar steady | `vu-meter.component.ts` | Speaking into mic shows peak that lingers and falls smoothly |
| FE-033 | `subscribe()` on app load; `unsubscribe()` on `document.visibilitychange === 'hidden'` and on disconnect | `core/ipc.service.ts`, `app.component.ts` | Hiding the tab stops telemetry traffic |
| FE-034 | Performance check: profile with 16 channels active, confirm <16 ms per frame in Chrome DevTools | — | Frame budget healthy |

### M6 — Presets

| ID | Task | Files | Acceptance |
|---|---|---|---|
| FE-040 | `PresetManagerComponent`: list (from `listPresets`), load, save (with name input), delete | `features/preset-manager/*` | Full lifecycle works against the backend |
| FE-041 | Confirm dialog for delete and overwrite (browser `confirm()` for v1; custom modal later if it gets ugly) | `preset-manager.component.ts` | Cannot accidentally clobber a preset |
| FE-042 | Show device-mismatch warning when a loaded preset references a missing device | `preset-manager.component.ts` | Banner with the missing device's friendly name |

### M7 — Per-channel processing UI (gate / EQ / compressor / pan)

A processing drawer per channel — opens from an "FX" button on every input strip and output bus. Hosts four panels: gate, parametric EQ, compressor, pan. The EQ is the centerpiece: a canvas-based curve where each band is a draggable point on a **log frequency x-axis (20 Hz - 20 kHz)** and a **linear dB y-axis (±18 dB)**. Drag the point to move frequency + gain together; mouse wheel adjusts Q; double-click empty space adds a band; double-click a point removes it.

| ID | Task | Files | Acceptance |
|---|---|---|---|
| FE-050 | Extend `MixerStateStore` channel signal with `pan`, `gate`, `compressor`, `eq` (each may be `null` = bypass). Typed interfaces match the BE state | `core/mixer-state.store.ts` | Store reflects new fields after `getState()`; existing M3/M4 selectors keep working |
| FE-051 | `PanControlComponent`: horizontal slider, range -100..+100, double-click recenters. Wired to `setPan` (debounced) | `features/processing/pan-control.component.ts` | Sliding pan audibly shifts L/R image |
| FE-052 | `GateComponent`: threshold (dB), attack (ms), hold (ms), release (ms), range (dB), bypass toggle. Wired to `setGate` | `features/processing/gate.component.ts` | All five controls drive the RPC; bypass toggle removes gate from chain |
| FE-053 | `CompressorComponent`: threshold (dB), ratio (n:1), attack (ms), release (ms), knee (dB), makeup (dB), bypass toggle. Small GR meter showing live gain reduction (read from telemetry) | `features/processing/compressor.component.ts` | Driving signal above threshold shows GR meter activity |
| FE-054 | `EqCurveComponent` — canvas parametric EQ. **X: log frequency 20 Hz - 20 kHz. Y: linear gain ±18 dB.** Interactions: drag point = move freq + gain; mouse wheel on point = adjust Q; double-click empty area = add band; double-click point = remove band; right-click point = filter-type menu (peaking / low-shelf / high-shelf / low-pass / high-pass / notch). Frequency-grid + dB-grid drawn behind the curve | `features/processing/eq-curve.component.ts` | All interactions wired; `addEqBand` / `setEqBand` / `removeEqBand` audibly reshape the channel |
| FE-055 | EQ response rendering: compute combined magnitude response of all bands at canvas resolution and draw the curve through/under the points; redraw on state change via `effect()`. Use the same RBJ formulas as the BE so the visual matches the audio exactly | `features/processing/eq-curve.component.ts` | Curve visually matches the audible filter; updates instantly on drag |
| FE-056 | EQ point rendering: filled circle per band, color-coded by filter type, label on hover (`f`, `gainDb`, `Q`); selected point highlighted; keyboard arrows nudge selected point | `features/processing/eq-curve.component.ts` | Hovering shows readout; selection persists during drag |
| FE-057 | `ProcessingPanelComponent`: vertical stack of `Pan`, `Gate`, `Eq`, `Compressor`. Opens as a drawer/overlay from an "FX" button on `ChannelStripComponent`. Works for both input strips and output buses (same component, different channel) | `features/processing/processing-panel.component.ts`, `features/channel-strip/channel-strip.component.ts` | Clicking "FX" on any input or output channel opens its processing panel |
| FE-058 | Debounce all DSP controls to ~30 Hz before sending RPCs; coalesce consecutive moves of the same band into a single `setEqBand` per tick | `features/processing/*` | Network panel shows ≤30 Hz RPC cadence under continuous dragging |
| FE-059 | Verify presets round-trip DSP state through the existing `savePreset` / `loadPreset` flow (BE preset DTO grew in BE-070) | `features/preset-manager/*` | Save → wipe channel DSP → load → DSP is restored exactly |

### M9 — Per-process loopback capture (FE side)

The BE now exposes per-process loopback captures (`process:<pid>:<name>` channel ids, friendly names like `"chrome (app)"`) as additional entries in `MixerState.Inputs`. They flow through the **existing** `MixerStateStore.inputs()` signal, which means the slot-config dialog already lists them alongside physical mics and virtual cables — **no FE work was strictly required to ship the MVP**. The user picks a process from the same radio list as any other input device.

The tasks below are quality-of-life follow-ups for when the BE adds dynamic add/remove (BE-105 / BE-106).

| ID | Task | Files | Acceptance |
|---|---|---|---|
| FE-080 | Visually distinguish process inputs from device inputs in the slot-config dialog (e.g. an "app" pill next to the name; group under a sub-heading "Applications") | `features/channel-strip/slot-config-dialog.component.ts` | Process loopbacks render with a clear visual marker; physical devices unchanged |
| FE-081 | Friendly-name strip: BE sends `"chrome (app)"`; FE strips the `(app)` suffix when the visual pill makes it redundant | `features/channel-strip/slot-config-dialog.component.ts` | Cleaner labels without losing the "this is a process" cue |
| FE-084 | Refresh button in the slot-config dialog header. Calls `refreshDevices` RPC, then `MixerStateStore.replace()` with the returned state. Disabled + label flips to "Refreshing…" while in flight; surface RPC errors inline | `features/channel-strip/slot-config-dialog.component.ts`, `core/ipc.service.ts` | Apps started after the host launched (e.g. opening VLC) appear in the picker after a Refresh click without restarting the BE |
| FE-085 | Render channels with `available === false` in red across the strip (border + name + "×" badge in header) and in the slot picker ("(no longer available)" annotation, red name). Filter unavailable channels out of the picker entirely if no slot binds to them — the spec is "channel strip stays put with red name; picker hides ghosts that no one is using" | `features/channel-strip/channel-strip.component.{html,css,ts}`, `features/channel-strip/slot-config-dialog.component.ts`, `core/mixer-state.store.ts` | Unplug the mic → the bound strip's name turns red, "×" badge appears, controls greyed; same device shows red in picker only as long as a slot still references it |
| FE-082 *(after BE-105)* | "Add application" affordance: opens a sub-picker listing currently-audio-producing processes via a new `listAudioProcesses` RPC, calls `addProcessLoopback(processName)` to attach it. Replaces the brief audio gap of full-engine refresh with seamless attachment | `features/channel-strip/slot-config-dialog.component.ts`, `core/ipc.service.ts` | User can attach any audio app mid-session without the refresh ~200 ms silence |
| FE-083 *(after BE-105)* | Show a "process gone" badge on a slot whose backing PID has exited (BE marks the channel dead via a new field on `ChannelDto`); offer "remove" or "reattach" actions | `features/channel-strip/channel-strip.component.ts` | Closing Chrome surfaces a red-dot indicator on the corresponding slot |

### M8 — Polish

| ID | Task | Files | Acceptance |
|---|---|---|---|
| FE-060 | Reconnect logic: exponential backoff (1 s, 2 s, 4 s, capped at 30 s); on reconnect, re-call `getState()` and re-`subscribe()` | `core/ipc.service.ts` | Brief host restart is transparent: meters resume, state re-hydrates |
| FE-061 | "Host stopped" banner when WS drops; auto-hides on reconnect | `shell/connection-banner.component.ts` | Stopping the host shows banner; restarting hides it |
| FE-062 | Production build wired into `build.ps1`: `ng build --configuration=production`; output goes to `dist/voicemeter-alt/browser/` | `FE/angular/angular.json` | `build.ps1` consumes the `browser/` subfolder when copying into `BE/.../wwwroot` |
| FE-063 | Verify SPA fallback works: navigating directly to `/some/route` returns `index.html` and Angular's router takes over | `app.routes.ts` | Refreshing on `/preset-manager` doesn't 404 |
| FE-064 | Set `<base href="./">` (relative) so the SPA works regardless of port and root path | `src/index.html` | Asset URLs resolve correctly when served from any host port |

---

## RPC contract reference

The frontend consumes the contract defined in [BE/README.md](../BE/README.md#rpc-contract-canonical). All RPCs go through `IpcService.call<T>()`. Telemetry comes through `IpcService.telemetry$` and is **not** routed through Angular change detection.

---

## Style & conventions

- **Standalone components only.** No `NgModule`.
- **Signals for state, RxJS only at the IPC boundary.** UI components prefer `computed()` and `effect()` over RxJS pipes.
- **OnPush change detection** on every component (default with signals).
- **No global state lib.** `MixerStateStore` is a service holding signals.
- **Canvas (not SVG, not DOM) for any high-frequency rendering** (VU meters, future spectrum analyzer).
- **No animations library.** CSS transitions are enough for this UI.
- **Debounce control surfaces** that emit fast (sliders) to ~30 Hz before sending RPCs.
- **No browser storage** (`localStorage`, `sessionStorage`, `IndexedDB`) for tokens or state. The host is the source of truth; tokens are in-memory.

---

## Definition of done (frontend, v1)

- All M1, M3-M8 tasks ✅
- App connects on launch via `/api/session`, hydrates state, renders matrix + strips + meters
- 16 meters at 60 fps with <5% renderer CPU on a modern desktop
- Brief host restart is transparent (auto-reconnect)
- `build.ps1` consumes the production Angular bundle into the embedded `wwwroot`
- No console errors during normal use
