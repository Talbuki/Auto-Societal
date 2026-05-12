# Changelog

## v1.2.2

### Daily Scheduled Auto-Start

- Added a recurring daily auto-start option so the plugin can begin the recommended society automation at a user-configured local time.
- Added scheduled start controls to the existing Settings popup, including enable/disable plus local hour and minute selection.
- Moved the scheduler check onto the framework update path so timed starts can still trigger while the main window is closed.
- Prevented duplicate starts by tracking the last successful automatic start date and only consuming the day after a successful launch.

## v1.2.1

### Header Help Popups

- Added a `Known issues` popup beside `Settings` so players can quickly review current limitations and caveats from the main window header.
- Added a `Planned updates` popup beside `Settings` so players can see a short roadmap of upcoming usability and tracking improvements.

## v1.2.0

### Beginner-Friendly UI Refresh

- Added a simpler default window flow aimed at first-time users, with advanced controls preserved behind progressive disclosure instead of removed outright.
- Promoted the recommended next step into a clearer “what should I do next?” experience so the primary action is easier to spot and understand.
- Rewrote key planner and automation labels in more player-facing language, reducing reliance on technical or developer-centric terminology.
- Shifted onboarding toward a more actionable setup experience with clearer status explanations for new players.
- Improved blocked and empty-state guidance so the window explains why a society cannot be started and what the player should do next.
- Reframed diagnostics and related advanced controls as troubleshooting tools to keep the default window less intimidating.
- Reduced top-bar decision density so first-time users see fewer immediate controls while still keeping expert options available in advanced views.

## v1.1.0

### Automation Compatibility Refactor

- Moved automation action/state ownership into `QuestionableAutomationService` so the planner window now renders service-reported status instead of tracking a separate UI-only automation message.
- Added service-owned automation intent for daily rows, including explicit actions for accepting available quests, accepting remaining pickups, continuing in-progress dailies, hand-in-ready states, setup-required states, and Questionable-unavailable states.
- Reworked planner refresh to request daily quest statuses in a batch, reusing shared Questionable availability/running checks instead of repeating the same IPC-heavy lookups for each society row.
- Isolated Questionable IPC names behind a single compatibility layer inside the automation service to make future IPC contract updates easier to maintain.
- Replaced the blocking quest-accept loop with a resumable automation session driven by framework updates, removing UI-thread waiting while preserving pickup, continue, and hand-in-ready messaging.
- Added a short post-accept pickup settle delay so automation does not prematurely switch into quest objectives while the next daily pickup is still appearing.
- Renamed the main automation button to `Start / continue recommended dailies` and updated row action labels to reflect the actual service-reported automation action for that society.

## v1.0.1 and Release Automation Updates

Changes in this entry cover repository activity from `v1.0.0` through the current `HEAD`. This summary is limited to repo history and release-process updates; it does not include terminal activity or build verification from outside the committed changes.

### Plugin / Release Version

- Bumped the plugin version in `SocietalReputation.csproj` from `0.1.0` to `1.0.1`.
- Updated `pluginmaster.json` to match the packaged manifest version by changing `AssemblyVersion` from `0.1.0.0` to `1.0.1.0`.

### Automation / Planner Behavior

- Added a dedicated partial-pickup state for society dailies so the planner no longer treats a society as fully actionable while Questionable is still accepting the remaining available quests.
- Deferred readiness-driven `InProgress` / `ReadyToTurnIn` behavior until daily pickup is complete for the current batch, defined as either `3` accepted quests or `0` ready quests remaining.
- Updated the planner UI to show this phase as pickup-in-progress with an `Accept remaining quests` action, neutral row styling, and messaging that directs the user to finish pickups before continuing objectives.
- Kept the change wired through shared daily-status evaluation so recommendation scoring and readiness-based alerts do not promote partially accepted societies as if pickup were already finished.

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
