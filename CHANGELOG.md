# Changelog

## v1.0.1 and Release Automation Updates

Changes in this entry cover repository activity from `v1.0.0` through the current `HEAD`. This summary is limited to repo history and release-process updates; it does not include terminal activity or build verification from outside the committed changes.

### Plugin / Release Version

- Bumped the plugin version in `SocietalReputation.csproj` from `0.1.0` to `1.0.1`.
- Updated `pluginmaster.json` to match the packaged manifest version by changing `AssemblyVersion` from `0.1.0.0` to `1.0.1.0`.
- Kept the scope of this release metadata-focused: there are no plugin source-code changes in this diff range, so no new gameplay or UI behavior is implied by this changelog.

### Build / Packaging / Distribution

- Changed the GitHub Actions release workflow to publish from pushes to `main` instead of version tags.
- Updated the workflow to build the release artifact, publish a stable GitHub Release named `latest`, and upload `SocietalReputation/bin/Release/SocietalReputation/latest.zip`.
- Added release validation through `scripts/Validate-PluginRelease.ps1` to:
  - verify the project version matches `pluginmaster.json`
  - verify `DownloadLinkInstall` and `DownloadLinkUpdate` end with `/releases/latest/download/latest.zip`
  - verify the packaged manifest matches repository metadata for `InternalName` and `AssemblyVersion`
- Added a post-publish verification step that checks the installer download URL after the GitHub Release is created.
- Updated the README release flow to document the new publish path and installer behavior.
- Clarified the external distribution contract: Dalamud still reads `pluginmaster.json` as the installer feed, but plugin downloads now resolve through the stable `latest` GitHub Release asset.
- Documented that pushing `main` republishes the live downloadable package, while version tags are now optional and no longer control the active Dalamud download.
