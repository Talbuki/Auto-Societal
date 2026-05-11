namespace SocietalReputation.Models;

public enum DailyQuestReadiness
{
    Unconfigured,
    Unavailable,
    LockedOrUnavailable,
    PickupPending,
    InProgress,
    ReadyToTurnIn,
    Ready,
    NoneAvailable,
}

public sealed record DailyQuestStatus(
    DailyQuestReadiness Readiness,
    int ReadyQuestCount,
    int AcceptedQuestCount,
    int CompletedQuestCount,
    int BlockedQuestCount,
    bool AllAcceptedQuestsComplete,
    bool CanStartNextQuest,
    bool IsAutomationAvailable,
    string StatusMessage)
{
    public bool HasVisibleActivity => ReadyQuestCount > 0 || AcceptedQuestCount > 0 || CompletedQuestCount > 0;

    public bool NeedsSetup => Readiness is DailyQuestReadiness.Unconfigured or DailyQuestReadiness.Unavailable;
}
