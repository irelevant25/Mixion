# output/

Build artifact produced by [`../build.ps1`](../build.ps1). This folder is generated — nothing here is hand-edited and the whole folder is safe to delete (or wipe with `.\build.ps1 -Clean`).

## Layout

```
output/
├── README.md            # this file (kept across builds)
└── Mixion.exe    # the entire app: audio engine + HTTP/WS server + Angular UI
```

That single `.exe` is the entire app. Angular static files are embedded as resources inside the assembly via `ManifestEmbeddedFileProvider`, so there's no companion `wwwroot/` folder to ship alongside.

## What you get

Both build modes produce **one file**: `Mixion.exe`. They differ only in size and runtime requirements:

| `-Mode`   | Size  | Requires on target machine          |
|-----------|-------|-------------------------------------|
| `portable`| ~70 MB | Nothing — just Windows 10+         |
| `minimal` | ~5 MB  | .NET 8 Desktop Runtime installed   |

## Runtime behavior

When the user double-clicks `Mixion.exe`:

1. **Driver check** — enumerates WASAPI endpoints. If VB-CABLE isn't found, a native Windows dialog appears:
   > *VB-CABLE driver not detected. Mixion requires VB-CABLE to provide virtual audio endpoints. Install it from https://vb-audio.com/Cable/ and relaunch the app.*
   The user clicks OK, the process exits cleanly. No web server starts, no audio engine starts.
2. **Found** — the host:
   - Binds Kestrel to `127.0.0.1`, reusing the previous run's port when it's free (otherwise a random free port).
   - Starts the audio engine.
   - Logs the URL to its console window (e.g., `http://127.0.0.1:54812/`).
   - Opens the user's default browser at that URL (unless `--no-browser`).
   - Stays running in the system tray until the user picks **Exit** (or logs off).

The browser tab is the entire UI — close it to disconnect, reopen the URL (or tray → UI) to reconnect. The audio engine keeps running while the host process is alive. When Mixion exits, the tab closes itself, and the next launch opens one fresh tab on the same port.

## Reproducing a build

```powershell
# from the repo root
.\build.ps1                    # default: portable
.\build.ps1 -Mode minimal      # smaller, requires .NET 8 runtime
.\build.ps1 -Clean             # wipe ./output and BE/.../wwwroot first
```

See [../README.md](../README.md#build--ship--buildps1) for the full script reference.
