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

Use the built plugin files from the output directory as the release payload:

- `SocietalReputation.dll`
- `SocietalReputation.json`

Do not include transient local build files or source-control metadata in the GitHub release archive.

### ZIP Layout

Package the prerelease as `SocietalReputation.zip` with these files at the root of the archive:

- `SocietalReputation.dll`
- `SocietalReputation.json`

Do not nest the files inside an extra folder in the ZIP.

## Custom Repository Install

If you want testers to install from a custom repo instead of a dev path:

1. Create a GitHub prerelease tagged `v0.1.0-prerelease`.
2. Upload `SocietalReputation.zip` to that release.
3. Host the root-level [repo.json](/c:/Users/darre/Desktop/FF14%20Plugins/repo.json:1) from a public raw URL or GitHub Pages.
4. Tell testers to copy that public `repo.json` URL.
5. In XIVLauncher, open `Settings` -> `Experimental`, add the custom plugin repository URL, then refresh the plugin installer.

The repository entry already points to:

- `https://github.com/Talbuki/Auto-Societal/releases/download/v0.1.0-prerelease/SocietalReputation.zip`

If you change the release tag, ZIP name, or hosting path, update [repo.json](/c:/Users/darre/Desktop/FF14%20Plugins/repo.json:1) to match before sharing it.

## Repository

- Repo: https://github.com/Talbuki/Auto-Societal
