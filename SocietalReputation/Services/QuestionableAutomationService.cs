using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using FFXIVClientStructs.FFXIV.Client.Game;
using SocietalReputation.Models;
using System.Diagnostics;
using System.Reflection;

namespace SocietalReputation.Services;

public sealed class QuestionableAutomationService
{
    private const string QuestionableQuestDataError = "Questionable returned invalid quest data for one or more dailies.";
    private const int PerSocietyDailyQuestLimit = 3;
    private static readonly TimeSpan QuestAcceptTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan QuestAcceptPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StatusCacheTtl = TimeSpan.FromSeconds(1);

    private readonly ICallGateSubscriber<bool> isRunning;
    private readonly ICallGateSubscriber<string, bool> startQuest;
    private readonly ICallGateSubscriber<string, bool> stop;
    private readonly ICallGateSubscriber<string, bool> isQuestLocked;
    private readonly ICallGateSubscriber<string, bool> isReadyToAcceptQuest;
    private readonly ICallGateSubscriber<string, bool> isQuestAccepted;
    private readonly ICallGateSubscriber<string, bool> isQuestComplete;
    private readonly ICallGateSubscriber<string, bool> isQuestUnobtainable;

    private CachedStatus? cachedAvailability;
    private CachedStatus? cachedRunningState;

    public QuestionableAutomationService(IDalamudPluginInterface pluginInterface)
    {
        this.isRunning = pluginInterface.GetIpcSubscriber<bool>("Questionable.IsRunning");
        this.startQuest = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.StartQuest");
        this.stop = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.Stop");
        this.isQuestLocked = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.IsQuestLocked");
        this.isReadyToAcceptQuest = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.IsReadyToAcceptQuest");
        this.isQuestAccepted = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.IsQuestAccepted");
        this.isQuestComplete = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.IsQuestComplete");
        this.isQuestUnobtainable = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.IsQuestUnobtainable");
    }

    public void InvalidateStatusCache()
    {
        this.cachedAvailability = null;
        this.cachedRunningState = null;
    }

    public bool IsAvailable()
    {
        if (TryGetCachedStatus(this.cachedAvailability, out var isAvailable))
        {
            return isAvailable;
        }

        try
        {
            var running = this.isRunning.InvokeFunc();
            CacheStatus(true, running);
            return true;
        }
        catch (IpcError)
        {
            CacheUnavailable();
            return false;
        }
    }

    public bool IsRunning()
    {
        if (TryGetCachedStatus(this.cachedRunningState, out var isRunning))
        {
            return isRunning;
        }

        if (!IsAvailable())
        {
            return false;
        }

        try
        {
            var running = this.isRunning.InvokeFunc();
            CacheStatus(true, running);
            return running;
        }
        catch (IpcError)
        {
            CacheUnavailable();
            return false;
        }
    }

    public AutomationResult AcceptAllAvailableDailies(SocietyInfo society)
    {
        if (society.DailyQuestStart == 0 || society.DailyQuestEnd == 0)
        {
            return new AutomationResult(false, $"No daily quest range is configured for {society.Name}.");
        }

        if (!IsAvailable())
        {
            return new AutomationResult(false, "Questionable IPC is unavailable. Install/enable Questionable and complete its setup.");
        }

        try
        {
            var acceptedThisRun = 0;

            while (true)
            {
                var evaluation = EvaluateSocietyQuests(society);
                if (evaluation.HadQuestDataError)
                {
                    return new AutomationResult(false, QuestionableQuestDataError);
                }

                var canAcceptMore = evaluation.AcceptedQuestCount < PerSocietyDailyQuestLimit;
                if (canAcceptMore && evaluation.FirstReadyQuestId is ushort readyQuestId)
                {
                    var quest = readyQuestId.ToString();
                    if (!this.startQuest.InvokeFunc(quest))
                    {
                        InvalidateStatusCache();
                        return BuildAcceptanceFailureResult(
                            society,
                            acceptedThisRun,
                            readyQuestId,
                            "Questionable could not start the ready quest.");
                    }

                    InvalidateStatusCache();
                    if (!WaitForQuestAcceptance(society, readyQuestId, evaluation.AcceptedQuestCount))
                    {
                        return BuildAcceptanceFailureResult(
                            society,
                            acceptedThisRun,
                            readyQuestId,
                            "Timed out waiting for the quest to be accepted.");
                    }

                    acceptedThisRun++;
                    continue;
                }

                if (evaluation.AcceptedQuestCount > 0 && !evaluation.AllAcceptedQuestsComplete)
                {
                    if (evaluation.FirstAcceptedIncompleteQuestId is not ushort questToResumeId)
                    {
                        return new AutomationResult(
                            false,
                            $"{society.Name} has accepted quests in progress, but none could be resumed.",
                            evaluation.AcceptedQuestCount);
                    }

                    if (!this.startQuest.InvokeFunc(questToResumeId.ToString()))
                    {
                        InvalidateStatusCache();
                        return BuildAcceptanceFailureResult(
                            society,
                            acceptedThisRun,
                            questToResumeId,
                            "Questionable could not resume the accepted quest.");
                    }

                    InvalidateStatusCache();
                    return new AutomationResult(
                        true,
                        BuildAutomationProgressMessage(society, evaluation.AcceptedQuestCount + acceptedThisRun, acceptedThisRun, "Continuing accepted quests before turn-in."),
                        evaluation.AcceptedQuestCount);
                }

                if (evaluation.AcceptedQuestCount > 0 && evaluation.AllAcceptedQuestsComplete)
                {
                    return new AutomationResult(
                        false,
                        $"{society.Name} accepted quests are complete and ready to hand in.",
                        evaluation.AcceptedQuestCount);
                }

                return acceptedThisRun > 0
                    ? new AutomationResult(
                        true,
                        BuildAutomationProgressMessage(society, evaluation.AcceptedQuestCount + acceptedThisRun, acceptedThisRun, "Finish accepted quests before turn-in."),
                        evaluation.AcceptedQuestCount + acceptedThisRun)
                    : new AutomationResult(false, $"No available {society.Name} daily quest was found.");
            }
        }
        catch (IpcError)
        {
            CacheUnavailable();
            return new AutomationResult(false, "Questionable IPC is unavailable. Install/enable Questionable and complete its setup.");
        }
    }

