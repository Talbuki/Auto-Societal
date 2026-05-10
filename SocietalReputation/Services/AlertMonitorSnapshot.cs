using SocietalReputation.Models;

namespace SocietalReputation.Services;

internal sealed record AlertMonitorSnapshot(
    int RemainingAllowances,
    AutomationMonitorState Automation,
    Dictionary<EAlliedSociety, AlertSocietyState> Societies);

internal sealed record AlertSocietyState(
    EAlliedSociety SocietyId,
    string Name,
    bool IsUnlocked,
    bool IsActionable,
    bool IsRankUpAvailable,
    int AcceptedQuestCount,
    int CompletedQuestCount);
