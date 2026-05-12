using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using SocietalReputation.Models;
using SocietalReputation.Services;
using System.Numerics;

namespace SocietalReputation.Windows;

public sealed class MainWindow : Window
{
    private const float PassiveRefreshIntervalSeconds = 3f;
    private const int DailyResetHourUtc = 15;
    private const int ConservativeReputationPerQuest = 60;
    private const int NearRankUpThresholdReputation = 240;
    private const string AlertSettingsPopupId = "alert-settings-popup";
    private const string KnownIssuesPopupId = "known-issues-popup";
    private const string PlannedUpdatesPopupId = "planned-updates-popup";
    private static readonly Vector2 FillWidthProgressBarSize = new(-1, 0);

    private readonly Configuration configuration;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ReputationService reputationService;
    private readonly QuestionableAutomationService automationService;
    private readonly AchievementTrackingService achievementTrackingService;

    private ReputationSnapshot? cachedSnapshot;
    private AchievementSnapshot? cachedAchievementSnapshot;
    private RowViewState[] cachedRowStates = [];
    private PlannerViewCache? plannerCache;
    private DateTime lastRawRefreshUtc = DateTime.MinValue;
    private bool rawDataInvalidated = true;
    private bool viewInvalidated = true;
    private bool lastKnownOpenState;
    private int rawDataVersion;
    private int viewDataVersion;
    private bool suppressHeaderSortSync;

