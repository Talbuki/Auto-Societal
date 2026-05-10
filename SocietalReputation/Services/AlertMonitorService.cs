using Dalamud.Plugin.Services;
using SocietalReputation.Models;

namespace SocietalReputation.Services;

internal sealed class AlertMonitorService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AutomationStalledThreshold = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan AutomationStalledCooldown = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PrerequisiteMetCooldown = TimeSpan.FromMinutes(10);

    private readonly ReputationService reputationService;
    private readonly QuestionableAutomationService automationService;
    private readonly AlertDispatcher dispatcher;

    private DateTime lastPollUtc = DateTime.MinValue;
    private AlertMonitorSnapshot? previousSnapshot;

    public AlertMonitorService(
        ReputationService reputationService,
        QuestionableAutomationService automationService,
        AlertDispatcher dispatcher)
    {
        this.reputationService = reputationService;
        this.automationService = automationService;
        this.dispatcher = dispatcher;
    }

    public void OnFrameworkUpdate(IFramework framework)
    {
        var utcNow = framework.LastUpdateUTC;
        if (this.lastPollUtc != DateTime.MinValue && utcNow - this.lastPollUtc < PollInterval)
        {
            return;
        }

        this.lastPollUtc = utcNow;
        var current = BuildSnapshot();
        if (this.previousSnapshot == null)
        {
            this.previousSnapshot = current;
            return;
        }

        EvaluateAlerts(this.previousSnapshot, current, utcNow);
        this.previousSnapshot = current;
    }

    private AlertMonitorSnapshot BuildSnapshot()
    {
        var snapshot = this.reputationService.GetSnapshot();
        var societyStates = new Dictionary<EAlliedSociety, AlertSocietyState>(snapshot.Progress.Count);

        for (var i = 0; i < snapshot.Progress.Count; i++)
        {
            var progress = snapshot.Progress[i];
            var dailyStatus = this.automationService.GetDailyQuestStatus(progress.Society);
            var societyState = new AlertSocietyState(
                progress.Society.Id,
                progress.Society.Name,
                progress.IsUnlocked,
                SocietyPlannerRules.IsActionable(progress, dailyStatus),
                SocietyPlannerRules.IsRankUpAvailable(progress),
                dailyStatus.AcceptedQuestCount,
                dailyStatus.CompletedQuestCount);
            societyStates[progress.Society.Id] = societyState;
        }

        return new AlertMonitorSnapshot(
            snapshot.RemainingAllowances,
            this.automationService.GetMonitorState(),
            societyStates);
    }

    private void EvaluateAlerts(AlertMonitorSnapshot previous, AlertMonitorSnapshot current, DateTime utcNow)
    {
        if (current.RemainingAllowances > previous.RemainingAllowances)
        {
            this.dispatcher.Dispatch(
                new AlertEvent(
                    AlertType.DailyReset,
                    "Daily reset",
                    $"Tribal allowances refreshed. {current.RemainingAllowances} allowance(s) are available.",
                    "daily-reset",
                    PollInterval),
                utcNow);
        }

        foreach (var pair in current.Societies)
        {
            if (!previous.Societies.TryGetValue(pair.Key, out var previousState))
            {
                continue;
            }

            var currentState = pair.Value;
            if (!previousState.IsUnlocked && currentState.IsUnlocked)
            {
                this.dispatcher.Dispatch(
                    new AlertEvent(
                        AlertType.SocietyUnlocked,
                        "Society unlocked",
                        $"{currentState.Name} is now unlocked and ready to track.",
                        $"unlock:{currentState.SocietyId}",
                        PollInterval),
                    utcNow);
            }

            if (!previousState.IsRankUpAvailable && currentState.IsRankUpAvailable)
            {
                this.dispatcher.Dispatch(
                    new AlertEvent(
                        AlertType.RankUpAvailable,
                        "Rank-up available",
                        $"{currentState.Name} is ready to rank up.",
                        $"rankup:{currentState.SocietyId}",
                        PollInterval),
                    utcNow);
            }

            if (previousState.IsUnlocked
                && !previousState.IsActionable
                && currentState.IsActionable
                && currentState.IsUnlocked)
            {
                this.dispatcher.Dispatch(
                    new AlertEvent(
                        AlertType.PrerequisiteMet,
                        "Prerequisite met",
                        $"{currentState.Name} is now actionable.",
                        $"prereq:{currentState.SocietyId}",
                        PrerequisiteMetCooldown),
                    utcNow);
            }
        }

        EvaluateAutomationStalledAlert(previous, current, utcNow);
    }

    private void EvaluateAutomationStalledAlert(AlertMonitorSnapshot previous, AlertMonitorSnapshot current, DateTime utcNow)
    {
        var automation = current.Automation;
        if (!automation.IsRunning || automation.TargetSocietyId is not EAlliedSociety targetSocietyId)
        {
            return;
        }

        if (!previous.Societies.TryGetValue(targetSocietyId, out var previousState)
            || !current.Societies.TryGetValue(targetSocietyId, out var currentState))
        {
            return;
        }

        var progressMarkerUtc = automation.LastProgressUtc ?? automation.LastStartedUtc;
        if (progressMarkerUtc == null || utcNow - progressMarkerUtc.Value < AutomationStalledThreshold)
        {
            return;
        }

        var acceptedUnchanged = previousState.AcceptedQuestCount == currentState.AcceptedQuestCount;
        var completedUnchanged = previousState.CompletedQuestCount == currentState.CompletedQuestCount;
        if (!acceptedUnchanged || !completedUnchanged)
        {
            return;
        }

        var targetName = automation.TargetSocietyName ?? currentState.Name;
        this.dispatcher.Dispatch(
            new AlertEvent(
                AlertType.AutomationStalled,
                "Automation stalled",
                $"{targetName} has shown no daily quest progress recently. Check Questionable or the current quest state.",
                $"stalled:{targetSocietyId}",
                AutomationStalledCooldown),
            utcNow);
    }
}