    public DailyQuestStatus GetDailyQuestStatus(SocietyInfo society)
    {
        var (acceptedQuestCount, completedQuestCount) = GetAcceptedQuestCounts(society);
        if (society.DailyQuestStart == 0 || society.DailyQuestEnd == 0)
        {
            return new DailyQuestStatus(
                DailyQuestReadiness.Unconfigured,
                0,
                acceptedQuestCount,
                completedQuestCount,
                0,
                false,
                false,
                IsAvailable(),
                "No daily quest range configured.");
        }

        if (!IsAvailable())
        {
            return new DailyQuestStatus(
                DailyQuestReadiness.Unavailable,
                0,
                acceptedQuestCount,
                completedQuestCount,
                0,
                false,
                acceptedQuestCount > 0,
                false,
                acceptedQuestCount > 0
                    ? $"{acceptedQuestCount} accepted quest(s) in progress."
                    : "Questionable unavailable.");
        }

        try
        {
            var evaluation = EvaluateSocietyQuests(society);
            var readiness = evaluation.AcceptedQuestCount > 0
                ? evaluation.AllAcceptedQuestsComplete
                    ? DailyQuestReadiness.ReadyToTurnIn
                    : DailyQuestReadiness.InProgress
                : evaluation.ReadyQuestCount > 0
                    ? DailyQuestReadiness.Ready
                    : evaluation.BlockedQuestCount > 0
                        ? DailyQuestReadiness.LockedOrUnavailable
                        : DailyQuestReadiness.NoneAvailable;

            return new DailyQuestStatus(
                readiness,
                evaluation.ReadyQuestCount,
                evaluation.AcceptedQuestCount,
                evaluation.CompletedQuestCount,
                evaluation.BlockedQuestCount,
                evaluation.AllAcceptedQuestsComplete,
                evaluation.CanStartNextQuest,
                true,
                BuildDailyStatusMessage(evaluation));
        }
        catch (IpcError)
        {
            CacheUnavailable();
            return new DailyQuestStatus(
                DailyQuestReadiness.Unavailable,
                0,
                acceptedQuestCount,
                completedQuestCount,
                0,
                false,
                acceptedQuestCount > 0,
                false,
                "Questionable IPC is unavailable.");
        }
        catch (Exception)
        {
            return new DailyQuestStatus(
                DailyQuestReadiness.Unavailable,
                0,
                acceptedQuestCount,
                completedQuestCount,
                0,
                false,
                acceptedQuestCount > 0,
                false,
                QuestionableQuestDataError);
        }
    }

    public AutomationResult StartFirstAvailable(IEnumerable<SocietyInfo> societies, string sourceLabel)
    {
        if (!IsAvailable())
        {
            return new AutomationResult(false, "Questionable IPC is unavailable. Install/enable Questionable and complete its setup.");
        }

        foreach (var society in societies)
        {
            var status = GetDailyQuestStatus(society);
            if (!status.CanStartNextQuest)
            {
                continue;
            }

            var result = AcceptAllAvailableDailies(society);
            if (result.Success)
            {
                return new AutomationResult(true, $"{sourceLabel}: {result.Message}");
            }
        }

        return new AutomationResult(false, $"{sourceLabel}: no available daily quest was found.");
    }

