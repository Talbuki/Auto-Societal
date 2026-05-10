# Societal Reputation

`Societal Reputation` is a Dalamud plugin for tracking Final Fantasy XIV Allied Society progress in a single planner-focused window.

This `v0.1.0` build is intended as a GitHub prerelease. The core planner is usable, but the project should still be treated as an early public build while feedback and compatibility notes are gathered.

## Features

- View reputation rank and current progress for supported Allied Societies.
- See accepted, completed, blocked, and ready daily-quest status per society.
- Get a recommended next society based on visible planner state.
- Filter by activity type and sort by name, expansion, recommendation, or nearest rank-up.
- Review tracked achievement milestone progress for each society.
- Use optional Questionable integration to start recommended or visible society dailies when Questionable is installed and configured.

## Requirements

- XIVLauncher with Dalamud enabled.
- FFXIV access to the relevant Allied Society content you want to track.
- Questionable is optional and only required for automation buttons.

## Usage

- Open the plugin from the Dalamud plugin installer or use `/socrep`.
- Use the planner controls to filter the visible societies and choose a sort mode.
- Open the in-game Achievements window at least once so achievement completion data can load.
- If you want automation features, install and configure Questionable first.

## Current Notes

- Automation depends on Questionable IPC availability. If Questionable is missing or not ready, the planner still works but automation actions will stay unavailable.
- Some societies have no configurable daily range yet, so they will show tracking data without automation support.
- This prerelease is aimed at testing real-game behavior, manifest polish, and release packaging before any broader distribution work.

## GitHub Prerelease Assets

GitHub Actions now builds and publishes the custom-repo release artifact for this project.

The workflow packages these files from the Release build output:

- `SocietalReputation.dll`
- `SocietalReputation.json`

The published release asset is `SocietalReputation.zip`, and custom-repo installs depend on it existing at the URL referenced by [repo.json](/c:/Users/darre/Desktop/FF14%20Plugins/repo.json:1).

Do not commit release binaries, transient local build files, or source-control metadata to this repository.

## Custom Repository Install

If you want testers to install from a custom repo instead of a dev path:

1. Push the release tag `v0.1.0-prerelease`, or manually run the `Build and Release` workflow with that version.
2. Let GitHub Actions publish `SocietalReputation.zip` and `SocietalReputation.json` to the matching GitHub release.
3. Host the root-level [repo.json](/c:/Users/darre/Desktop/FF14%20Plugins/repo.json:1) from a public raw URL or GitHub Pages.
4. Tell testers to copy that public `repo.json` URL.
5. In XIVLauncher, open `Settings` -> `Experimental`, add the custom plugin repository URL, then refresh the plugin installer.

The current repository entry still points to the published release artifact used for custom-repo installs:

- `https://github.com/Talbuki/Auto-Societal/releases/download/v0.1.0-prerelease/SocietalReputation.zip`

If that asset is missing, GitHub will return `404 Not Found` and Dalamud installs will fail even if the repo manifest itself loads correctly. The release workflow is what keeps that URL populated.

If you change the release tag, asset name, or hosting path, update [repo.json](/c:/Users/darre/Desktop/FF14%20Plugins/repo.json:1) to match before sharing it.

## Repository

- Repo: https://github.com/Talbuki/Auto-Societal
