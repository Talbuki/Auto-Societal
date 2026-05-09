namespace SocietalReputation.Models;

public sealed record SocietyInfo(
    EAlliedSociety Id,
    string Name,
    string Expansion,
    string Activity,
    bool UsesArrAlliedRank,
    ushort DailyQuestStart,
    ushort DailyQuestEnd,
    IReadOnlyList<ReputationRank> Ranks);