    public AutomationResult Stop()
    {
        try
        {
            var result = this.stop.InvokeFunc("SocietalReputation")
                ? new AutomationResult(true, "Stopped Questionable automation.")
                : new AutomationResult(false, "Questionable did not stop automation.");
            InvalidateStatusCache();
            return result;
        }
        catch (IpcError)
        {
            CacheUnavailable();
            return new AutomationResult(false, "Questionable IPC is unavailable.");
        }
    }

    private static bool TryGetCachedStatus(CachedStatus? cachedStatus, out bool value)
    {
        if (cachedStatus != null && cachedStatus.ExpiresAtUtc > DateTime.UtcNow)
        {
            value = cachedStatus.Value;
            return true;
        }

        value = false;
        return false;
    }

    private void CacheStatus(bool isAvailable, bool isRunningValue)
    {
        var expiresAtUtc = DateTime.UtcNow + StatusCacheTtl;
        this.cachedAvailability = new CachedStatus(isAvailable, expiresAtUtc);
        this.cachedRunningState = new CachedStatus(isRunningValue, expiresAtUtc);
    }

    private void CacheUnavailable()
    {
        var expiresAtUtc = DateTime.UtcNow + StatusCacheTtl;
        this.cachedAvailability = new CachedStatus(false, expiresAtUtc);
        this.cachedRunningState = new CachedStatus(false, expiresAtUtc);
    }

    private static string BuildDailyStatusMessage(SocietyQuestEvaluation evaluation)
    {
        if (evaluation.AcceptedQuestCount > 0)
        {
            if (evaluation.AllAcceptedQuestsComplete)
            {
                return evaluation.ReadyQuestCount > 0
                    ? $"{evaluation.AcceptedQuestCount}/{PerSocietyDailyQuestLimit} accepted, all complete, ready to hand in after remaining pickups."
                    : $"{evaluation.AcceptedQuestCount}/{PerSocietyDailyQuestLimit} accepted, all complete, ready to hand in.";
            }

            if (evaluation.CompletedQuestCount > 0)
            {
                return $"{evaluation.AcceptedQuestCount}/{PerSocietyDailyQuestLimit} accepted, {evaluation.CompletedQuestCount} complete, keep going.";
            }

            return evaluation.ReadyQuestCount > 0
                ? $"{evaluation.AcceptedQuestCount}/{PerSocietyDailyQuestLimit} accepted, finish remaining objectives before turn-in."
                : $"{evaluation.AcceptedQuestCount}/{PerSocietyDailyQuestLimit} accepted, keep working objectives.";
        }

        if (evaluation.ReadyQuestCount > 0)
        {
            return $"{evaluation.ReadyQuestCount} quest(s) ready to start.";
        }

        if (evaluation.HadQuestDataError)
        {
            return QuestionableQuestDataError;
        }

        return evaluation.BlockedQuestCount > 0
            ? $"{evaluation.BlockedQuestCount} quest(s) locked or unavailable."
            : "No daily quest available.";
    }

    private bool TryInvokeQuestCheck(ICallGateSubscriber<string, bool> subscriber, string quest, out bool result)
    {
        try
        {
            result = subscriber.InvokeFunc(quest);
            return true;
        }
        catch (IpcError)
        {
            InvalidateStatusCache();
            result = false;
            return false;
        }
        catch (TargetInvocationException)
        {
            result = false;
            return false;
        }
        catch (KeyNotFoundException)
        {
            result = false;
            return false;
        }
        catch (Exception)
        {
            result = false;
            return false;
        }
    }

