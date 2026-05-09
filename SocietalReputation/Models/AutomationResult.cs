namespace SocietalReputation.Models;

public sealed record AutomationResult(
    bool Success,
    string Message,
    int AcceptedQuestCount = 0,
    int? BlockedQuestId = null,
    bool StoppedOnFailure = false);
