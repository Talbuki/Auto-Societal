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
5. Let GitHub Actions build Release and upload `latest.zip` to the GitHub Release.

The installer feed is the repository JSON above. Dalamud reads that feed, then downloads the ZIP from the latest GitHub Release asset.
