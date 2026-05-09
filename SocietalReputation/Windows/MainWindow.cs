using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Bindings.ImGui;
using SocietalReputation.Models;
using SocietalReputation.Services;
using System.Numerics;

namespace SocietalReputation.Windows;

public sealed class MainWindow : Window
{
    private readonly Configuration configuration;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ReputationService reputationService;
    private readonly QuestionableAutomationService automationService;
    private readonly AchievementTrackingService achievementTrackingService;
    private string automationStatus = "Automation uses Questionable IPC when available.";
    private IReadOnlyList<SocietyPlannerRow> cachedRows = [];
    private ReputationSnapshot? cachedSnapshot;
    private AchievementSnapshot? cachedAchievementSnapshot;
    private PlannerRecommendation cachedRecommendation = new(null, "No recommendation yet.", "Open the planner to refresh recommendations.");
    private DateTime lastRefreshUtc = DateTime.MinValue;
    private const float RefreshIntervalSeconds = 1f;

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

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(880, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void PreDraw()
    {
        if (this.configuration.IsMainWindowOpen != IsOpen)
        {
            this.configuration.IsMainWindowOpen = IsOpen;
            this.configuration.Save(this.pluginInterface);
        }
    }

    public override void Draw()
    {
        RefreshPlannerData();

        DrawPlannerControls();
        DrawPlannerSummary();
        DrawDiagnosticsPanel();

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

        foreach (var row in GetVisibleRows())
        {
            var progress = row.Progress;
            var dailyStatus = row.DailyStatus;

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(progress.Society.Name);
            ImGui.TextDisabled(progress.Society.Expansion);
            ImGui.TextDisabled(row.AchievementStatus.StatusMessage);
            if (ReferenceEquals(this.cachedRecommendation.Row, row))
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
                ImGui.ProgressBar(0, new Vector2(-1, 0), "Locked");
            }
            else if (progress.CurrentRank.MaximumReputation == 0)
            {
                ImGui.ProgressBar(1, new Vector2(-1, 0), "Complete");
            }
            else
            {
                ImGui.ProgressBar(progress.RankProgress, new Vector2(-1, 0), $"{progress.RankReputationEarned:N0} / {progress.RankReputationRequired:N0}");
            }

            ImGui.TableNextColumn();
            if (!progress.HasDailyQuestSupport)
            {
                ImGui.TextDisabled("No dailies");
            }
            else
            {
                ImGui.TextUnformatted($"{dailyStatus.AcceptedQuestCount}/{progress.DailyQuestAllowanceTotal} accepted");
                ImGui.TextDisabled(BuildDailyBreakdownText(dailyStatus));
            }

            ImGui.TableNextColumn();
            var canStartDaily = progress.IsUnlocked && dailyStatus.CanStartNextQuest;
            if (!canStartDaily)
            {
                ImGui.BeginDisabled();
            }

            var buttonLabel = dailyStatus.Readiness switch
            {
                DailyQuestReadiness.InProgress => $"Continue daily###start-daily-{(byte)progress.Society.Id}",
                DailyQuestReadiness.ReadyToTurnIn => $"Hand-in ready###start-daily-{(byte)progress.Society.Id}",
                _ when progress.AcceptedDailyQuestCount > 0 => $"Resume daily###start-daily-{(byte)progress.Society.Id}",
                _ => $"Start daily###start-daily-{(byte)progress.Society.Id}",
            };
            if (ImGui.Button(buttonLabel))
            {
                this.automationStatus = this.automationService.AcceptAllAvailableDailies(progress.Society).Message;
                InvalidatePlannerData();
            }

            if (!canStartDaily)
            {
                ImGui.EndDisabled();
            }

            ImGui.TextDisabled(dailyStatus.StatusMessage);
        }

        ImGui.EndTable();
    }