    private SocietyQuestEvaluation EvaluateSocietyQuests(SocietyInfo society)
    {
        var quests = new List<QuestState>(society.DailyQuestEnd - society.DailyQuestStart + 1);
        var acceptedQuestCount = 0;
        var completedQuestCount = 0;
        var readyQuestCount = 0;
        var blockedQuestCount = 0;
        var hadQuestDataError = false;
        ushort? firstReadyQuestId = null;
        ushort? firstAcceptedIncompleteQuestId = null;

        for (ushort questId = society.DailyQuestStart; questId <= society.DailyQuestEnd; questId++)
        {
            var quest = questId.ToString();
            if (!TryInvokeQuestCheck(this.isQuestAccepted, quest, out var isAccepted))
            {
                hadQuestDataError = true;
                continue;
            }

            if (isAccepted)
            {
                acceptedQuestCount++;
                var isComplete = false;
                var isCompleteKnown = TryInvokeQuestCheck(this.isQuestComplete, quest, out isComplete);
                if (!isCompleteKnown)
                {
                    hadQuestDataError = true;
                }
                else if (isComplete)
                {
                    completedQuestCount++;
                }
                else if (firstAcceptedIncompleteQuestId == null)
                {
                    firstAcceptedIncompleteQuestId = questId;
                }

                quests.Add(new QuestState(questId, true));
                continue;
            }

            var isBlocked = false;
            if (!TryInvokeQuestCheck(this.isQuestUnobtainable, quest, out var isUnobtainable))
            {
                hadQuestDataError = true;
                continue;
            }

            if (!TryInvokeQuestCheck(this.isQuestLocked, quest, out var isLocked))
            {
                hadQuestDataError = true;
                continue;
            }

            if (isUnobtainable || isLocked)
            {
                isBlocked = true;
                blockedQuestCount++;
            }

            var isReadyToAccept = false;
            if (!isBlocked)
            {
                if (!TryInvokeQuestCheck(this.isReadyToAcceptQuest, quest, out isReadyToAccept))
                {
                    hadQuestDataError = true;
                    continue;
                }

                if (isReadyToAccept)
                {
                    readyQuestCount++;
                    firstReadyQuestId ??= questId;
                }
            }

            quests.Add(new QuestState(questId, false));
        }

        return new SocietyQuestEvaluation(
            [.. quests],
            acceptedQuestCount,
            completedQuestCount,
            readyQuestCount,
            blockedQuestCount,
            acceptedQuestCount > 0 && completedQuestCount >= acceptedQuestCount,
            hadQuestDataError,
            firstReadyQuestId,
            firstAcceptedIncompleteQuestId);
    }

    private bool WaitForQuestAcceptance(SocietyInfo society, ushort questId, int acceptedQuestCountBeforeStart)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < QuestAcceptTimeout)
        {
            var evaluation = EvaluateSocietyQuests(society);
            if (evaluation.IsAccepted(questId) || evaluation.AcceptedQuestCount > acceptedQuestCountBeforeStart)
            {
                return true;
            }

            Thread.Sleep(QuestAcceptPollInterval);
        }

        return false;
    }

    private static AutomationResult BuildAcceptanceFailureResult(SocietyInfo society, int acceptedThisRun, ushort blockedQuestId, string reason)
    {
        var message = acceptedThisRun > 0
            ? $"Accepted {acceptedThisRun} {society.Name} daily quest(s) before quest {blockedQuestId} blocked progress: {reason}"
            : $"{society.Name} quest {blockedQuestId} blocked progress: {reason}";
        return new AutomationResult(acceptedThisRun > 0, message, acceptedThisRun, blockedQuestId, true);
    }

    private static unsafe (int AcceptedQuestCount, int CompletedQuestCount) GetAcceptedQuestCounts(SocietyInfo society)
    {
        if (society.DailyQuestStart == 0 || society.DailyQuestEnd == 0)
        {
            return (0, 0);
        }

        var questManager = QuestManager.Instance();
        if (questManager == null)
        {
            return (0, 0);
        }

        var acceptedQuestCount = 0;
        var completedQuestCount = 0;
        for (ushort questId = society.DailyQuestStart; questId <= society.DailyQuestEnd; questId++)
        {
            var dailyQuest = questManager->GetDailyQuestById(questId);
            if (dailyQuest == null)
            {
                continue;
            }

            acceptedQuestCount++;
            if (dailyQuest->IsCompleted)
            {
                completedQuestCount++;
            }
        }

        return (acceptedQuestCount, completedQuestCount);
    }

    private static string BuildAutomationProgressMessage(SocietyInfo society, int acceptedCount, int acceptedThisRun, string suffix)
    {
        return acceptedThisRun > 0
            ? $"Accepted {acceptedThisRun} {society.Name} daily quest(s). {acceptedCount}/{PerSocietyDailyQuestLimit} accepted total. {suffix}"
            : $"{society.Name}: {acceptedCount}/{PerSocietyDailyQuestLimit} accepted. {suffix}";
    }

    private sealed record CachedStatus(bool Value, DateTime ExpiresAtUtc);

    private sealed record QuestState(ushort QuestId, bool IsAccepted);

    private sealed record SocietyQuestEvaluation(
        QuestState[] Quests,
        int AcceptedQuestCount,
        int CompletedQuestCount,
        int ReadyQuestCount,
        int BlockedQuestCount,
        bool AllAcceptedQuestsComplete,
        bool HadQuestDataError,
        ushort? FirstReadyQuestId,
        ushort? FirstAcceptedIncompleteQuestId)
    {
        public bool CanStartNextQuest => this.AcceptedQuestCount > 0 && !this.AllAcceptedQuestsComplete || this.ReadyQuestCount > 0;

        public bool IsAccepted(ushort questId)
        {
            for (var i = 0; i < this.Quests.Length; i++)
            {
                var quest = this.Quests[i];
                if (quest.QuestId == questId)
                {
                    return quest.IsAccepted;
                }
            }

            return false;
        }
    }
}
