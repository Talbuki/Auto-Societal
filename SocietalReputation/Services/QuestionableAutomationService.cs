using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using FFXIVClientStructs.FFXIV.Client.Game;
using SocietalReputation.Models;
using System.Reflection;

namespace SocietalReputation.Services;

public sealed class QuestionableAutomationService
{
    private const string QuestionableQuestDataError = "Questionable returned invalid quest data for one or more dailies.";
    private readonly ICallGateSubscriber<bool> isRunning;
    private readonly ICallGateSubscriber<string, bool> startQuest;
    private readonly ICallGateSubscriber<string, bool> stop;
    private readonly ICallGateSubscriber<string, bool> isQuestLocked;
    private readonly ICallGateSubscriber<string, bool> isReadyToAcceptQuest;
    private readonly ICallGateSubscriber<string, bool> isQuestAccepted;
    private readonly ICallGateSubscriber<string, bool> isQuestUnobtainable;

    public QuestionableAutomationService(IDalamudPluginInterface pluginInterface)
    {
        this.isRunning = pluginInterface.GetIpcSubscriber<bool>("Questionable.IsRunning");
        this.startQuest = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.StartQuest");
        this.stop = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.Stop");
        this.isQuestLocked = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.IsQuestLocked");
        this.isReadyToAcceptQuest = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.IsReadyToAcceptQuest");
        this.isQuestAccepted = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.IsQuestAccepted");
        this.isQuestUnobtainable = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.IsQuestUnobtainable");
    }

    public bool IsAvailable()
    {
        try
        {
            _ = this.isRunning.InvokeFunc();
            return true;
        }
        catch (IpcError)
        {
            return false;
        }
    }

    public bool IsRunning()
    {
        try
        {
            return this.isRunning.InvokeFunc();
        }
        catch (IpcError)
        {
            return false;
        }
    }

    public AutomationResult StartNextAvailableDaily(SocietyInfo society)
    {
        if (society.DailyQuestStart == 0 || society.DailyQuestEnd == 0)
        {
            return new AutomationResult(false, $"No daily quest range is configured for {society.Name}.");
        }

        try
        {
            for (var questId = society.DailyQuestStart; questId <= society.DailyQuestEnd; questId++)
            {
                var quest = questId.ToString();
                if (TryInvokeQuestCheck(this.isQuestUnobtainable, quest, out var isUnobtainable) && isUnobtainable ||
                    TryInvokeQuestCheck(this.isQuestLocked, quest, out var isLocked) && isLocked)
                {
                    continue;
                }

                if (!TryInvokeQuestCheck(this.isReadyToAcceptQuest, quest, out var isReadyToAccept) ||
                    !TryInvokeQuestCheck(this.isQuestAccepted, quest, out var isAccepted))
                {
                    continue;
                }

                if (!isReadyToAccept && !isAccepted)
                {
                    continue;
                }

                return this.startQuest.InvokeFunc(quest)
                    ? new AutomationResult(true, $"Started {society.Name} daily quest {questId}.")
                    : new AutomationResult(false, $"Questionable could not start quest {questId}.");
            }

            return new AutomationResult(false, $"No available {society.Name} daily quest was found.");
        }
        catch (IpcError)
        {
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
                acceptedQuestCount > 0,
                false,
                acceptedQuestCount > 0
                    ? $"{acceptedQuestCount} accepted quest(s) in progress."
                    : "Questionable unavailable.");
        }

        try
        {
            var readyQuestCount = 0;
            var blockedQuestCount = 0;
            var hadQuestDataError = false;

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
                    continue;
                }

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
                    blockedQuestCount++;
                    continue;
                }

                if (!TryInvokeQuestCheck(this.isReadyToAcceptQuest, quest, out var isReadyToAccept))
                {
                    hadQuestDataError = true;
                    continue;
                }

                if (isReadyToAccept)
                {
                    readyQuestCount++;
                }
            }

            var readiness = acceptedQuestCount > 0
                ? DailyQuestReadiness.InProgress
                : readyQuestCount > 0
                    ? DailyQuestReadiness.Ready
                    : blockedQuestCount > 0
                        ? DailyQuestReadiness.LockedOrUnavailable
                        : DailyQuestReadiness.NoneAvailable;

            return new DailyQuestStatus(
                readiness,
                readyQuestCount,
                acceptedQuestCount,
                completedQuestCount,
                blockedQuestCount,
                readyQuestCount > 0 || acceptedQuestCount > 0,
                true,
                BuildDailyStatusMessage(readyQuestCount, acceptedQuestCount, completedQuestCount, blockedQuestCount, hadQuestDataError));
        }
        catch (IpcError)
        {
            return new DailyQuestStatus(
                DailyQuestReadiness.Unavailable,
                0,
                acceptedQuestCount,
                completedQuestCount,
                0,
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

            var result = StartNextAvailableDaily(society);
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
            return this.stop.InvokeFunc("SocietalReputation")
                ? new AutomationResult(true, "Stopped Questionable automation.")
                : new AutomationResult(false, "Questionable did not stop automation.");
        }
        catch (IpcError)
        {
            return new AutomationResult(false, "Questionable IPC is unavailable.");
        }
    }

    private static string BuildDailyStatusMessage(int readyQuestCount, int acceptedQuestCount, int completedQuestCount, int blockedQuestCount, bool hadQuestDataError)
    {
        if (acceptedQuestCount > 0)
        {
            if (completedQuestCount > 0)
            {
                return $"{completedQuestCount}/{acceptedQuestCount} accepted quest(s) completed.";
            }

            return $"{acceptedQuestCount} accepted quest(s) in progress.";
        }

        if (readyQuestCount > 0)
        {
            return $"{readyQuestCount} quest(s) ready to start.";
        }

        if (hadQuestDataError)
        {
            return QuestionableQuestDataError;
        }

        return blockedQuestCount > 0
            ? $"{blockedQuestCount} quest(s) locked or unavailable."
            : "No daily quest available.";
    }

    private static bool TryInvokeQuestCheck(ICallGateSubscriber<string, bool> subscriber, string quest, out bool result)
    {
        try
        {
            result = subscriber.InvokeFunc(quest);
            return true;
        }
        catch (IpcError)
        {
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
}
