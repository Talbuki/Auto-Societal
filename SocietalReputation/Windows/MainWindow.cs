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

        if (!ImGui.BeginTable("societal-reputation-table", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            return;
        }

        ImGui.TableSetupColumn("Society");
        ImGui.TableSetupColumn("Activity");
        ImGui.TableSetupColumn("Rank");
        ImGui.TableSetupColumn("Reputation");
        ImGui.TableSetupColumn("Progress");
        ImGui.TableSetupColumn("Dailies");
        ImGui.TableSetupColumn("Automation");
        ImGui.TableHeadersRow();

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
            SaveConfiguration();
        }

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
        ImGui.TextUnformatted($"Tribal allowances: {snapshot.RemainingAllowances}/{snapshot.TotalAllowances} remaining");
        ImGui.TextDisabled($"{snapshot.AcceptedDailyQuests} accepted daily quest(s) active across all societies.");

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
        ImGui.TextDisabled(this.automationStatus);
        ImGui.Separator();
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

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(progress.Society.Name);
        ImGui.TextDisabled(progress.Society.Expansion);
        ImGui.TextDisabled(rowState.Row.AchievementStatus.StatusMessage);
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
        }

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

        return new PlannerRecommendation(
            recommendedRow.Row,
            $"Recommended: {progress.Society.Name}",
            rankRemaining > 0
                ? $"{reason} {rankRemaining:N0} reputation to the next rank."
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
        return new RowViewState(
            row,
            GetRecommendationScore(row),
            GetDiagnosticPriority(row),
            progress.IsUnlocked && dailyStatus.CanStartNextQuest,
            BuildDailyBreakdownText(dailyStatus),
            BuildButtonLabel(progress, dailyStatus));
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
        return this.configuration.SortMode switch
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
        string ButtonLabel);

    private sealed record PlannerViewCache(
        RowViewState[] VisibleRows,
        RowViewState[] DiagnosticsRows,
        PlannerRecommendation Recommendation,
        RowViewState? RecommendationRow,
        int ActionableCount,
        int InProgressCount,
        int SetupCount,
        int BlockedCount,
        string AutomationState,
        int RawDataVersion,
        int ViewDataVersion);

    private static class EnumValueCache<TEnum>
        where TEnum : struct, Enum
    {
        public static readonly TEnum[] Values = Enum.GetValues<TEnum>();
    }
}
