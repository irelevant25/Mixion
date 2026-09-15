---
name: mixion-release
description: Cut a Mixion release on GitHub — the CHANGELOG.md section, the version tag, and the Release workflow that builds and tests Mixion.exe and publishes it with release notes; also repairing a failed or wrong release. Use when the user asks to release, publish, ship or tag a version, to write or update the changelog, or when the release workflow fails.
---

# Releasing Mixion

Releases are built by `.github/workflows/release.yml` on a GitHub `windows-latest` runner. It starts when:
- a tag `vX.Y.Z` (or `vX.Y.Z-suffix`, a prerelease) is pushed, or
- someone runs it on GitHub (Actions → **Release** → *Run workflow*) with a version — the tag is then created on the branch it runs from; *draft* keeps the release unpublished.

The run, in order — any failing step stops it before anything is published:
1. Checks the version is `X.Y.Z` or `X.Y.Z-suffix`.
2. `build.ps1 -Mode portable -Version X.Y.Z` → `output/Mixion.exe`, self-contained, with the version in its file properties and `/api/health`.
3. Backend tests with `--filter "Requires!=AudioDevices"` (runners have no audio devices), then the frontend unit tests.
4. Release notes: the `## [X.Y.Z]` section of `CHANGELOG.md`, the commits since the previous `v*` tag (all commits for the first release), a compare link, download notes and the exe's SHA-256. No changelog section → a warning and commits only.
5. `gh release create` with `Mixion.exe` attached, a prerelease when the version has a suffix. If the release already exists (a re-run), its notes are refreshed and the exe replaced.

## Cutting a release

Committing, tagging and pushing are outward-facing: confirm with the user before each one.

1. Everything to ship is committed and verified (`mixion-verify` skill).
2. Pick the version (SemVer): major for changes that break existing setups (presets, settings, requirements), minor for new features, patch for fixes only. Prereleases look like `1.3.0-beta.1`.
3. Edit `CHANGELOG.md`: rename `## [Unreleased]` to `## [X.Y.Z] - YYYY-MM-DD` and put a new empty `## [Unreleased]` above it. Write for people using Mixion — what they'll notice — under `### Added`, `### Changed`, `### Fixed`, `### Removed`. Check the section against `git log <previous tag>..HEAD` so nothing user-visible is missing.
4. Commit (`Release vX.Y.Z`) and push. Local `master` tracks `main` on the remote `irelevant25`:
   ```powershell
   git push irelevant25 master:main
   ```
5. Tag that commit and push the tag, which starts the workflow:
   ```powershell
   git tag vX.Y.Z
   git push irelevant25 vX.Y.Z
   ```
6. The `gh` CLI isn't installed locally, so give the user the run page, https://github.com/irelevant25/Mixion/actions, and the release page to check: title, notes, the `Mixion.exe` asset, the prerelease flag.

## When something goes wrong

- **The run failed before publishing** (build, tests): fix it and push the fix. As nothing was published, move the tag to the fixed commit and push it again — `git tag -f vX.Y.Z`, then `git push -f irelevant25 vX.Y.Z` — which starts a new run.
- **A test fails only on the runner**: a test that needs real audio hardware gets `[Trait("Requires", "AudioDevices")]`; anything else is a real failure to fix, not to filter.
- **The published notes are wrong**: edit the release on GitHub, and fix `CHANGELOG.md` on `main` so the history stays right. Re-running the workflow from the same tag also rewrites the notes, from the `CHANGELOG.md` in that tagged commit.
- **The published build is broken**: never move a published tag — people may already have that exe. Fix it and release a patch version.
- **Changelog section missing** (warning in the run): the release is published with commits only. Add the section in the next release, or edit the release on GitHub.
