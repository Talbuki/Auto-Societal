# Societal Reputation v0.1.0-prerelease

First public GitHub prerelease of `Societal Reputation`.

## Highlights

- Allied Society planner window with rank, reputation, and daily status tracking.
- Activity filtering, recommendation sorting, and actionable-society views.
- Achievement milestone tracking for supported societies.
- Optional Questionable integration for starting available society dailies.
- Performance-focused UI and IPC caching improvements for smoother repeated refreshes.

## Notes For Testers

- This is an early public build and should be treated as a prerelease.
- Automation requires Questionable to be installed, enabled, and configured.
- Achievement completion data requires opening the in-game Achievements window at least once.
- Some societies currently show planner tracking without automation support.

## Release Assets

- `SocietalReputation.zip` for Dalamud custom-repository installs
- `repo.json` for custom repository hosting

`SocietalReputation.zip` must be attached to the GitHub prerelease tagged `v0.1.0-prerelease`. Until that asset is published, the custom-repo download URL in `repo.json` will return `404 Not Found` and installs will fail.

The published install artifact should contain these files at the package root:

- `SocietalReputation.dll`
- `SocietalReputation.json`
