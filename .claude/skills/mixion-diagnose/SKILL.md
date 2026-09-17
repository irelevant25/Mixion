---
name: mixion-diagnose
description: Troubleshoot Mixion at runtime — no sound, a device or app missing or red in the slot picker, dropouts or growing delay, VB-CABLE errors, the UI not loading or not connecting, crashes, startup or shutdown problems. Reads the host log, audio settings and presets, maps log lines and WASAPI HRESULTs to causes, and inspects the live state. Use when the user reports that something in Mixion doesn't work or behaves oddly.
---

# Diagnosing Mixion

Start from evidence: the log first, then settings and the live state, then the code. Reading the user's files is fine; ask before changing their settings or presets, or stopping their Mixion.

## Where things are

| What | Where |
|---|---|
| Log | `%LOCALAPPDATA%\Mixion\logs\host-yyyyMMdd.log`, one per day; at 10 MB it rotates to `host-yyyyMMdd.1.log` … `.5.log`. The tray's **Console** item shows the same output live. |
| Audio settings | `%LOCALAPPDATA%\Mixion\audio-settings.json`: `CaptureBufferMs`, `RenderLatencyMs` (1–200, default 10), `PreferLowLatency` (default true), `ExclusiveRenderDeviceIds`, `ExclusiveRenderLatencyMsByDeviceId` |
| Presets | `%LOCALAPPDATA%\Mixion\presets\`, plus the last-preset pointer that is loaded at start |
| Port | `%LOCALAPPDATA%\Mixion\host-port.txt`: the port the host reuses (`--port N` overrides it and isn't saved) |
| CLI | `--port N`, `--no-browser`, `--no-driver-check` (development only) |

Each line looks like `2026-09-14T19:18:21.657+02:00 INFO [DeviceWatcher] message` (lines written straight to the crash log have no category). Every start begins with `Host starting (pid …, version …); raw args=[…]`, so split a multi-run log there and check which build was running. Find the lines around the user's report — for example with the Grep tool on the logs folder, or `Get-Content <log> -Tail 300` in PowerShell.

## What the log says

| Line | Meaning |
|---|---|
| `Basic VB-CABLE not detected; aborting startup.` | Exit code 2 after a message box. The basic single-cable VB-CABLE is missing; A+B, C+D and Voicemeeter don't count. |
| `Another instance already owns the mutex; forwarding and exiting.` | Mixion was already running; the second launch only opened its UI. |
| `Mixion host listening at http://127.0.0.1:N` | The UI's address for this run. |
| `Engine target sample rate: N Hz` · `Skipping {Flow} '{Name}': sample rate R Hz ≠ engine rate N Hz.` | The engine runs at the default output's rate and leaves out devices at other rates (no resampling). Fix: give those devices the same rate in Windows Sound settings. |
| `Could not open {Flow} '{Name}'; skipping.` · `Could not open render endpoint …` · `Could not attach …` | Read the HRESULT in the exception (table below). The device watcher retries with backoff (10 s, doubling up to 10 min; a device plug/unplug notification retries devices at once) and logs later failures at debug level only. |
| `Opened process loopback for '{app}' (PID n).` · `Attached new input '{app} (app)'.` · `Detached input …: its source is no longer available.` · `Re-attached input '…' to process n.` | Normal app tracking. The same source detaching and re-attaching over and over is not normal — see Dropouts. |
| `Could not enumerate audio processes; per-process loopback inputs disabled.` | App capture is off for this engine build; check the exception (OS build, audio service). |
| `Audio engine did not start; HTTP/WS surface remains available.` | The UI loads with no mixer. The watcher keeps retrying: `Audio engine rebuilt after an earlier failure.` or `Audio engine still can't be rebuilt; retrying later.` The exception says why. |
| `No spare channel slots left; rebuilding the engine with room to grow.` | Many devices/apps appeared; one ~200 ms gap is expected. |
| `Device/app reconciliation failed; retrying on the next pass.` | One occurrence is harmless; repeated ones are a bug — read the stack trace. |
| `Auto-loaded last preset '{Name}'` · `Preset '{Name}' references N device(s) not currently present.` | Missing devices keep their slots and reconnect when they appear. |
| `WS open  remote=…` / `WS close` | A UI tab connected / disconnected. No `WS open` after `listening at` means no tab reached this host. |
| `Refused GET {Path} from host=… origin=…` | 403 from `LoopbackRequestGuard`: the request's `Host` wasn't `localhost` / `127.0.0.1` / `[::1]`, or its `Origin` wasn't a loopback origin. Expected for a foreign web page probing the port. If it's the user's own tab, they opened Mixion under another name (a hosts-file alias, the machine name); use `http://127.0.0.1:N`. |
| `MixEngine loop crashed` · `Unhandled RPC exception in {Method}` · `FATAL` | Bugs. Collect the stack trace and the lines before it. |

