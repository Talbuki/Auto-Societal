namespace SocietalReputation.Models;

public sealed record ReputationSnapshot(
    IReadOnlyList<SocietyProgress> Progress,
    int RemainingAllowances,
    int TotalAllowances,
    int AcceptedDailyQuests);
