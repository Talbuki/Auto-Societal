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
4. Let GitHub Actions build the plugin from `main`, validate the packaged manifest, and publish the stable `latest` GitHub Release.
5. Confirm the `latest` GitHub Release contains an asset named exactly `latest.zip`.
6. Confirm `https://github.com/Talbuki/Auto-Societal/releases/latest/download/latest.zip` downloads successfully before testing installation in Dalamud.

The installer feed is the repository JSON above. Dalamud reads that feed, then downloads the ZIP from the latest GitHub Release asset.

Pushing `main` republishes the downloadable plugin package. Version tags are optional and no longer control the live Dalamud artifact. If the installer returns `404`, the stable `latest` release asset is missing or the GitHub release has not finished publishing yet.
