# Changelog

All notable changes to Mixion are recorded here, newest first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

Add entries under **Unreleased** as changes land. To release, rename that heading to the version and date (for example `## [1.3.0] - 2026-10-01`) and start a new, empty **Unreleased** section above it. The release workflow publishes the version's section as the GitHub release notes.

## [Unreleased]

### Security

- Web pages open in your browser can no longer connect to Mixion. Mixion now accepts connections only from its own UI, which also blocks DNS rebinding. Before, an unguessable token was the only protection.
- Connection tokens now work once and expire after a minute, so a token copied from the log can't be used.

## [1.2.1] - 2026-09-15

### Added

- The tray icon's tooltip shows the version.

### Fixed

- The UI showed the version as `1.0.0.0` (or `1.2.0.0`) instead of the release version. It now shows `v1.2.1`-style versions; a build with changes after a release shows them too, like `v1.2.1-3-gabc1234`.
- Removing a slot, or giving it another device, left that channel's routes on, so audio kept playing where you could no longer see or switch it off. The routes are now switched off with it.
- An app such as Chrome was missing from the input list when Mixion had been opened from that app, for example from Chrome's downloads.

## [1.2.0] - 2026-09-15

### Added

- True stereo from input to output: audio on one side moves only that side's meter. Pan works as balance, and the noise gate and compressor react to both sides together.
- Apps and devices are tracked automatically. An app is one channel by its name, so closing and reopening Chrome brings its channel back within a second or two, and plugging in or unplugging a device attaches or detaches it — no Refresh needed.
- Presets keep slots bound to apps and devices that aren't running, and reconnect them when they appear.
- The browser tab closes itself when Mixion exits, and a newly opened Mixion tab closes older ones.

### Changed

- Mixion reuses its previous port, so the UI keeps the same address across restarts.
- Refresh in the slot picker is now a small Rescan link, needed only as a last resort.
- When a source stops (an app closes, a device is unplugged), the other channels keep playing after a brief dropout, and the delay returns to normal right after.

### Fixed

- An app such as Chrome stopped working in Mixion after it was closed and reopened.
- Both meters of a channel moved together even for one-sided audio — the mixer was effectively mono.
- Restarting Mixion opened a new browser tab on a new port each time.
- If the audio engine couldn't start (for example because Windows Audio wasn't ready at logon), it stayed off. Mixion now retries, and the last preset is still applied.
- A page opened while Mixion was starting could show an empty mixer.

## [1.1.0] - 2026-05-06

### Added

- Audio settings page: capture buffer, render latency, low-latency mode, and exclusive mode per output device.
- Signal flow view, with a test that measures the delay of a route.
- Low-latency mode for devices that support it, for less delay.
- The VB-CABLE input and output are shown as one connected pair, and routes that would loop audio back through the cable are blocked.
- A warning before unsaved changes are discarded when you start a new preset.

### Changed

- Less delay through the mixer.

## [1.0.0] - 2026-05-04

First release.

### Added

- Routing matrix: any mix of inputs — microphones, virtual cables and apps — to any outputs.
- Per-channel gain, mute and solo, with peak and RMS meters.
- Per-channel noise gate, parametric EQ with a draggable curve, compressor and pan.
- Per-app capture (Chrome, Spotify, games, …).
- Refresh picks up new devices and apps without restarting.
- Presets with created and edited dates and rename; the last preset loads at start.
- One portable `Mixion.exe`: it checks for VB-CABLE at start, runs in the system tray, allows a single instance, and opens its UI in the browser.
