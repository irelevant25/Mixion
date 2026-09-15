---
name: mixion-verify
description: Verify that a Mixion change works — build and test the .NET host and the Angular SPA, then optionally run the real host end to end (WebSocket client, simulated app restart, graceful Ctrl+C shutdown). Use before reporting Mixion work as done, when asked to test, verify, run or check that something works, and after changes to the audio engine, device/app tracking, the WebSocket protocol, or startup/shutdown.
---

# Verifying Mixion changes

Run what matches the change and report results as they are — test counts, failures, new warnings. Commands assume the repo root.

## 1. Backend

```powershell
dotnet build Mixion.sln
dotnet test BE/Mixion.Host.Tests/Mixion.Host.Tests.csproj
```

`AudioProcessEnumeratorTests` and `ProcessSnapshotTests.Capture_*` use the machine's real audio stack and process table; everything else is self-contained. `ControlSocketTests` starts real Kestrel on a random port.

Tests that need real audio devices carry `[Trait("Requires", "AudioDevices")]`, because the release workflow runs `dotnet test --filter "Requires!=AudioDevices"` on runners without audio hardware. Give that trait to any new test of that kind, and run the filtered command too when you add or rename such tests.

## 2. Frontend

```powershell
cd FE/angular
npx ng build --configuration development      # compile + strict template type-check
npx ng test --watch=false --browsers=ChromeHeadless
npx ng build                                  # production: budgets, optimizer
```

Known noise: the production build warns that `signal-flow` and `audio-settings` component styles exceed the 4 kB budget.

## 3. Live host smoke test

Runs the real host against the machine's audio devices and checks, from a WebSocket client's point of view:
- the engine starts and meter frames stream;
- an app with an audio session is attached, detached when it exits, and re-attached when it starts again (a renamed PowerShell copy playing a silent WAV — nothing audible);
- Ctrl+C sends `hostShutdown`, closes the socket with 1001 `host-shutdown`, and the host exits with code 0.

Before running:
1. Build first (`dotnet build Mixion.sln`) — the script runs `BE/Mixion.Host/bin/Debug/net8.0-windows/win-x64/Mixion.exe`.
2. The script refuses to start while a Mixion process runs (the single-instance gate would forward to it). Don't stop the user's own instance without asking.
3. If `%LOCALAPPDATA%\Mixion\audio-settings.json` lists `ExclusiveRenderDeviceIds`, the test host locks those outputs for ~30 s — ask the user first.
4. The test host auto-loads the user's last preset (`%LOCALAPPDATA%\Mixion\presets\`), so its routes are live for ~25 s — apps the user is listening to (a game, a call) could be heard twice. If a preset exists, ask first. Without one every route starts off and nothing is audible.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude/skills/mixion-verify/scripts/smoke.ps1
```

Takes ~40 s. Side effects: a tray icon for the duration, lines appended to `%LOCALAPPDATA%\Mixion\logs\`, scratch files in `%TEMP%\mixion-smoke`. It passes `--port 54917 --no-browser --no-driver-check`, so the remembered port and browser state are untouched.

The script prints the client transcript, the relevant host log lines and a PASS/FAIL table. On a FAIL, read the transcript and the log excerpt before changing code — device-specific warnings (for example a render endpoint another app holds exclusively) are expected on some machines.

## 4. In the browser (manual)

Not scriptable here; ask the user or describe what to check:
- Dev: launch profile `Mixion.Host (no-driver-check, fixed port)` + `npm start` in `FE/angular`, open http://localhost:4200. Under `ng serve` the tab reconnects after a host restart instead of closing.
- Packaged: `.\build.ps1`, run `output/Mixion.exe`, tray → Exit should close the tab; restarting opens one fresh tab on the same port.
- Stereo: a source panned hard left moves only the left meter of its strip.
- Slot picker: closing/reopening an app turns its strip red, then back to normal within ~1–2 s, without Rescan.