## WASAPI HRESULTs

| HRESULT | Name | Usual cause |
|---|---|---|
| `0x8889000A` | `AUDCLNT_E_DEVICE_IN_USE` | Another app holds the device in exclusive mode (Windows: device Properties → Advanced → "Allow applications to take exclusive control"). |
| `0x88890004` | `AUDCLNT_E_DEVICE_INVALIDATED` | Device unplugged, disabled, or its format changed; the watcher re-opens or detaches it. |
| `0x88890008` | `AUDCLNT_E_UNSUPPORTED_FORMAT` | The format was refused — typical for exclusive-mode outputs; turn exclusive off for that device or raise its latency. |
| `0x8889000E` | `AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED` | Exclusive mode is disabled for that device in Windows. |
| `0x88890010` | `AUDCLNT_E_SERVICE_NOT_RUNNING` | The Windows Audio service is stopped (early at logon, or broken). |
| `0x88890019` | `AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED` | The driver needs another buffer size; change the latency for that device. |
| `0x80070005` | `E_ACCESSDENIED` | On a microphone: Windows privacy blocks it — Settings → Privacy & security → Microphone → allow desktop apps. |
| `0x80070490` | `ERROR_NOT_FOUND` | The endpoint id no longer exists (device removed or reinstalled). |

## Symptoms

- **No sound on an output**: Is the route on in the matrix? Is the channel muted, is another channel soloed, is the gain at the bottom? Was the output opened (`Opened …` or a skip/failure line for it)? Is it at the engine's rate? Is another app holding it exclusively?
- **A device isn't in the picker or stays red**: a skip line for a different sample rate, an open failure with an HRESULT, disabled in Windows, or microphone privacy. A device whose opens keep failing is retried less and less often — reconnecting it (or Rescan) retries at once.
- **An app isn't captured or stays red**: App capture needs Windows 11 or Windows 10 build 20348+. The app must have played sound since it started (an audio session). Apps sharing an executable name share one channel. DRM-protected playback may refuse capture. An app removed in the slot picker stays detached until Rescan, a restart, or loading a preset that uses it. Mixion never captures a process tree that contains Mixion itself. If Mixion was started from the app (opened from Chrome's downloads, say), it captures the app's audio process instead, so `Opened process loopback for '<app>' (PID n)` names a child process rather than the app's root; an app that plays from the very process that started Mixion stays out — start Mixion from Explorer instead. Check who started it with `Get-CimInstance Win32_Process -Filter "Name='Mixion.exe'"` (`ParentProcessId`).
- **Other apps are silent on one device**: that output is in exclusive mode (`ExclusiveRenderDeviceIds`); only Mixion can play there.
- **Short dropouts on every output**: When a live source stops delivering (an app stops its audio, a device drops out), the mix waits up to ~50 ms for it — longer for a source that usually delivers in bigger chunks — and then continues without it. Frequent dropouts point to a source that keeps stopping and starting: look for repeated `Detached`/`Re-attached` lines, or an app that plays short sounds with gaps. Other causes: buffers too small for the hardware (`CaptureBufferMs` / `RenderLatencyMs`, or `PreferLowLatency` on a driver that handles IAudioClient3 badly), exclusive-mode latency too low, Bluetooth devices, CPU load.
- **Delay that grows over a long session**: The devices' clocks drift apart and Mixion doesn't resample, so the faster source's buffer slowly fills — up to about 170 ms on an input or 85 ms on an output at 44.1/48 kHz. Restarting Mixion resets it.
- **UI doesn't load, or keeps reconnecting**: Is the host running (`listening at` in today's log)? Compare the tab's address with `host-port.txt` — a tab left over from a run on another port can't connect; open the UI from the tray. A page that loads during startup waits up to 15 s for the engine.
- **The tab didn't close when Mixion exited**: Expected under `ng serve` (the dev tab reconnects instead). In the packaged app a browser can refuse; the retired-tab screen shows then.
- **Mixion won't start at all**: the log's last start — VB-CABLE check, the single-instance forward, a `FATAL` exception.

## Live state

With Mixion running on port N (from the log or `host-port.txt`), the smoke-test client prints the channel list, the apps' up/down state and meter frames. It only reads, so it's safe next to the user's UI:

```bash
timeout 20 node .claude/skills/mixion-verify/scripts/wsclient.mjs N
```

`http://127.0.0.1:N/api/health` returns the host version.

## Reporting back

Tell the user what the evidence shows (quote the log lines), the likely cause, and the fix — a Windows setting, a Mixion setting, or a code change. Reproduce a code-level problem with a test or the `mixion-verify` smoke test before changing code.