    public MainWindow(
        Configuration configuration,
        IDalamudPluginInterface pluginInterface,
        ReputationService reputationService,
        QuestionableAutomationService automationService,
        AchievementTrackingService achievementTrackingService)
        : base("Societal Reputation###SocietalReputationMainWindow")
    {
        this.configuration = configuration;
        this.pluginInterface = pluginInterface;
        this.reputationService = reputationService;
        this.automationService = automationService;
        this.achievementTrackingService = achievementTrackingService;
        this.lastKnownOpenState = configuration.IsMainWindowOpen;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(880, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void PreDraw()
    {
        if (this.lastKnownOpenState == IsOpen)
        {
            return;
        }

        this.lastKnownOpenState = IsOpen;
        this.configuration.IsMainWindowOpen = IsOpen;
        this.configuration.Save(this.pluginInterface);

        if (IsOpen)
        {
            InvalidateRawData();
        }
    }

    public override void Draw()
    {
        EnsurePlannerCache();
        var cache = this.plannerCache;
        var snapshot = this.cachedSnapshot;
        if (cache == null || snapshot == null)
        {
            return;
        }

        var monitor = this.automationService.GetMonitorState();
        DrawModeHeader();
        DrawPlannerControls(cache, monitor);
        DrawOnboardingChecklist(cache, snapshot);
        DrawHeroSection(cache, snapshot, monitor);
        DrawSummarySection(cache, snapshot, monitor);
        DrawTroubleshootingPanel(cache, monitor);
        DrawSocietyTable(cache);
    }

    private void DrawModeHeader()
    {
        var advancedMode = this.configuration.IsAdvancedModeEnabled;
        if (ImGui.Checkbox("Advanced mode", ref advancedMode))
        {
            this.configuration.IsAdvancedModeEnabled = advancedMode;
            SaveConfiguration();
        }

        DrawTooltip("Show advanced planner controls, extra sorting, and troubleshooting details.");

        ImGui.SameLine();
        if (ImGui.SmallButton("Settings##alert-settings"))
        {
            ImGui.OpenPopup(AlertSettingsPopupId);
        }

        DrawTooltip("Open alert and automation settings.");

        ImGui.SameLine();
        if (ImGui.SmallButton("Known issues##known-issues"))
        {
            ImGui.OpenPopup(KnownIssuesPopupId);
        }

        DrawTooltip("View current known issues.");

        ImGui.SameLine();
        if (ImGui.SmallButton("Planned updates##planned-updates"))
        {
            ImGui.OpenPopup(PlannedUpdatesPopupId);
        }

        DrawTooltip("View planned improvements.");
        DrawAlertSettingsPopup();
        DrawKnownIssuesPopup();
        DrawPlannedUpdatesPopup();
    }

    private void DrawPlannerControls(PlannerViewCache cache, AutomationMonitorState monitor)
    {
        var recommendation = cache.RecommendationRow;
        var canRunRecommended = recommendation?.Row.DailyStatus.CanExecuteAction == true;

        if (this.configuration.IsAdvancedModeEnabled)
        {
            var showCompleted = this.configuration.ShowCompletedDailies;
            if (ImGui.Checkbox("Show completed societies", ref showCompleted))
            {
                this.configuration.ShowCompletedDailies = showCompleted;
                SaveConfiguration();
            }

            ImGui.SameLine();
            var onlyActionable = this.configuration.OnlyShowActionableSocieties;
            if (ImGui.Checkbox("Only show societies I can do right now", ref onlyActionable))
            {
                this.configuration.OnlyShowActionableSocieties = onlyActionable;
                SaveConfiguration();
            }

            ImGui.SameLine();
            var activityFilter = this.configuration.PreferredActivityFilter;
            if (DrawEnumCombo("Preferred activity", ref activityFilter))
            {
                this.configuration.PreferredActivityFilter = activityFilter;
                SaveConfiguration();
            }

            ImGui.SameLine();
            var sortMode = this.configuration.SortMode;
            if (DrawEnumCombo("Table order", ref sortMode))
            {
                this.configuration.SortMode = sortMode;
                this.configuration.SortAscending = GetDefaultSortDirection(sortMode);
                this.suppressHeaderSortSync = true;
                SaveConfiguration();
            }

            ImGui.SameLine();
            ImGui.TextDisabled($"Order: {this.configuration.SortMode} {(this.configuration.SortAscending ? "ascending" : "descending")}");
        }

        DrawAutomationHelpHint(cache, monitor);

        if (!canRunRecommended)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Do next recommended quests"))
        {
            if (recommendation != null)
            {
                this.automationService.StartOrContinueDaily(recommendation.Row.Progress.Society);
            }

            this.automationService.InvalidateStatusCache();
            InvalidateRawData();
        }

        if (!canRunRecommended)
        {
            ImGui.EndDisabled();
        }

        if (monitor.IsRunning)
        {
            ImGui.SameLine();
            if (ImGui.Button("Stop quest automation"))
            {
                this.automationService.Stop();
                this.automationService.InvalidateStatusCache();
                InvalidateRawData();
            }
        }
    }

    private void DrawOnboardingChecklist(PlannerViewCache cache, ReputationSnapshot snapshot)
    {
        if (!this.configuration.ShowOnboardingWalkthrough || this.configuration.OnboardingDismissed)
        {
            return;
        }

        var automationAvailable = this.automationService.IsAvailable();
        var unlockedCount = CountRows(this.cachedRowStates, static row => row.Row.Progress.IsUnlocked);
        var hasSomethingToDo = cache.ActionableCount > 0 || snapshot.AcceptedDailyQuests > 0;

        ImGui.TextUnformatted("Getting Started");
        ImGui.Separator();
        DrawChecklistStep("Tracking", "Ready", "Reputation tracking is working for this character.");
        DrawChecklistStep(
            "Automation helper",
            automationAvailable ? "Ready" : "Needs attention",
            automationAvailable
                ? "Quest automation is available if you want help picking up and continuing dailies."
                : "Install or enable Questionable if you want automatic quest pickup. Tracking still works without it.");
        DrawChecklistStep(
            "Societies unlocked",
            unlockedCount > 0 ? "Ready" : "Needs attention",
            unlockedCount > 0
                ? $"{unlockedCount} society row(s) are unlocked and can be tracked."
                : "You have not unlocked any tracked societies on this character yet.");
        DrawChecklistStep(
            "Ready today",
            hasSomethingToDo ? "Ready" : snapshot.RemainingAllowances == 0 ? "Unavailable" : "Needs attention",
            hasSomethingToDo
                ? "You have a clear next step today."
                : snapshot.RemainingAllowances == 0
                    ? "You are out of daily allowances until reset."
                    : "No startable society quests are ready right now.");

        if (ImGui.Button("Hide checklist"))
        {
            this.configuration.ShowOnboardingWalkthrough = false;
            SaveConfiguration();
        }

        ImGui.SameLine();
        if (ImGui.Button("Do not show again"))
        {
            this.configuration.ShowOnboardingWalkthrough = false;
            this.configuration.OnboardingDismissed = true;
            SaveConfiguration();
        }

        ImGui.Separator();
    }

    private static void DrawChecklistStep(string title, string state, string description)
    {
        ImGui.TextUnformatted($"{title}: {state}");
        ImGui.TextDisabled(description);
    }

    private void DrawHeroSection(PlannerViewCache cache, ReputationSnapshot snapshot, AutomationMonitorState monitor)
    {
        ImGui.TextUnformatted("What should I do next?");
        ImGui.Separator();

        if (TryDrawEmptyState(cache, snapshot))
        {
            ImGui.Separator();
            return;
        }

        var recommendationRow = cache.RecommendationRow;
        if (recommendationRow == null)
        {
            ImGui.TextDisabled("Nothing needs attention right now.");
            ImGui.TextDisabled(cache.Recommendation.Reason);
            ImGui.Separator();
            return;
        }

        var progress = recommendationRow.Row.Progress;
        var dailyStatus = recommendationRow.Row.DailyStatus;
        var statusLine = BuildHeroStatusText(recommendationRow);

        ImGui.TextUnformatted(progress.Society.Name);
        ImGui.TextDisabled($"{progress.Society.Expansion} {Bullet()} {progress.Society.Activity}");
        ImGui.TextUnformatted(statusLine);
        ImGui.TextDisabled(cache.Recommendation.Reason);

        if (!progress.IsMaxRank && progress.CurrentRank.MaximumReputation > 0)
        {
            ImGui.ProgressBar(progress.RankProgress, FillWidthProgressBarSize, $"{progress.CurrentReputation:N0} / {progress.CurrentRank.MaximumReputation:N0}");
            DrawTooltip($"{GetRankUpDistance(progress):N0} reputation remaining to rank up.");
        }

        if (dailyStatus.CanExecuteAction)
        {
            if (ImGui.Button($"{recommendationRow.ActionLabelText}##hero-action"))
            {
                this.automationService.StartOrContinueDaily(progress.Society);
                this.automationService.InvalidateStatusCache();
                InvalidateRawData();
            }
        }
        else
        {
            ImGui.BeginDisabled();
            ImGui.Button($"{recommendationRow.ActionLabelText}##hero-action-disabled");
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        ImGui.TextDisabled(monitor.LastMessage);
        DrawTooltip(dailyStatus.StatusMessage);
        ImGui.Separator();
    }

    private bool TryDrawEmptyState(PlannerViewCache cache, ReputationSnapshot snapshot)
    {
        var unlockedCount = CountRows(this.cachedRowStates, static row => row.Row.Progress.IsUnlocked);
        if (unlockedCount == 0)
        {
            ImGui.TextDisabled("You have not unlocked any tracked societies yet.");
            ImGui.TextDisabled("Unlock a society first, then come back here for daily guidance.");
            return true;
        }

        if (snapshot.RemainingAllowances == 0 && snapshot.AcceptedDailyQuests == 0)
        {
            ImGui.TextDisabled("You have used all of today's society allowances.");
            ImGui.TextDisabled($"Come back in {cache.ResetCountdownText} when daily allowances reset.");
            return true;
        }

        if (!this.automationService.IsAvailable())
        {
            ImGui.TextDisabled("Automation requires the Questionable plugin.");
            ImGui.TextDisabled("Tracking still works without it, but automatic quest pickup is unavailable.");
            return true;
        }

        if (cache.RecommendationRow == null)
        {
            ImGui.TextDisabled("No society needs a startable step right now.");
            ImGui.TextDisabled("Try again after reset or switch to advanced mode to review every society.");
            return true;
        }

        return false;
    }

    private void DrawSummarySection(PlannerViewCache cache, ReputationSnapshot snapshot, AutomationMonitorState monitor)
    {
        ImGui.TextUnformatted("Today at a glance");
        ImGui.Separator();
        ImGui.TextUnformatted($"Daily allowances left: {snapshot.RemainingAllowances}/{snapshot.TotalAllowances}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Societies I can do now: {cache.ActionableCount}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Quests already accepted: {snapshot.AcceptedDailyQuests}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Reset in: {cache.ResetCountdownText}");

        var achievementSnapshot = this.cachedAchievementSnapshot;
        if (achievementSnapshot != null)
        {
            ImGui.TextDisabled($"Achievement progress: {achievementSnapshot.CompletedAchievementCount}/{achievementSnapshot.TotalAchievementCount} tracked milestones complete.");
        }

        ImGui.TextDisabled(GetFriendlyAutomationMessage(cache, monitor));
        DrawTooltip("Automation details stay available in troubleshooting and advanced mode.");
        ImGui.Separator();
    }

    private void DrawTroubleshootingPanel(PlannerViewCache cache, AutomationMonitorState monitor)
    {
        if (!this.configuration.IsAdvancedModeEnabled)
        {
            return;
        }

        if (!ImGui.CollapsingHeader("Troubleshooting"))
        {
            return;
        }

        ImGui.TextUnformatted($"Automation status: {GetFriendlyAutomationTitle(cache.AutomationState)}");
        ImGui.TextDisabled(monitor.LastMessage);
        ImGui.TextUnformatted($"Societies I can do now: {cache.ActionableCount}");
        ImGui.TextDisabled($"{cache.InProgressCount} active, {cache.BlockedCount} blocked today, {cache.SetupCount} still need setup.");

        for (var i = 0; i < cache.DiagnosticsRows.Length; i++)
        {
            var row = cache.DiagnosticsRows[i].Row;
            ImGui.BulletText($"{row.Progress.Society.Name}: {row.DailyStatus.StatusMessage}");
        }

        ImGui.Separator();
    }

    private void DrawSocietyTable(PlannerViewCache cache)
    {
        var tableFlags = this.configuration.IsAdvancedModeEnabled
            ? ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortMulti
            : ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable;

        var columnCount = this.configuration.IsAdvancedModeEnabled ? 8 : 5;
        if (!ImGui.BeginTable("societal-reputation-table", columnCount, tableFlags))
        {
            return;
        }

        if (this.configuration.IsAdvancedModeEnabled)
        {
            SetupAdvancedColumns();
            ImGui.TableHeadersRow();
            if (TrySyncSortFromTableHeaders())
            {
                RebuildPlannerView();
                cache = this.plannerCache ?? cache;
            }
        }
        else
        {
            SetupSimpleColumns();
            ImGui.TableHeadersRow();
        }

        for (var i = 0; i < cache.VisibleRows.Length; i++)
        {
            if (this.configuration.IsAdvancedModeEnabled)
            {
                DrawAdvancedRow(cache, cache.VisibleRows[i]);
            }
            else
            {
                DrawSimpleRow(cache, cache.VisibleRows[i]);
            }
        }

        ImGui.EndTable();
    }

    private void SetupSimpleColumns()
    {
        ImGui.TableSetupColumn("Society");
        ImGui.TableSetupColumn("Type");
        ImGui.TableSetupColumn("Progress");
        ImGui.TableSetupColumn("Why this matters");
        ImGui.TableSetupColumn("Next step");
    }

    private void SetupAdvancedColumns()
    {
        ImGui.TableSetupColumn("Society", GetSortableColumnFlags(SortColumnKey.Society), 0, (uint)SortColumnKey.Society);
        ImGui.TableSetupColumn("Activity", GetSortableColumnFlags(SortColumnKey.Activity), 0, (uint)SortColumnKey.Activity);
        ImGui.TableSetupColumn("Rank", GetSortableColumnFlags(SortColumnKey.Rank), 0, (uint)SortColumnKey.Rank);
        ImGui.TableSetupColumn("Reputation", GetSortableColumnFlags(SortColumnKey.Reputation), 0, (uint)SortColumnKey.Reputation);
        ImGui.TableSetupColumn("Progress", GetSortableColumnFlags(SortColumnKey.Progress), 0, (uint)SortColumnKey.Progress);
        ImGui.TableSetupColumn("ETA", GetSortableColumnFlags(SortColumnKey.Eta), 0, (uint)SortColumnKey.Eta);
        ImGui.TableSetupColumn("Dailies", GetSortableColumnFlags(SortColumnKey.Dailies), 0, (uint)SortColumnKey.Dailies);
        ImGui.TableSetupColumn("Automation", ImGuiTableColumnFlags.NoSort, 0, (uint)SortColumnKey.Automation);
    }

    private void DrawSimpleRow(PlannerViewCache cache, RowViewState rowState)
    {
        var progress = rowState.Row.Progress;
        var dailyStatus = rowState.Row.DailyStatus;

        ImGui.TableNextRow();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, rowState.RowColorU32);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(progress.Society.Name);
        ImGui.TextDisabled(progress.Society.Expansion);
        if (ReferenceEquals(cache.RecommendationRow, rowState))
        {
            ImGui.TextDisabled("Best next step");
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(progress.Society.Activity);
        ImGui.TextDisabled(progress.CurrentRank.Name);

        ImGui.TableNextColumn();
        if (!progress.IsUnlocked)
        {
            ImGui.TextDisabled("Locked");
        }
        else if (progress.IsMaxRank)
        {
            ImGui.TextUnformatted("Max rank reached");
        }
        else
        {
            ImGui.TextUnformatted($"{progress.CurrentReputation:N0} / {progress.CurrentRank.MaximumReputation:N0}");
            ImGui.ProgressBar(progress.RankProgress, FillWidthProgressBarSize, rowState.EtaSummary);
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(BuildHeroStatusText(rowState));
        ImGui.TextDisabled(rowState.Row.IsActionable ? dailyStatus.StatusMessage : rowState.BlockedInlineText);
        DrawTooltip(rowState.BlockedTooltip);

        ImGui.TableNextColumn();
        DrawActionButton(rowState, progress.Society);
    }

    private void DrawAdvancedRow(PlannerViewCache cache, RowViewState rowState)
    {
        var progress = rowState.Row.Progress;
        var dailyStatus = rowState.Row.DailyStatus;

        ImGui.TableNextRow();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, rowState.RowColorU32);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(progress.Society.Name);
        ImGui.TextDisabled(progress.Society.Expansion);
        ImGui.TextDisabled(rowState.Row.AchievementStatus.StatusMessage);
        DrawTooltip(rowState.Row.AchievementStatus.StatusMessage);
        if (ReferenceEquals(cache.RecommendationRow, rowState))
        {
            ImGui.TextDisabled("Best next step");
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(progress.Society.Activity);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(progress.CurrentRank.Name);
        if (progress.RankedUpToday)
        {
            ImGui.TextDisabled("Ranked up today");
        }

        ImGui.TableNextColumn();
        if (!progress.IsUnlocked)
        {
            ImGui.TextDisabled("Locked");
        }
        else if (progress.CurrentRank.MaximumReputation == 0)
        {
            ImGui.TextUnformatted("Max");
        }
        else
        {
            ImGui.TextUnformatted($"{progress.CurrentReputation:N0} / {progress.CurrentRank.MaximumReputation:N0}");
            DrawTooltip($"{GetRankUpDistance(progress):N0} reputation to next rank.");
        }

        ImGui.TableNextColumn();
        if (!progress.IsUnlocked)
        {
            ImGui.ProgressBar(0, FillWidthProgressBarSize, "Locked");
        }
        else if (progress.CurrentRank.MaximumReputation == 0)
        {
            ImGui.ProgressBar(1, FillWidthProgressBarSize, "Complete");
        }
        else
        {
            ImGui.ProgressBar(progress.RankProgress, FillWidthProgressBarSize, $"{progress.RankReputationEarned:N0} / {progress.RankReputationRequired:N0}");
            DrawTooltip($"{GetRankUpDistance(progress):N0} reputation remaining to rank up.");
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(rowState.EtaSummary);
        ImGui.TextDisabled(rowState.EtaDetail);
        DrawTooltip(rowState.EtaTooltip);

        ImGui.TableNextColumn();
        if (!progress.HasDailyQuestSupport)
        {
            ImGui.TextDisabled("No dailies");
        }
        else
        {
            ImGui.TextUnformatted($"{dailyStatus.AcceptedQuestCount}/{progress.DailyQuestAllowanceTotal} accepted");
            ImGui.TextDisabled(rowState.DailyBreakdownText);
        }

        ImGui.TableNextColumn();
        DrawActionButton(rowState, progress.Society);
        ImGui.TextDisabled(dailyStatus.StatusMessage);
        DrawTooltip(dailyStatus.StatusMessage);
        if (!rowState.Row.IsActionable && !progress.IsMaxRank)
        {
            ImGui.TextDisabled(rowState.BlockedInlineText);
            DrawTooltip(rowState.BlockedTooltip);
        }
    }

    private void DrawActionButton(RowViewState rowState, SocietyInfo society)
    {
        if (!rowState.CanStartDaily)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button(rowState.ButtonLabel))
        {
            this.automationService.StartOrContinueDaily(society);
            this.automationService.InvalidateStatusCache();
            InvalidateRawData();
        }

        if (!rowState.CanStartDaily)
        {
            ImGui.EndDisabled();
        }
    }

    private void EnsurePlannerCache()
    {
        var shouldRefreshRawData = this.rawDataInvalidated
            || this.cachedSnapshot == null
            || (DateTime.UtcNow - this.lastRawRefreshUtc).TotalSeconds >= PassiveRefreshIntervalSeconds;

        if (shouldRefreshRawData)
        {
            RefreshRawData();
        }

        if (this.viewInvalidated || this.plannerCache == null)
        {
            RebuildPlannerView();
        }
    }

    private void RefreshRawData()
    {
        var snapshot = this.reputationService.GetSnapshot();
        var progressCount = snapshot.Progress.Count;
        var societies = new SocietyInfo[progressCount];
        for (var i = 0; i < progressCount; i++)
        {
            societies[i] = snapshot.Progress[i].Society;
        }

        var achievementSnapshot = this.achievementTrackingService.GetSnapshot(societies);
        var dailyStatuses = this.automationService.GetDailyQuestStatuses(societies);
        var rowStates = new RowViewState[progressCount];
        for (var i = 0; i < progressCount; i++)
        {
            var progress = snapshot.Progress[i];
            var row = new SocietyPlannerRow(
                progress,
                dailyStatuses[progress.Society.Id],
                achievementSnapshot.GetStatus(progress.Society.Id));
            rowStates[i] = BuildRowState(row);
        }

        this.cachedSnapshot = snapshot;
        this.cachedAchievementSnapshot = achievementSnapshot;
        this.cachedRowStates = rowStates;
        this.lastRawRefreshUtc = DateTime.UtcNow;
        this.rawDataInvalidated = false;
        this.viewInvalidated = true;
        this.rawDataVersion++;
    }

    private void RebuildPlannerView()
    {
        var recommendationRow = GetRecommendedRow(this.cachedRowStates);
        var recommendation = BuildRecommendation(recommendationRow);
        var visibleRows = BuildVisibleRows(this.cachedRowStates);
        var diagnosticsRows = BuildDiagnosticsRows(this.cachedRowStates);

        this.plannerCache = new PlannerViewCache(
            visibleRows,
            diagnosticsRows,
            recommendation,
            recommendationRow,
            CountRows(this.cachedRowStates, static row => row.Row.IsActionable),
            CountRows(this.cachedRowStates, static row => row.Row.DailyStatus.Readiness == DailyQuestReadiness.InProgress),
            CountRows(this.cachedRowStates, static row => row.Row.DailyStatus.NeedsSetup),
            CountRows(this.cachedRowStates, static row => row.Row.DailyStatus.Readiness == DailyQuestReadiness.LockedOrUnavailable),
            CountRows(this.cachedRowStates, static row => row.Row.Progress.IsMaxRank),
            CountRows(this.cachedRowStates, static row => row.IsNearRankUp),
            CountRows(this.cachedRowStates, static row => row.Row.Progress.IsUnlocked),
            BuildResetCountdownText(DateTime.UtcNow),
            BuildAutomationState(),
            this.rawDataVersion,
            ++this.viewDataVersion);
        this.viewInvalidated = false;
    }

    private RowViewState[] BuildVisibleRows(RowViewState[] rows)
    {
        var visibleRows = new List<RowViewState>(rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (!this.configuration.ShowCompletedDailies && row.Row.Progress.IsMaxRank)
            {
                continue;
            }

            if (!this.configuration.IsAdvancedModeEnabled && row.Row.Progress.IsMaxRank)
            {
                continue;
            }

            if (this.configuration.IsAdvancedModeEnabled && this.configuration.OnlyShowActionableSocieties && !row.Row.IsActionable)
            {
                continue;
            }

            if (this.configuration.IsAdvancedModeEnabled
                && !MatchesActivityFilter(row.Row.Progress.Society.Activity, this.configuration.PreferredActivityFilter))
            {
                continue;
            }

            visibleRows.Add(row);
        }

        visibleRows.Sort(GetVisibleRowComparer());
        return [.. visibleRows];
    }

    private static RowViewState[] BuildDiagnosticsRows(RowViewState[] rows)
    {
        var diagnosticsRows = new List<RowViewState>(rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (row.Row.IsActionable || row.Row.Progress.IsMaxRank)
            {
                continue;
            }

            diagnosticsRows.Add(row);
        }

        diagnosticsRows.Sort(static (left, right) =>
        {
            var priorityComparison = left.DiagnosticPriority.CompareTo(right.DiagnosticPriority);
            return priorityComparison != 0
                ? priorityComparison
                : StringComparer.Ordinal.Compare(left.Row.Progress.Society.Name, right.Row.Progress.Society.Name);
        });
        return [.. diagnosticsRows];
    }

    private RowViewState? GetRecommendedRow(RowViewState[] rows)
    {
        RowViewState? bestRow = null;
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (!row.Row.Progress.IsUnlocked || row.Row.Progress.IsMaxRank)
            {
                continue;
            }

            if (this.configuration.IsAdvancedModeEnabled
                && !MatchesActivityFilter(row.Row.Progress.Society.Activity, this.configuration.PreferredActivityFilter))
            {
                continue;
            }

            if (bestRow == null || CompareRecommendedRows(row, bestRow) < 0)
            {
                bestRow = row;
            }
        }

        return bestRow;
    }

    private static PlannerRecommendation BuildRecommendation(RowViewState? recommendedRow)
    {
        if (recommendedRow == null)
        {
            return new PlannerRecommendation(
                null,
                "Nothing needs your attention right now.",
                "Try again after reset or switch to advanced mode to review every society.");
        }

        var progress = recommendedRow.Row.Progress;
        var reason = recommendedRow.Row.IsActionable
            ? recommendedRow.Row.DailyStatus.StatusMessage
            : progress.IsMaxRank
                ? "Already at max rank."
                : "This is still your closest active goal.";
        var rankRemaining = progress.CurrentRank.MaximumReputation == 0
            ? 0
            : Math.Max(0, progress.CurrentRank.MaximumReputation - progress.CurrentReputation);
        var eta = BuildEtaInfo(progress, recommendedRow.Row.DailyStatus);
        var etaText = eta.Kind switch
        {
            EtaKind.Completed => "Completed.",
            EtaKind.Projected => $"{eta.StatusPhrase} Target: {eta.EstimatedCompletionUtc:ddd, MMM d}.",
            _ => "No projection available.",
        };

        return new PlannerRecommendation(
            recommendedRow.Row,
            $"Next recommended society: {progress.Society.Name}",
            rankRemaining > 0
                ? $"{reason} {rankRemaining:N0} reputation to the next rank. {etaText}"
                : reason);
    }

    private string BuildAutomationState()
    {
        if (!this.automationService.IsAvailable())
        {
            return "Questionable: unavailable";
        }

        return this.automationService.IsRunning()
            ? "Questionable: running"
            : "Questionable: ready";
    }

    private static RowViewState BuildRowState(SocietyPlannerRow row)
    {
        var dailyStatus = row.DailyStatus;
        var progress = row.Progress;
        var eta = BuildEtaInfo(progress, dailyStatus);
        var visualState = GetVisualState(row);
        var blockedInlineText = BuildBlockedInlineText(row);
        var blockedTooltip = BuildBlockedTooltip(row);
        return new RowViewState(
            row,
            GetRecommendationScore(row),
            GetDiagnosticPriority(row),
            dailyStatus.CanExecuteAction,
            BuildDailyBreakdownText(dailyStatus),
            $"{dailyStatus.RecommendedActionLabel}###start-daily-{(byte)progress.Society.Id}",
            dailyStatus.RecommendedActionLabel,
            BuildRowColor(visualState),
            BuildEtaSummaryText(eta),
            BuildEtaDetailText(eta),
            BuildEtaTooltipText(eta),
            GetRankUpDistance(progress) > 0 && GetRankUpDistance(progress) <= NearRankUpThresholdReputation,
            blockedInlineText,
            blockedTooltip);
    }

    private static string BuildBlockedInlineText(SocietyPlannerRow row)
    {
        if (row.IsActionable || row.Progress.IsMaxRank)
        {
            return string.Empty;
        }

        return row.DailyStatus.Readiness switch
        {
            DailyQuestReadiness.Unconfigured => "Set up automation for this society first.",
            DailyQuestReadiness.Unavailable => "Automation helper is not available right now.",
            DailyQuestReadiness.LockedOrUnavailable when !row.Progress.IsUnlocked => "Unlock this society first.",
            DailyQuestReadiness.LockedOrUnavailable => "This society's quests are locked or unavailable right now.",
            DailyQuestReadiness.PickupPending => "Waiting for remaining quests to appear.",
            DailyQuestReadiness.NoneAvailable => "No daily quests are available until reset.",
            _ => "This society cannot be started right now.",
        };
    }

    private static string BuildBlockedTooltip(SocietyPlannerRow row)
    {
        if (row.IsActionable || row.Progress.IsMaxRank)
        {
            return string.Empty;
        }

        return row.DailyStatus.Readiness switch
        {
            DailyQuestReadiness.Unconfigured => "Complete the automation setup for this society if you want automatic quest pickup.",
            DailyQuestReadiness.Unavailable => "Questionable is unavailable. Tracking still works, but automation cannot start quests right now.",
            DailyQuestReadiness.LockedOrUnavailable when !row.Progress.IsUnlocked => "This society is still locked for this character.",
            DailyQuestReadiness.LockedOrUnavailable => "The quest chain is locked or temporarily unavailable. Try again later or after checking prerequisites.",
            DailyQuestReadiness.PickupPending => row.DailyStatus.StatusMessage,
            DailyQuestReadiness.NoneAvailable => "No obtainable daily quests are available for this society right now.",
            _ => row.DailyStatus.StatusMessage,
        };
    }

    private static string BuildDailyBreakdownText(DailyQuestStatus dailyStatus)
    {
        return dailyStatus.Readiness switch
        {
            DailyQuestReadiness.PickupPending => $"{dailyStatus.AcceptedQuestCount} accepted, {dailyStatus.ReadyQuestCount} still ready to appear",
            DailyQuestReadiness.ReadyToTurnIn => dailyStatus.ReadyQuestCount > 0
                ? $"{dailyStatus.CompletedQuestCount} complete, more pickups still appearing"
                : $"{dailyStatus.CompletedQuestCount} complete, ready to hand in",
            DailyQuestReadiness.InProgress => dailyStatus.CompletedQuestCount > 0
                ? $"{dailyStatus.CompletedQuestCount} complete, keep going"
                : "Finish the remaining objectives",
            _ => $"{dailyStatus.CompletedQuestCount} completed, {dailyStatus.ReadyQuestCount} ready, {dailyStatus.BlockedQuestCount} blocked",
        };
    }

    private static string BuildHeroStatusText(RowViewState row)
    {
        var dailyStatus = row.Row.DailyStatus;
        return dailyStatus.Readiness switch
        {
            DailyQuestReadiness.Ready => $"{dailyStatus.ReadyQuestCount} quest(s) are ready to start.",
            DailyQuestReadiness.PickupPending => "Finish picking up the remaining quests first.",
            DailyQuestReadiness.InProgress => "You already have quests in progress here.",
            DailyQuestReadiness.ReadyToTurnIn => "Your accepted quests are ready to hand in.",
            DailyQuestReadiness.NoneAvailable => "No daily quests are available right now.",
            DailyQuestReadiness.Unavailable => "Automation is unavailable, but tracking still works.",
            DailyQuestReadiness.Unconfigured => "Automation setup is still needed for this society.",
            DailyQuestReadiness.LockedOrUnavailable => row.BlockedInlineText,
            _ => row.Row.DailyStatus.StatusMessage,
        };
    }

    private static int GetRecommendationScore(SocietyPlannerRow row)
    {
        var score = 0;
        if (row.IsActionable)
        {
            score += 1000;
        }

        score += row.DailyStatus.Readiness switch
        {
            DailyQuestReadiness.Ready => 250,
            DailyQuestReadiness.InProgress => 200,
            DailyQuestReadiness.PickupPending => 125,
            DailyQuestReadiness.LockedOrUnavailable => -100,
            DailyQuestReadiness.Unavailable => -250,
            DailyQuestReadiness.Unconfigured => -350,
            _ => 0,
        };
        score += row.DailyStatus.CompletedQuestCount * 150;
        score += row.DailyStatus.AcceptedQuestCount * 100;
        score += row.DailyStatus.ReadyQuestCount * 75;
        score -= row.DailyStatus.BlockedQuestCount * 20;
        score += row.Progress.RankedUpToday ? -25 : 25;
        score += row.Progress.IsUnlocked ? 25 : -500;
        score += row.Progress.IsMaxRank ? -1000 : 0;
        score -= row.Progress.CurrentRank.MaximumReputation == 0
            ? 0
            : Math.Max(0, row.Progress.CurrentRank.MaximumReputation - row.Progress.CurrentReputation) / 10;
        return score;
    }

    private static int GetDiagnosticPriority(SocietyPlannerRow row)
    {
        return row.DailyStatus.Readiness switch
        {
            DailyQuestReadiness.Unavailable => 0,
            DailyQuestReadiness.Unconfigured => 1,
            DailyQuestReadiness.LockedOrUnavailable => 2,
            DailyQuestReadiness.NoneAvailable => 3,
            DailyQuestReadiness.PickupPending => 4,
            DailyQuestReadiness.InProgress => 5,
            DailyQuestReadiness.Ready => 6,
            _ => 6,
        };
    }

    private static bool MatchesActivityFilter(string activity, ActivityFilter filter)
    {
        return filter switch
        {
            ActivityFilter.Combat => activity.Contains("Combat", StringComparison.OrdinalIgnoreCase),
            ActivityFilter.Crafting => activity.Contains("Crafting", StringComparison.OrdinalIgnoreCase),
            ActivityFilter.Gathering => activity.Contains("Gathering", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    private static int CountRows(RowViewState[] rows, Func<RowViewState, bool> predicate)
    {
        var count = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            if (predicate(rows[i]))
            {
                count++;
            }
        }

        return count;
    }

    private Comparison<RowViewState> GetVisibleRowComparer()
    {
        Comparison<RowViewState> baseComparer = this.configuration.SortMode switch
        {
            SocietySortMode.Name => static (left, right) =>
                StringComparer.Ordinal.Compare(left.Row.Progress.Society.Name, right.Row.Progress.Society.Name),
            SocietySortMode.Expansion => static (left, right) =>
            {
                var expansionComparison = StringComparer.Ordinal.Compare(left.Row.Progress.Society.Expansion, right.Row.Progress.Society.Expansion);
                return expansionComparison != 0
                    ? expansionComparison
                    : StringComparer.Ordinal.Compare(left.Row.Progress.Society.Name, right.Row.Progress.Society.Name);
            },
            SocietySortMode.ClosestToRankUp => static (left, right) =>
            {
                var maxRankComparison = left.Row.Progress.IsMaxRank.CompareTo(right.Row.Progress.IsMaxRank);
                if (maxRankComparison != 0)
                {
                    return maxRankComparison;
                }

                var distanceComparison = GetRankUpDistance(left.Row.Progress).CompareTo(GetRankUpDistance(right.Row.Progress));
                if (distanceComparison != 0)
                {
                    return distanceComparison;
                }

                var actionableComparison = right.Row.IsActionable.CompareTo(left.Row.IsActionable);
                return actionableComparison != 0
                    ? actionableComparison
                    : StringComparer.Ordinal.Compare(left.Row.Progress.Society.Name, right.Row.Progress.Society.Name);
            },
            _ => static (left, right) => CompareRecommendedRows(left, right),
        };
        return this.configuration.SortAscending
            ? baseComparer
            : (left, right) => baseComparer(right, left);
    }

    private unsafe bool TrySyncSortFromTableHeaders()
    {
        if (this.suppressHeaderSortSync)
        {
            this.suppressHeaderSortSync = false;
            return false;
        }

        var sortSpecs = ImGui.TableGetSortSpecs();
        if (sortSpecs.IsNull || !sortSpecs.SpecsDirty || sortSpecs.SpecsCount == 0)
        {
            return false;
        }

        var primary = sortSpecs.Specs[0];
        var mappedMode = MapSortModeFromColumn((SortColumnKey)primary.ColumnUserID);
        var ascending = primary.SortDirection != ImGuiSortDirection.Descending;
        var changed = mappedMode != this.configuration.SortMode || ascending != this.configuration.SortAscending;
        if (changed)
        {
            this.configuration.SortMode = mappedMode;
            this.configuration.SortAscending = ascending;
            SaveConfiguration();
        }

        sortSpecs.SpecsDirty = false;
        return changed;
    }

    private ImGuiTableColumnFlags GetSortableColumnFlags(SortColumnKey column)
    {
        var flags = ImGuiTableColumnFlags.None;
        if (GetColumnForSortMode(this.configuration.SortMode) == column)
        {
            flags |= ImGuiTableColumnFlags.DefaultSort;
            if (!this.configuration.SortAscending)
            {
                flags |= ImGuiTableColumnFlags.PreferSortDescending;
            }
        }

        return flags;
    }

    private static SocietySortMode MapSortModeFromColumn(SortColumnKey column)
    {
        return column switch
        {
            SortColumnKey.Society => SocietySortMode.Name,
            SortColumnKey.Activity => SocietySortMode.Name,
            SortColumnKey.Rank => SocietySortMode.ClosestToRankUp,
            SortColumnKey.Reputation => SocietySortMode.ClosestToRankUp,
            SortColumnKey.Progress => SocietySortMode.Recommended,
            SortColumnKey.Eta => SocietySortMode.ClosestToRankUp,
            SortColumnKey.Dailies => SocietySortMode.Recommended,
            _ => SocietySortMode.Recommended,
        };
    }

    private static SortColumnKey GetColumnForSortMode(SocietySortMode mode)
    {
        return mode switch
        {
            SocietySortMode.Name => SortColumnKey.Society,
            SocietySortMode.Expansion => SortColumnKey.Activity,
            SocietySortMode.ClosestToRankUp => SortColumnKey.Rank,
            _ => SortColumnKey.Progress,
        };
    }

    private static bool GetDefaultSortDirection(SocietySortMode sortMode)
    {
        return sortMode switch
        {
            SocietySortMode.Recommended => false,
            _ => true,
        };
    }

    private static int CompareRecommendedRows(RowViewState left, RowViewState right)
    {
        var scoreComparison = right.RecommendationScore.CompareTo(left.RecommendationScore);
        if (scoreComparison != 0)
        {
            return scoreComparison;
        }

        var distanceComparison = GetRankUpDistance(left.Row.Progress).CompareTo(GetRankUpDistance(right.Row.Progress));
        if (distanceComparison != 0)
        {
            return distanceComparison;
        }

        return StringComparer.Ordinal.Compare(left.Row.Progress.Society.Name, right.Row.Progress.Society.Name);
    }

    private static int GetRankUpDistance(SocietyProgress progress)
    {
        return progress.CurrentRank.MaximumReputation == 0
            ? int.MaxValue
            : progress.CurrentRank.MaximumReputation - progress.CurrentReputation;
    }

    private static EtaInfo BuildEtaInfo(SocietyProgress progress, DailyQuestStatus dailyStatus)
    {
        if (progress.IsMaxRank)
        {
            return new EtaInfo(
                int.MaxValue,
                DateTime.UtcNow,
                "No further rank progress is needed for this society.",
                EtaKind.Completed,
                "Completed",
                0,
                0);
        }

        if (!progress.IsUnlocked || progress.CurrentRank.MaximumReputation == 0 || !progress.HasDailyQuestSupport)
        {
            return new EtaInfo(
                int.MaxValue,
                DateTime.UtcNow,
                "No ETA available.",
                EtaKind.Unavailable,
                "No projection",
                0,
                0);
        }

        var remaining = Math.Max(0, GetRankUpDistance(progress));
        var estimatedRepPerReset = GetConservativeReputationPerReset(progress, dailyStatus);
        if (estimatedRepPerReset <= 0)
        {
            return new EtaInfo(
                int.MaxValue,
                DateTime.UtcNow,
                "No daily quest pace available.",
                EtaKind.Unavailable,
                "No projection",
                0,
                0);
        }

        var days = (remaining + estimatedRepPerReset - 1) / estimatedRepPerReset;
        var completion = GetNextDailyResetUtc(DateTime.UtcNow).AddDays(Math.Max(0, days - 1));
        var repPerWeek = estimatedRepPerReset * 7;
        var ranksPerWeek = progress.RankReputationRequired <= 0
            ? 0
            : (double)repPerWeek / progress.RankReputationRequired;
        var statusPhrase = days <= 1
            ? "You'll rank up tomorrow."
            : $"~{days} days to rank up.";

        return new EtaInfo(
            days,
            completion,
            $"{remaining:N0} rep remaining at ~{estimatedRepPerReset:N0} rep/day. Est. completion {completion:ddd, MMM d}. Rep/week {repPerWeek:N0}. Ranks/week {ranksPerWeek:0.00}.",
            EtaKind.Projected,
            statusPhrase,
            repPerWeek,
            ranksPerWeek);
    }

    private static int GetConservativeReputationPerReset(SocietyProgress progress, DailyQuestStatus dailyStatus)
    {
        if (!progress.HasDailyQuestSupport || progress.IsMaxRank || !progress.IsUnlocked)
        {
            return 0;
        }

        var availableSlots = Math.Max(0, progress.DailyQuestAllowanceTotal - dailyStatus.AcceptedQuestCount);
        var projectedQuestCount = Math.Max(1, Math.Min(progress.DailyQuestAllowanceTotal, availableSlots + dailyStatus.CompletedQuestCount));
        return projectedQuestCount * ConservativeReputationPerQuest;
    }

    private static DateTime GetNextDailyResetUtc(DateTime utcNow)
    {
        var resetToday = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, DailyResetHourUtc, 0, 0, DateTimeKind.Utc);
        return utcNow < resetToday
            ? resetToday
            : resetToday.AddDays(1);
    }

    private static string BuildResetCountdownText(DateTime utcNow)
    {
        var nextReset = GetNextDailyResetUtc(utcNow);
        var remaining = nextReset - utcNow;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        return $"{(int)remaining.TotalHours}h {remaining.Minutes:D2}m";
    }

    private static RowVisualState GetVisualState(SocietyPlannerRow row)
    {
        var progress = row.Progress;
        if (!progress.IsUnlocked || progress.IsMaxRank)
        {
            return RowVisualState.Neutral;
        }

        return row.DailyStatus.Readiness switch
        {
            DailyQuestReadiness.ReadyToTurnIn => RowVisualState.Ready,
            DailyQuestReadiness.PickupPending => RowVisualState.Neutral,
            DailyQuestReadiness.InProgress => RowVisualState.InProgress,
            DailyQuestReadiness.LockedOrUnavailable or DailyQuestReadiness.Unavailable or DailyQuestReadiness.Unconfigured => RowVisualState.Blocked,
            _ => RowVisualState.Neutral,
        };
    }

    private static uint BuildRowColor(RowVisualState state)
    {
        var color = state switch
        {
            RowVisualState.Ready => new Vector4(0.10f, 0.28f, 0.12f, 0.24f),
            RowVisualState.InProgress => new Vector4(0.32f, 0.26f, 0.08f, 0.22f),
            RowVisualState.Blocked => new Vector4(0.35f, 0.08f, 0.08f, 0.20f),
            _ => new Vector4(0.18f, 0.18f, 0.18f, 0.15f),
        };
        return ImGui.GetColorU32(color);
    }

    private void DrawAlertSettingsPopup()
    {
        if (!ImGui.BeginPopup(AlertSettingsPopupId))
        {
            return;
        }

        ImGui.TextUnformatted("Alerts and automation settings");
        ImGui.Separator();

        var enableToastAlerts = this.configuration.EnableToastAlerts;
        if (ImGui.Checkbox("Show toast alerts", ref enableToastAlerts))
        {
            this.configuration.EnableToastAlerts = enableToastAlerts;
            SaveConfiguration();
        }

        var enableChatAlerts = this.configuration.EnableChatAlerts;
        if (ImGui.Checkbox("Write alerts to chat", ref enableChatAlerts))
        {
            this.configuration.EnableChatAlerts = enableChatAlerts;
            SaveConfiguration();
        }

        var notifyDailyReset = this.configuration.NotifyDailyReset;
        if (ImGui.Checkbox("Alert when allowances reset", ref notifyDailyReset))
        {
            this.configuration.NotifyDailyReset = notifyDailyReset;
            SaveConfiguration();
        }

        var notifySocietyUnlocked = this.configuration.NotifySocietyUnlocked;
        if (ImGui.Checkbox("Alert when a society unlocks", ref notifySocietyUnlocked))
        {
            this.configuration.NotifySocietyUnlocked = notifySocietyUnlocked;
            SaveConfiguration();
        }

        var notifyRankUpAvailable = this.configuration.NotifyRankUpAvailable;
        if (ImGui.Checkbox("Alert when a rank-up is ready", ref notifyRankUpAvailable))
        {
            this.configuration.NotifyRankUpAvailable = notifyRankUpAvailable;
            SaveConfiguration();
        }

        var notifyAutomationStalled = this.configuration.NotifyAutomationStalled;
        if (ImGui.Checkbox("Alert when automation stops making progress", ref notifyAutomationStalled))
        {
            this.configuration.NotifyAutomationStalled = notifyAutomationStalled;
            SaveConfiguration();
        }

        var notifyPrerequisiteMet = this.configuration.NotifyPrerequisiteMet;
        if (ImGui.Checkbox("Alert when a blocked society becomes available", ref notifyPrerequisiteMet))
        {
            this.configuration.NotifyPrerequisiteMet = notifyPrerequisiteMet;
            SaveConfiguration();
        }

        ImGui.EndPopup();
    }

    private void DrawKnownIssuesPopup()
    {
        if (!ImGui.BeginPopup(KnownIssuesPopupId))
        {
            return;
        }

        ImGui.TextUnformatted("Known issues");
        ImGui.Separator();

        ImGui.BulletText("Accepts and Completes Quests 1 at a time.");
        

        ImGui.EndPopup();
    }

    private void DrawPlannedUpdatesPopup()
    {
        if (!ImGui.BeginPopup(PlannedUpdatesPopupId))
        {
            return;
        }

        ImGui.TextUnformatted("Planned updates");
        ImGui.Separator();

        ImGui.BulletText("Questionable dependency to be removed");
        

        ImGui.EndPopup();
    }

    private static void DrawTooltip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !string.IsNullOrWhiteSpace(text))
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(text);
            ImGui.EndTooltip();
        }
    }

    private void DrawAutomationHelpHint(PlannerViewCache cache, AutomationMonitorState monitor)
    {
        ImGui.TextDisabled(GetFriendlyAutomationMessage(cache, monitor));
        DrawTooltip("Advanced mode and troubleshooting include more technical automation details.");
    }

    private static string GetFriendlyAutomationTitle(string automationState)
    {
        return automationState switch
        {
            "Questionable: unavailable" => "Automation helper unavailable",
            "Questionable: running" => "Quest automation running",
            _ => "Automation ready",
        };
    }

    private static string GetFriendlyAutomationMessage(PlannerViewCache cache, AutomationMonitorState monitor)
    {
        if (monitor.IsRunning)
        {
            return "Quest automation is currently working on your selected society.";
        }

        return cache.AutomationState switch
        {
            "Questionable: unavailable" => "Automation requires Questionable. Tracking still works without it.",
            _ => "Automation is ready when you want help starting or continuing quests.",
        };
    }

    private static string BuildEtaSummaryText(EtaInfo eta)
    {
        return eta.Kind switch
        {
            EtaKind.Completed => "Completed",
            EtaKind.Projected when eta.EstimatedResets <= 1 => "You'll rank up tomorrow",
            EtaKind.Projected => $"~{eta.EstimatedResets} days",
            _ => "No ETA",
        };
    }

    private static string BuildEtaDetailText(EtaInfo eta)
    {
        return eta.Kind switch
        {
            EtaKind.Completed => "Max rank reached",
            EtaKind.Projected => $"Est. {eta.EstimatedCompletionUtc:ddd, MMM d}",
            _ => "No daily projection",
        };
    }

    private static string BuildEtaTooltipText(EtaInfo eta)
    {
        return eta.Kind switch
        {
            EtaKind.Projected => $"{eta.DetailText}\nWeekly pace: {eta.ProjectedReputationPerWeek:N0} rep/week, {eta.ProjectedRanksPerWeek:0.00} ranks/week.",
            _ => eta.DetailText,
        };
    }

    private bool DrawEnumCombo<TEnum>(string label, ref TEnum selected)
        where TEnum : struct, Enum
    {
        var changed = false;
        if (ImGui.BeginCombo(label, selected.ToString()))
        {
            foreach (var value in EnumValueCache<TEnum>.Values)
            {
                var isSelected = EqualityComparer<TEnum>.Default.Equals(selected, value);
                if (ImGui.Selectable(value.ToString(), isSelected))
                {
                    selected = value;
                    changed = true;
                }

                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        return changed;
    }

    private void SaveConfiguration()
    {
        this.configuration.Save(this.pluginInterface);
        InvalidateView();
    }

    private void InvalidateRawData()
    {
        this.rawDataInvalidated = true;
        this.viewInvalidated = true;
    }

    private void InvalidateView()
    {
        this.viewInvalidated = true;
    }

    private static string Bullet()
    {
        return "/";
    }

    private sealed record RowViewState(
        SocietyPlannerRow Row,
        int RecommendationScore,
        int DiagnosticPriority,
        bool CanStartDaily,
        string DailyBreakdownText,
        string ButtonLabel,
        string ActionLabelText,
        uint RowColorU32,
        string EtaSummary,
        string EtaDetail,
        string EtaTooltip,
        bool IsNearRankUp,
        string BlockedInlineText,
        string BlockedTooltip);

    private sealed record PlannerViewCache(
        RowViewState[] VisibleRows,
        RowViewState[] DiagnosticsRows,
        PlannerRecommendation Recommendation,
        RowViewState? RecommendationRow,
        int ActionableCount,
        int InProgressCount,
        int SetupCount,
        int BlockedCount,
        int MaxedCount,
        int NearRankUpCount,
        int UnlockedCount,
        string ResetCountdownText,
        string AutomationState,
        int RawDataVersion,
        int ViewDataVersion);

    private sealed record EtaInfo(
        int EstimatedResets,
        DateTime EstimatedCompletionUtc,
        string DetailText,
        EtaKind Kind,
        string StatusPhrase,
        int ProjectedReputationPerWeek,
        double ProjectedRanksPerWeek);

    private enum EtaKind
    {
        Unavailable,
        Projected,
        Completed,
    }

    private enum RowVisualState
    {
        Neutral,
        Ready,
        InProgress,
        Blocked,
    }

    private enum SortColumnKey : uint
    {
        Society = 1,
        Activity = 2,
        Rank = 3,
        Reputation = 4,
        Progress = 5,
        Eta = 6,
        Dailies = 7,
        Automation = 8,
    }

    private static class EnumValueCache<TEnum>
        where TEnum : struct, Enum
    {
        public static readonly TEnum[] Values = Enum.GetValues<TEnum>();
    }
}
