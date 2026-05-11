# Societal Reputation

Custom Dalamud plugin repository for `SocietalReputation`.

## Install in Dalamud

1. Open `/xlsettings` in game.
2. Go to `Experimental`.
3. Under `Custom Plugin Repositories`, add:

   `https://raw.githubusercontent.com/Talbuki/Auto-Societal/main/pluginmaster.json`

4. Save settings.
5. Open `/xlplugins` and install `Societal Reputation`.

## Release Flow

1. Bump the plugin version in [SocietalReputation.csproj](SocietalReputation/SocietalReputation.csproj).
2. Update `AssemblyVersion` in `pluginmaster.json` to match the packaged manifest version.
3. Commit and push to `main`.
4. Create and push a tag such as `v1.0.1`.
5. Let GitHub Actions build the plugin, validate the packaged manifest, and upload `latest.zip` to the GitHub Release.
6. Confirm the latest GitHub Release contains an asset named exactly `latest.zip`.
7. Confirm `https://github.com/Talbuki/Auto-Societal/releases/latest/download/latest.zip` downloads successfully before testing installation in Dalamud.

The installer feed is the repository JSON above. Dalamud reads that feed, then downloads the ZIP from the latest GitHub Release asset.

Pushing `main` without publishing a release tag does not update the downloadable plugin package. If the installer returns `404`, the latest GitHub Release is missing `latest.zip` or has not finished publishing yet.
