using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using SocietalReputation.Models;
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
    private AutomationSession? activeSession;
    private DateTime lastAutomationTickUtc = DateTime.MinValue;
    private EAlliedSociety? lastAutomationSocietyId;
    private string? lastAutomationSocietyName;
    private DateTime? lastAutomationStartedUtc;
    private DateTime? lastAutomationProgressUtc;
    private DateTime? lastAutomationResultUtc;
    private bool lastAutomationResultSucceeded;
    private string lastAutomationMessage = "Automation uses Questionable IPC when available.";

    public QuestionableAutomationService(IDalamudPluginInterface pluginInterface)
    {
        this.isRunning = pluginInterface.GetIpcSubscriber<bool>(QuestionableIpc.IsRunning);
        this.startQuest = pluginInterface.GetIpcSubscriber<string, bool>(QuestionableIpc.StartQuest);
        this.stop = pluginInterface.GetIpcSubscriber<string, bool>(QuestionableIpc.Stop);
        this.isQuestLocked = pluginInterface.GetIpcSubscriber<string, bool>(QuestionableIpc.IsQuestLocked);
        this.isReadyToAcceptQuest = pluginInterface.GetIpcSubscriber<string, bool>(QuestionableIpc.IsReadyToAcceptQuest);
        this.isQuestAccepted = pluginInterface.GetIpcSubscriber<string, bool>(QuestionableIpc.IsQuestAccepted);
        this.isQuestComplete = pluginInterface.GetIpcSubscriber<string, bool>(QuestionableIpc.IsQuestComplete);
        this.isQuestUnobtainable = pluginInterface.GetIpcSubscriber<string, bool>(QuestionableIpc.IsQuestUnobtainable);
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
        if (TryGetCachedStatus(this.cachedRunningState, out var isRunningValue))
        {
            return isRunningValue;
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

    public AutomationMonitorState GetMonitorState()
    {
        return new AutomationMonitorState(
            IsRunning(),
            this.lastAutomationSocietyId,
            this.lastAutomationSocietyName,
            this.lastAutomationStartedUtc,
            this.lastAutomationProgressUtc,
            this.lastAutomationResultUtc,
            this.lastAutomationResultSucceeded,
            this.lastAutomationMessage);
    }

    public IReadOnlyDictionary<EAlliedSociety, DailyQuestStatus> GetDailyQuestStatuses(IEnumerable<SocietyInfo> societies)
    {
        var availability = GetAvailabilitySnapshot();
        var statuses = new Dictionary<EAlliedSociety, DailyQuestStatus>();
        foreach (var society in societies)
        {
            statuses[society.Id] = BuildDailyQuestStatus(society, availability);
        }

        return statuses;
    }

    public DailyQuestStatus GetDailyQuestStatus(SocietyInfo society)
    {
        return BuildDailyQuestStatus(society, GetAvailabilitySnapshot());
    }

    public AutomationResult StartOrContinueDaily(SocietyInfo society)
    {
        BeginAutomationTracking(society);

        var status = GetDailyQuestStatus(society);
        if (!status.CanExecuteAction)
        {
            return RecordAutomationResult(new AutomationResult(false, status.StatusMessage));
        }

        return status.RecommendedAction switch
        {
            AutomationAction.AcceptAvailable or AutomationAction.AcceptRemaining => StartAcceptanceSession(society),
            AutomationAction.Continue => ContinueAcceptedQuest(society),
            _ => RecordAutomationResult(new AutomationResult(false, status.StatusMessage)),
        };
    }

    public AutomationResult Stop()
    {
        try
        {
            var result = this.stop.InvokeFunc("SocietalReputation")
                ? new AutomationResult(true, "Stopped Questionable automation.")
                : new AutomationResult(false, "Questionable did not stop automation.");
            InvalidateStatusCache();
            this.activeSession = null;
            ClearAutomationTracking();
            return RecordAutomationResult(result);
        }
        catch (IpcError)
        {
            CacheUnavailable();
            this.activeSession = null;
            return RecordAutomationResult(new AutomationResult(false, "Questionable IPC is unavailable."));
        }
    }

    public void OnFrameworkUpdate(IFramework framework)
    {
        if (this.activeSession == null)
        {
            return;
        }

        var utcNow = framework.LastUpdateUTC;
        if (this.lastAutomationTickUtc != DateTime.MinValue && utcNow - this.lastAutomationTickUtc < QuestAcceptPollInterval)
        {
            return;
        }

        this.lastAutomationTickUtc = utcNow;
        AdvanceActiveSession(utcNow);
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

    private AvailabilitySnapshot GetAvailabilitySnapshot()
    {
        var isAvailable = IsAvailable();
        return new AvailabilitySnapshot(isAvailable, isAvailable && IsRunning());
    }

    private DailyQuestStatus BuildDailyQuestStatus(SocietyInfo society, AvailabilitySnapshot availability)
    {
        var (acceptedQuestCount, completedQuestCount) = GetAcceptedQuestCounts(society);
        if (society.DailyQuestStart == 0 || society.DailyQuestEnd == 0)
        {
            return CreateStatus(
                DailyQuestReadiness.Unconfigured,
                0,
                acceptedQuestCount,
                completedQuestCount,
                0,
                false,
                false,
                availability.IsAvailable,
                AutomationAction.SetupRequired,
                "Setup required",
                "No daily quest range configured.");
        }

        if (!availability.IsAvailable)
        {
            return CreateStatus(
                DailyQuestReadiness.Unavailable,
                0,
                acceptedQuestCount,
                completedQuestCount,
                0,
                false,
                acceptedQuestCount > 0,
                false,
                AutomationAction.Unavailable,
                "Unavailable",
                acceptedQuestCount > 0
                    ? $"{acceptedQuestCount} accepted quest(s) in progress."
                    : "Questionable unavailable.");
        }

        try
        {
            var evaluation = EvaluateSocietyQuests(society);
            if (evaluation.HadQuestDataError)
            {
                return CreateStatus(
                    DailyQuestReadiness.Unavailable,
                    0,
                    acceptedQuestCount,
                    completedQuestCount,
                    0,
                    false,
                    acceptedQuestCount > 0,
                    true,
                    AutomationAction.Unavailable,
                    "Unavailable",
                    QuestionableQuestDataError);
            }

            var pickupComplete = evaluation.AcceptedQuestCount >= PerSocietyDailyQuestLimit || evaluation.ReadyQuestCount == 0;
            var readiness = evaluation.AcceptedQuestCount > 0 && !pickupComplete
                ? DailyQuestReadiness.PickupPending
                : evaluation.AcceptedQuestCount > 0
                    ? evaluation.AllAcceptedQuestsComplete
                        ? DailyQuestReadiness.ReadyToTurnIn
                        : DailyQuestReadiness.InProgress
                    : evaluation.ReadyQuestCount > 0
                        ? DailyQuestReadiness.Ready
                        : evaluation.BlockedQuestCount > 0
                            ? DailyQuestReadiness.LockedOrUnavailable
                            : DailyQuestReadiness.NoneAvailable;
            var recommendedAction = GetRecommendedAction(readiness);

            return CreateStatus(
                readiness,
                evaluation.ReadyQuestCount,
                evaluation.AcceptedQuestCount,
                evaluation.CompletedQuestCount,
                evaluation.BlockedQuestCount,
                evaluation.AllAcceptedQuestsComplete,
                evaluation.CanStartNextQuest,
                true,
                recommendedAction,
                GetRecommendedActionLabel(recommendedAction),
                BuildDailyStatusMessage(evaluation));
        }
        catch (IpcError)
        {
            CacheUnavailable();
            return CreateStatus(
                DailyQuestReadiness.Unavailable,
                0,
                acceptedQuestCount,
                completedQuestCount,
                0,
                false,
                acceptedQuestCount > 0,
                false,
                AutomationAction.Unavailable,
                "Unavailable",
                "Questionable IPC is unavailable.");
        }
        catch (Exception)
        {
            return CreateStatus(
                DailyQuestReadiness.Unavailable,
                0,
                acceptedQuestCount,
                completedQuestCount,
                0,
                false,
                acceptedQuestCount > 0,
                false,
                AutomationAction.Unavailable,
                "Unavailable",
                QuestionableQuestDataError);
        }
    }

    private static DailyQuestStatus CreateStatus(
        DailyQuestReadiness readiness,
        int readyQuestCount,
        int acceptedQuestCount,
        int completedQuestCount,
        int blockedQuestCount,
        bool allAcceptedQuestsComplete,
        bool canStartNextQuest,
        bool isAutomationAvailable,
        AutomationAction recommendedAction,
        string recommendedActionLabel,
        string statusMessage)
    {
        return new DailyQuestStatus(
            readiness,
            readyQuestCount,
            acceptedQuestCount,
            completedQuestCount,
            blockedQuestCount,
            allAcceptedQuestsComplete,
            canStartNextQuest,
            isAutomationAvailable,
            recommendedAction,
            CanExecuteAction(recommendedAction),
            recommendedActionLabel,
            statusMessage);
    }

    private static AutomationAction GetRecommendedAction(DailyQuestReadiness readiness)
    {
        return readiness switch
        {
            DailyQuestReadiness.Ready => AutomationAction.AcceptAvailable,
            DailyQuestReadiness.PickupPending => AutomationAction.AcceptRemaining,
            DailyQuestReadiness.InProgress => AutomationAction.Continue,
            DailyQuestReadiness.ReadyToTurnIn => AutomationAction.HandInReady,
            DailyQuestReadiness.Unconfigured => AutomationAction.SetupRequired,
            DailyQuestReadiness.Unavailable => AutomationAction.Unavailable,
            _ => AutomationAction.None,
        };
    }

    private static bool CanExecuteAction(AutomationAction action)
    {
        return action is AutomationAction.AcceptAvailable or AutomationAction.AcceptRemaining or AutomationAction.Continue;
    }

    private static string GetRecommendedActionLabel(AutomationAction action)
    {
        return action switch
        {
            AutomationAction.AcceptAvailable => "Accept available",
            AutomationAction.AcceptRemaining => "Accept remaining",
            AutomationAction.Continue => "Continue daily",
            AutomationAction.HandInReady => "Hand-in ready",
            AutomationAction.SetupRequired => "Setup required",
            AutomationAction.Unavailable => "Unavailable",
            _ => "No action",
        };
    }

    private AutomationResult StartAcceptanceSession(SocietyInfo society)
    {
        this.activeSession = new AutomationSession(society, AutomationSessionPhase.Evaluate, 0, null, 0, DateTime.UtcNow);
        var immediateResult = AdvanceActiveSession(DateTime.UtcNow);
        return immediateResult ?? new AutomationResult(true, $"Started {society.Name} automation.");
    }

    private AutomationResult ContinueAcceptedQuest(SocietyInfo society)
    {
        try
        {
            var evaluation = EvaluateSocietyQuests(society);
            if (evaluation.HadQuestDataError)
            {
                return RecordAutomationResult(new AutomationResult(false, QuestionableQuestDataError));
            }

            if (evaluation.FirstAcceptedIncompleteQuestId is not ushort questToResumeId)
            {
                return RecordAutomationResult(new AutomationResult(false, $"{society.Name} has accepted quests in progress, but none could be resumed.", evaluation.AcceptedQuestCount));
            }

            if (!this.startQuest.InvokeFunc(questToResumeId.ToString()))
            {
                InvalidateStatusCache();
                return RecordAutomationResult(BuildAcceptanceFailureResult(
                    society,
                    0,
                    questToResumeId,
                    "Questionable could not resume the accepted quest."));
            }

            InvalidateStatusCache();
            MarkAutomationProgress();
            return RecordAutomationResult(new AutomationResult(
                true,
                BuildAutomationProgressMessage(society, evaluation.AcceptedQuestCount, 0, "Continuing accepted quests."),
                evaluation.AcceptedQuestCount));
        }
        catch (IpcError)
        {
            CacheUnavailable();
            return RecordAutomationResult(new AutomationResult(false, "Questionable IPC is unavailable. Install/enable Questionable and complete its setup."));
        }
    }

    private AutomationResult? AdvanceActiveSession(DateTime utcNow)
    {
        var session = this.activeSession;
        if (session == null)
        {
            return null;
        }

        try
        {
            return session.Phase switch
            {
                AutomationSessionPhase.Evaluate => EvaluateAutomationSession(session, utcNow),
                AutomationSessionPhase.WaitingForAcceptance => WaitForQuestAcceptance(session, utcNow),
                _ => null,
            };
        }
        catch (IpcError)
        {
            CacheUnavailable();
            return FinishActiveSession(new AutomationResult(false, "Questionable IPC is unavailable. Install/enable Questionable and complete its setup."));
        }
    }

    private AutomationResult? EvaluateAutomationSession(AutomationSession session, DateTime utcNow)
    {
        var evaluation = EvaluateSocietyQuests(session.Society);
        if (evaluation.HadQuestDataError)
        {
            return FinishActiveSession(new AutomationResult(false, QuestionableQuestDataError));
        }

        var canAcceptMore = evaluation.AcceptedQuestCount < PerSocietyDailyQuestLimit;
        if (canAcceptMore && evaluation.FirstReadyQuestId is ushort readyQuestId)
        {
            if (!this.startQuest.InvokeFunc(readyQuestId.ToString()))
            {
                InvalidateStatusCache();
                return FinishActiveSession(BuildAcceptanceFailureResult(
                    session.Society,
                    session.AcceptedThisRun,
                    readyQuestId,
                    "Questionable could not start the ready quest."));
            }

            InvalidateStatusCache();
            this.activeSession = session with
            {
                Phase = AutomationSessionPhase.WaitingForAcceptance,
                PendingQuestId = readyQuestId,
                AcceptedQuestCountBeforeStart = evaluation.AcceptedQuestCount,
                WaitStartedUtc = utcNow,
            };
            SetAutomationMessage($"{session.Society.Name}: started quest {readyQuestId}, waiting for acceptance.");
            return new AutomationResult(
                true,
                $"{session.Society.Name}: starting available quests. Waiting for pickup confirmation.",
                evaluation.AcceptedQuestCount);
        }

        if (evaluation.AcceptedQuestCount > 0 && evaluation.FirstReadyQuestId is not null)
        {
            return FinishActiveSession(new AutomationResult(
                session.AcceptedThisRun > 0,
                BuildAutomationProgressMessage(
                    session.Society,
                    evaluation.AcceptedQuestCount,
                    session.AcceptedThisRun,
                    "Waiting for remaining pickups to become accept-ready."),
                evaluation.AcceptedQuestCount));
        }

        if (evaluation.AcceptedQuestCount > 0 && !evaluation.AllAcceptedQuestsComplete)
        {
            if (evaluation.FirstAcceptedIncompleteQuestId is not ushort questToResumeId)
            {
                return FinishActiveSession(new AutomationResult(
                    false,
                    $"{session.Society.Name} has accepted quests in progress, but none could be resumed.",
                    evaluation.AcceptedQuestCount));
            }

            if (!this.startQuest.InvokeFunc(questToResumeId.ToString()))
            {
                InvalidateStatusCache();
                return FinishActiveSession(BuildAcceptanceFailureResult(
                    session.Society,
                    session.AcceptedThisRun,
                    questToResumeId,
                    "Questionable could not resume the accepted quest."));
            }

            InvalidateStatusCache();
            MarkAutomationProgress();
            return FinishActiveSession(new AutomationResult(
                true,
                BuildAutomationProgressMessage(session.Society, evaluation.AcceptedQuestCount, session.AcceptedThisRun, "Continuing accepted quests after all available pickups."),
                evaluation.AcceptedQuestCount));
        }

        if (evaluation.AcceptedQuestCount > 0 && evaluation.AllAcceptedQuestsComplete)
        {
            MarkAutomationProgress();
            return FinishActiveSession(new AutomationResult(
                true,
                $"{session.Society.Name} accepted quests are complete and ready to hand in.",
                evaluation.AcceptedQuestCount));
        }

        if (session.AcceptedThisRun > 0)
        {
            return FinishActiveSession(new AutomationResult(
                true,
                BuildAutomationProgressMessage(session.Society, evaluation.AcceptedQuestCount, session.AcceptedThisRun, "Finish accepted quests before turn-in."),
                evaluation.AcceptedQuestCount));
        }

        return FinishActiveSession(new AutomationResult(false, $"No available {session.Society.Name} daily quest was found."));
    }

    private AutomationResult? WaitForQuestAcceptance(AutomationSession session, DateTime utcNow)
    {
        if (session.PendingQuestId is not ushort questId)
        {
            return FinishActiveSession(new AutomationResult(false, $"{session.Society.Name} automation lost the pending quest state."));
        }

        var evaluation = EvaluateSocietyQuests(session.Society);
        if (evaluation.HadQuestDataError)
        {
            return FinishActiveSession(new AutomationResult(false, QuestionableQuestDataError));
        }

        if (evaluation.IsAccepted(questId) || evaluation.AcceptedQuestCount > session.AcceptedQuestCountBeforeStart)
        {
            MarkAutomationProgress();
            this.activeSession = session with
            {
                Phase = AutomationSessionPhase.Evaluate,
                AcceptedThisRun = session.AcceptedThisRun + 1,
                PendingQuestId = null,
            };
            SetAutomationMessage(BuildAutomationProgressMessage(
                session.Society,
                evaluation.AcceptedQuestCount,
                session.AcceptedThisRun + 1,
                "Continuing automation."));
            return null;
        }

        if (utcNow - session.WaitStartedUtc >= QuestAcceptTimeout)
        {
            return FinishActiveSession(BuildAcceptanceFailureResult(
                session.Society,
                session.AcceptedThisRun,
                questId,
                "Timed out waiting for the quest to be accepted."));
        }

        return null;
    }

    private void BeginAutomationTracking(SocietyInfo society)
    {
        var utcNow = DateTime.UtcNow;
        this.lastAutomationSocietyId = society.Id;
        this.lastAutomationSocietyName = society.Name;
        this.lastAutomationStartedUtc = utcNow;
        this.lastAutomationProgressUtc = null;
        this.lastAutomationMessage = $"Attempting automation for {society.Name}.";
    }

    private void MarkAutomationProgress()
    {
        this.lastAutomationProgressUtc = DateTime.UtcNow;
    }

    private void SetAutomationMessage(string message)
    {
        this.lastAutomationMessage = message;
    }

    private AutomationResult RecordAutomationResult(AutomationResult result)
    {
        this.lastAutomationResultUtc = DateTime.UtcNow;
        this.lastAutomationResultSucceeded = result.Success;
        this.lastAutomationMessage = result.Message;
        return result;
    }

    private AutomationResult FinishActiveSession(AutomationResult result)
    {
        this.activeSession = null;
        return RecordAutomationResult(result);
    }

    private void ClearAutomationTracking()
    {
        this.lastAutomationSocietyId = null;
        this.lastAutomationSocietyName = null;
        this.lastAutomationStartedUtc = null;
        this.lastAutomationProgressUtc = null;
    }

    private static string BuildDailyStatusMessage(SocietyQuestEvaluation evaluation)
    {
        if (evaluation.AcceptedQuestCount > 0)
        {
            var pickupComplete = evaluation.AcceptedQuestCount >= PerSocietyDailyQuestLimit || evaluation.ReadyQuestCount == 0;
            if (!pickupComplete)
            {
                return $"{evaluation.AcceptedQuestCount}/{PerSocietyDailyQuestLimit} accepted, {evaluation.ReadyQuestCount} still ready to accept before continuing objectives.";
            }

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
                ? $"{evaluation.AcceptedQuestCount}/{PerSocietyDailyQuestLimit} accepted, pick up remaining available quests before continuing objectives."
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

    private static class QuestionableIpc
    {
        public const string IsRunning = "Questionable.IsRunning";
        public const string StartQuest = "Questionable.StartQuest";
        public const string Stop = "Questionable.Stop";
        public const string IsQuestLocked = "Questionable.IsQuestLocked";
        public const string IsReadyToAcceptQuest = "Questionable.IsReadyToAcceptQuest";
        public const string IsQuestAccepted = "Questionable.IsQuestAccepted";
        public const string IsQuestComplete = "Questionable.IsQuestComplete";
        public const string IsQuestUnobtainable = "Questionable.IsQuestUnobtainable";
    }

    private sealed record CachedStatus(bool Value, DateTime ExpiresAtUtc);

    private sealed record AvailabilitySnapshot(bool IsAvailable, bool IsRunning);

    private sealed record QuestState(ushort QuestId, bool IsAccepted);

    private sealed record AutomationSession(
        SocietyInfo Society,
        AutomationSessionPhase Phase,
        int AcceptedThisRun,
        ushort? PendingQuestId,
        int AcceptedQuestCountBeforeStart,
        DateTime WaitStartedUtc);

    private enum AutomationSessionPhase
    {
        Evaluate,
        WaitingForAcceptance,
    }

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
