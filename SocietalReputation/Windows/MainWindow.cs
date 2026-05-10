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
    private static readonly Vector2 FillWidthProgressBarSize = new(-1, 0);

    private readonly Configuration configuration;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ReputationService reputationService;
    private readonly QuestionableAutomationService automationService;
    private readonly AchievementTrackingService achievementTrackingService;

    private string automationStatus = "Automation uses Questionable IPC when available.";
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
        if (cache == null)
        {
            return;
        }

        DrawPlannerControls(cache);
        DrawPlannerSummary(cache);
        DrawDiagnosticsPanel(cache);

        var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortMulti;
        if (!ImGui.BeginTable("societal-reputation-table", 8, tableFlags))
        {
            return;
        }

        SetupSortableColumns();
        ImGui.TableHeadersRow();
        if (TrySyncSortFromTableHeaders())
        {
            RebuildPlannerView();
            cache = this.plannerCache;
            if (cache == null)
            {
                ImGui.EndTable();
                return;
            }
        }

        for (var i = 0; i < cache.VisibleRows.Length; i++)
        {
            DrawRow(cache, cache.VisibleRows[i]);
        }

        ImGui.EndTable();
    }

    private void DrawPlannerControls(PlannerViewCache cache)
    {
        var showCompleted = this.configuration.ShowCompletedDailies;
        if (ImGui.Checkbox("Show completed dailies", ref showCompleted))
        {
            this.configuration.ShowCompletedDailies = showCompleted;
            SaveConfiguration();
        }

        ImGui.SameLine();
        var onlyActionable = this.configuration.OnlyShowActionableSocieties;
        if (ImGui.Checkbox("Only actionable", ref onlyActionable))
        {
            this.configuration.OnlyShowActionableSocieties = onlyActionable;
            SaveConfiguration();
        }

        ImGui.SameLine();
        var activityFilter = this.configuration.PreferredActivityFilter;
        if (DrawEnumCombo("Focus", ref activityFilter))
        {
            this.configuration.PreferredActivityFilter = activityFilter;
            SaveConfiguration();
        }

        ImGui.SameLine();
        var sortMode = this.configuration.SortMode;
        if (DrawEnumCombo("Sort", ref sortMode))
        {
            this.configuration.SortMode = sortMode;
            this.configuration.SortAscending = GetDefaultSortDirection(sortMode);
            this.suppressHeaderSortSync = true;
            SaveConfiguration();
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"Sort: {this.configuration.SortMode} {(this.configuration.SortAscending ? "↑" : "↓")}");

        if (ImGui.Button("Start recommended daily"))
        {
            var recommendation = cache.RecommendationRow;
            this.automationStatus = recommendation == null
                ? "No recommended daily is currently startable."
                : this.automationService.AcceptAllAvailableDailies(recommendation.Row.Progress.Society).Message;
            this.automationService.InvalidateStatusCache();
            InvalidateRawData();
        }

        ImGui.SameLine();
        if (ImGui.Button("Start visible dailies"))
        {
            var visibleSocieties = new List<SocietyInfo>(cache.VisibleRows.Length);
            for (var i = 0; i < cache.VisibleRows.Length; i++)
            {
                var rowState = cache.VisibleRows[i];
                if (rowState.Row.IsActionable)
                {
                    visibleSocieties.Add(rowState.Row.Progress.Society);
                }
            }

            this.automationStatus = this.automationService.StartFirstAvailable(visibleSocieties, "Visible list").Message;
            this.automationService.InvalidateStatusCache();
            InvalidateRawData();
        }

        ImGui.SameLine();
        if (ImGui.Button("Stop automation"))
        {
            this.automationStatus = this.automationService.Stop().Message;
            this.automationService.InvalidateStatusCache();
            InvalidateRawData();
        }
    }

    private void DrawPlannerSummary(PlannerViewCache cache)
    {
        var snapshot = this.cachedSnapshot;
        if (snapshot == null)
        {
            return;
        }

        ImGui.TextDisabled(cache.AutomationState);
        ImGui.TextUnformatted($"Daily reset in: {cache.ResetCountdownText}");
        ImGui.TextUnformatted($"Tribal allowances: {snapshot.RemainingAllowances}/{snapshot.TotalAllowances} remaining");
        ImGui.TextDisabled($"{snapshot.AcceptedDailyQuests} accepted daily quest(s) active across all societies.");
        DrawDashboard(cache);

        var achievementSnapshot = this.cachedAchievementSnapshot;
        if (achievementSnapshot != null)
        {
            ImGui.TextUnformatted($"Relevant achievements: {achievementSnapshot.CompletedAchievementCount}/{achievementSnapshot.TotalAchievementCount} complete");
            ImGui.TextDisabled($"{achievementSnapshot.FullyCompletedSocietyCount}/{snapshot.Progress.Count} societies have all tracked milestones complete.");
            if (!achievementSnapshot.IsAchievementListLoaded)
            {
                ImGui.TextDisabled(achievementSnapshot.StatusMessage);
            }
        }

        ImGui.TextUnformatted(cache.Recommendation.Summary);
        ImGui.TextDisabled(cache.Recommendation.Reason);
        DrawTooltip("Recommendation is based on actionability, quest readiness, and distance to rank up.");
        ImGui.TextDisabled(this.automationStatus);
        DrawTooltip(this.automationStatus);
        ImGui.Separator();
    }

    private static void DrawDashboard(PlannerViewCache cache)
    {
        ImGui.TextUnformatted($"Actionable: {cache.ActionableCount}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"In progress: {cache.InProgressCount}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Blocked: {cache.BlockedCount}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Near rank-up: {cache.NearRankUpCount}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Maxed: {cache.MaxedCount}");
    }

    private void DrawDiagnosticsPanel(PlannerViewCache cache)
    {
        if (!ImGui.CollapsingHeader("Diagnostics"))
        {
            return;
        }

        ImGui.TextUnformatted($"Actionable societies: {cache.ActionableCount}");
        ImGui.TextDisabled($"{cache.InProgressCount} in progress, {cache.BlockedCount} blocked today, {cache.SetupCount} need setup");

        for (var i = 0; i < cache.DiagnosticsRows.Length; i++)
        {
            var row = cache.DiagnosticsRows[i].Row;
            ImGui.BulletText($"{row.Progress.Society.Name}: {row.DailyStatus.StatusMessage}");
        }

        ImGui.Separator();
    }

    private void DrawRow(PlannerViewCache cache, RowViewState rowState)
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
            ImGui.TextDisabled("Recommended next");
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
        if (!rowState.CanStartDaily)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button(rowState.ButtonLabel))
        {
            this.automationStatus = this.automationService.AcceptAllAvailableDailies(progress.Society).Message;
            this.automationService.InvalidateStatusCache();
            InvalidateRawData();
        }

        if (!rowState.CanStartDaily)
        {
            ImGui.EndDisabled();
        }

        ImGui.TextDisabled(dailyStatus.StatusMessage);
        DrawTooltip(dailyStatus.StatusMessage);
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
        var rowStates = new RowViewState[progressCount];
        for (var i = 0; i < progressCount; i++)
        {
            var progress = snapshot.Progress[i];
            var row = new SocietyPlannerRow(
                progress,
                this.automationService.GetDailyQuestStatus(progress.Society),
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

            if (this.configuration.OnlyShowActionableSocieties && !row.Row.IsActionable)
            {
                continue;
            }

            if (!MatchesActivityFilter(row.Row.Progress.Society.Activity, this.configuration.PreferredActivityFilter))
            {
                continue;
            }

            visibleRows.Add(row);
        }

        visibleRows.Sort(GetVisibleRowComparer());
        return [.. visibleRows];
    }

    private RowViewState[] BuildDiagnosticsRows(RowViewState[] rows)
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

            if (!MatchesActivityFilter(row.Row.Progress.Society.Activity, this.configuration.PreferredActivityFilter))
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

    private PlannerRecommendation BuildRecommendation(RowViewState? recommendedRow)
    {
        if (recommendedRow == null)
        {
            return new PlannerRecommendation(
                null,
                "No recommended daily is available.",
                "Try changing the focus filter or waiting for new dailies to unlock.");
        }

        var progress = recommendedRow.Row.Progress;
        var reason = recommendedRow.Row.IsActionable
            ? recommendedRow.Row.DailyStatus.StatusMessage
            : progress.IsMaxRank
                ? "Already at max rank."
                : "Not currently startable, but still your closest active goal.";
        var rankRemaining = progress.CurrentRank.MaximumReputation == 0
            ? 0
            : Math.Max(0, progress.CurrentRank.MaximumReputation - progress.CurrentReputation);
        var eta = BuildEtaInfo(progress, recommendedRow.Row.DailyStatus);
        var etaText = eta.Kind switch
        {
            EtaKind.Completed => "Completed.",
            EtaKind.Projected => $"{eta.StatusPhrase} Target: {eta.EstimatedCompletionUtc:ddd, MMM d}. Rep/week: {eta.ProjectedReputationPerWeek:N0}. Ranks/week: {eta.ProjectedRanksPerWeek:0.00}.",
            _ => "No projection available.",
        };

        return new PlannerRecommendation(
            recommendedRow.Row,
            $"Recommended: {progress.Society.Name}",
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
        return new RowViewState(
            row,
            GetRecommendationScore(row),
            GetDiagnosticPriority(row),
            progress.IsUnlocked && dailyStatus.CanStartNextQuest,
            BuildDailyBreakdownText(dailyStatus),
            BuildButtonLabel(progress, dailyStatus),
            BuildRowColor(visualState),
            BuildEtaSummaryText(eta),
            BuildEtaDetailText(eta),
            BuildEtaTooltipText(eta),
            GetRankUpDistance(progress) > 0 && GetRankUpDistance(progress) <= NearRankUpThresholdReputation);
    }

    private static string BuildButtonLabel(SocietyProgress progress, DailyQuestStatus dailyStatus)
    {
        return dailyStatus.Readiness switch
        {
            DailyQuestReadiness.InProgress => $"Continue daily###start-daily-{(byte)progress.Society.Id}",
            DailyQuestReadiness.ReadyToTurnIn => $"Hand-in ready###start-daily-{(byte)progress.Society.Id}",
            _ when progress.AcceptedDailyQuestCount > 0 => $"Resume daily###start-daily-{(byte)progress.Society.Id}",
            _ => $"Start daily###start-daily-{(byte)progress.Society.Id}",
        };
    }

    private static string BuildDailyBreakdownText(DailyQuestStatus dailyStatus)
    {
        return dailyStatus.Readiness switch
        {
            DailyQuestReadiness.ReadyToTurnIn => dailyStatus.ReadyQuestCount > 0
                ? $"{dailyStatus.CompletedQuestCount} complete, {dailyStatus.ReadyQuestCount} still ready to accept, hand-in after pickups"
                : $"{dailyStatus.CompletedQuestCount} complete, ready to hand in",
            DailyQuestReadiness.InProgress => dailyStatus.CompletedQuestCount > 0
                ? $"{dailyStatus.CompletedQuestCount} complete, keep going"
                : "finish remaining objectives",
            _ => $"{dailyStatus.CompletedQuestCount} completed, {dailyStatus.ReadyQuestCount} ready, {dailyStatus.BlockedQuestCount} blocked",
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
            DailyQuestReadiness.InProgress => 4,
            DailyQuestReadiness.Ready => 5,
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

    private void SetupSortableColumns()
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

    private static void DrawTooltip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !string.IsNullOrWhiteSpace(text))
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(text);
            ImGui.EndTooltip();
        }
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
            EtaKind.Projected => $"{eta.DetailText}\nWeekly efficiency: {eta.ProjectedReputationPerWeek:N0} rep/week, {eta.ProjectedRanksPerWeek:0.00} ranks/week.",
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

    private sealed record RowViewState(
        SocietyPlannerRow Row,
        int RecommendationScore,
        int DiagnosticPriority,
        bool CanStartDaily,
        string DailyBreakdownText,
        string ButtonLabel,
        uint RowColorU32,
        string EtaSummary,
        string EtaDetail,
        string EtaTooltip,
        bool IsNearRankUp);

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