    private void DrawPlannerControls()
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
            var recommendation = GetRecommendedActionableRow();
            this.automationStatus = recommendation is null
                ? "No recommended daily is currently startable."
                : this.automationService.AcceptAllAvailableDailies(recommendation.Progress.Society).Message;
            InvalidatePlannerData();
        }

        ImGui.SameLine();
        if (ImGui.Button("Start visible dailies"))
        {
            this.automationStatus = this.automationService.StartFirstAvailable(
                GetVisibleRows().Where(row => row.IsActionable).Select(row => row.Progress.Society),
                "Visible list").Message;
            InvalidatePlannerData();
        }

        ImGui.SameLine();
        if (ImGui.Button("Stop automation"))
        {
            this.automationStatus = this.automationService.Stop().Message;
            InvalidatePlannerData();
        }
    }

    private void DrawPlannerSummary()
    {
        var snapshot = this.cachedSnapshot;
        if (snapshot == null)
        {
            return;
        }

        var achievementSnapshot = this.cachedAchievementSnapshot;
        var automationState = this.automationService.IsAvailable()
            ? this.automationService.IsRunning() ? "Questionable: running" : "Questionable: ready"
            : "Questionable: unavailable";
        ImGui.TextDisabled(automationState);
        ImGui.TextUnformatted($"Tribal allowances: {snapshot.RemainingAllowances}/{snapshot.TotalAllowances} remaining");
        ImGui.TextDisabled($"{snapshot.AcceptedDailyQuests} accepted daily quest(s) active across all societies.");
        if (achievementSnapshot != null)
        {
            ImGui.TextUnformatted($"Relevant achievements: {achievementSnapshot.CompletedAchievementCount}/{achievementSnapshot.TotalAchievementCount} complete");
            ImGui.TextDisabled($"{achievementSnapshot.FullyCompletedSocietyCount}/{snapshot.Progress.Count} societies have all tracked milestones complete.");
            if (!achievementSnapshot.IsAchievementListLoaded)
            {
                ImGui.TextDisabled(achievementSnapshot.StatusMessage);
            }
        }
        ImGui.TextUnformatted(this.cachedRecommendation.Summary);
        ImGui.TextDisabled(this.cachedRecommendation.Reason);
        ImGui.TextDisabled(this.automationStatus);
        ImGui.Separator();
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

    private void DrawDiagnosticsPanel()
    {
        if (!ImGui.CollapsingHeader("Diagnostics"))
        {
            return;
        }

        var actionableCount = this.cachedRows.Count(row => row.IsActionable);
        var inProgressCount = this.cachedRows.Count(row => row.DailyStatus.Readiness == DailyQuestReadiness.InProgress);
        var setupCount = this.cachedRows.Count(row => row.DailyStatus.NeedsSetup);
        var blockedCount = this.cachedRows.Count(row => row.DailyStatus.Readiness == DailyQuestReadiness.LockedOrUnavailable);

        ImGui.TextUnformatted($"Actionable societies: {actionableCount}");
        ImGui.TextDisabled($"{inProgressCount} in progress, {blockedCount} blocked today, {setupCount} need setup");

        foreach (var row in this.cachedRows
                     .Where(row => !row.IsActionable)
                     .Where(row => !row.Progress.IsMaxRank)
                     .OrderBy(row => GetDiagnosticPriority(row))
                     .ThenBy(row => row.Progress.Society.Name, StringComparer.Ordinal))
        {
            ImGui.BulletText($"{row.Progress.Society.Name}: {row.DailyStatus.StatusMessage}");
        }

        ImGui.Separator();
    }

    private void RefreshPlannerData()
    {
        if ((DateTime.UtcNow - this.lastRefreshUtc).TotalSeconds < RefreshIntervalSeconds && this.cachedSnapshot != null)
        {
            return;
        }

        var snapshot = this.reputationService.GetSnapshot();
        var achievementSnapshot = this.achievementTrackingService.GetSnapshot(snapshot.Progress.Select(progress => progress.Society));
        var rows = snapshot.Progress
            .Select(progress => new SocietyPlannerRow(
                progress,
                this.automationService.GetDailyQuestStatus(progress.Society),
                achievementSnapshot.GetStatus(progress.Society.Id)))
            .ToArray();

        this.cachedSnapshot = snapshot;
        this.cachedAchievementSnapshot = achievementSnapshot;
        this.cachedRows = rows;
        this.cachedRecommendation = BuildRecommendation(rows);
        this.lastRefreshUtc = DateTime.UtcNow;
    }

    private void InvalidatePlannerData()
    {
        this.lastRefreshUtc = DateTime.MinValue;
    }

    private IEnumerable<SocietyPlannerRow> GetVisibleRows()
    {
        return SortRows(FilterRows(this.cachedRows)).ToArray();
    }

    private IEnumerable<SocietyPlannerRow> FilterRows(IEnumerable<SocietyPlannerRow> rows)
    {
        foreach (var row in rows)
        {
            if (!this.configuration.ShowCompletedDailies && row.Progress.IsMaxRank)
            {
                continue;
            }

            if (this.configuration.OnlyShowActionableSocieties && !row.IsActionable)
            {
                continue;
            }

            if (!MatchesActivityFilter(row.Progress.Society.Activity, this.configuration.PreferredActivityFilter))
            {
                continue;
            }

            yield return row;
        }
    }

    private IEnumerable<SocietyPlannerRow> SortRows(IEnumerable<SocietyPlannerRow> rows)
    {
        return this.configuration.SortMode switch
        {
            SocietySortMode.Name => rows
                .OrderBy(row => row.Progress.Society.Name, StringComparer.Ordinal),
            SocietySortMode.Expansion => rows
                .OrderBy(row => row.Progress.Society.Expansion, StringComparer.Ordinal)
                .ThenBy(row => row.Progress.Society.Name, StringComparer.Ordinal),
            SocietySortMode.ClosestToRankUp => rows
                .OrderBy(row => row.Progress.IsMaxRank)
                .ThenBy(row => row.Progress.CurrentRank.MaximumReputation == 0 ? int.MaxValue : row.Progress.CurrentRank.MaximumReputation - row.Progress.CurrentReputation)
                .ThenByDescending(row => row.IsActionable)
                .ThenBy(row => row.Progress.Society.Name, StringComparer.Ordinal),
            _ => rows
                .OrderByDescending(row => GetRecommendationScore(row))
                .ThenBy(row => row.Progress.CurrentRank.MaximumReputation == 0 ? int.MaxValue : row.Progress.CurrentRank.MaximumReputation - row.Progress.CurrentReputation)
                .ThenBy(row => row.Progress.Society.Name, StringComparer.Ordinal),
        };
    }

    private PlannerRecommendation BuildRecommendation(IReadOnlyList<SocietyPlannerRow> rows)
    {
        var recommendedRow = rows
            .Where(row => row.Progress.IsUnlocked && !row.Progress.IsMaxRank)
            .Where(row => MatchesActivityFilter(row.Progress.Society.Activity, this.configuration.PreferredActivityFilter))
            .OrderByDescending(GetRecommendationScore)
            .ThenBy(row => GetDiagnosticPriority(row))
            .ThenBy(row => row.Progress.CurrentRank.MaximumReputation == 0 ? int.MaxValue : row.Progress.CurrentRank.MaximumReputation - row.Progress.CurrentReputation)
            .FirstOrDefault();

        if (recommendedRow == null)
        {
            return new PlannerRecommendation(
                null,
                "No recommended daily is available.",
                "Try changing the focus filter or waiting for new dailies to unlock.");
        }

        var progress = recommendedRow.Progress;
        var reason = recommendedRow.IsActionable
            ? recommendedRow.DailyStatus.StatusMessage
            : progress.IsMaxRank
                ? "Already at max rank."
                : "Not currently startable, but still your closest active goal.";
        var rankRemaining = progress.CurrentRank.MaximumReputation == 0
            ? 0
            : Math.Max(0, progress.CurrentRank.MaximumReputation - progress.CurrentReputation);
        return new PlannerRecommendation(
            recommendedRow,
            $"Recommended: {progress.Society.Name}",
            rankRemaining > 0
                ? $"{reason} {rankRemaining:N0} reputation to the next rank."
                : reason);
    }

    private SocietyPlannerRow? GetRecommendedActionableRow()
    {
        return this.cachedRows
            .Where(row => row.IsActionable)
            .Where(row => MatchesActivityFilter(row.Progress.Society.Activity, this.configuration.PreferredActivityFilter))
            .OrderByDescending(GetRecommendationScore)
            .ThenBy(row => GetDiagnosticPriority(row))
            .ThenBy(row => row.Progress.CurrentRank.MaximumReputation == 0 ? int.MaxValue : row.Progress.CurrentRank.MaximumReputation - row.Progress.CurrentReputation)
            .FirstOrDefault();
    }

    private int GetRecommendationScore(SocietyPlannerRow row)
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

    private bool DrawEnumCombo<TEnum>(string label, ref TEnum selected)
        where TEnum : struct, Enum
    {
        var changed = false;
        if (ImGui.BeginCombo(label, selected.ToString()))
        {
            foreach (var value in Enum.GetValues<TEnum>())
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
        InvalidatePlannerData();
    }
}
