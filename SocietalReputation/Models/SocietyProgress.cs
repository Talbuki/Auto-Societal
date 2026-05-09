namespace SocietalReputation.Models;

public sealed record SocietyProgress(
    SocietyInfo Society,
    ReputationRank CurrentRank,
    int CurrentReputation,
    bool IsUnlocked,
    bool RankedUpToday,
    int AcceptedDailyQuestCount,
    int CompletedDailyQuestCount,
    int DailyQuestAllowanceTotal)
{
    public int RankReputationRequired => CurrentRank.MaximumReputation <= CurrentRank.MinimumReputation
        ? 1
        : CurrentRank.MaximumReputation - CurrentRank.MinimumReputation;

    public int RankReputationEarned => CurrentRank.MaximumReputation <= CurrentRank.MinimumReputation
        ? 1
        : Math.Clamp(CurrentReputation - CurrentRank.MinimumReputation, 0, RankReputationRequired);

    public float RankProgress => (float)RankReputationEarned / RankReputationRequired;

    public int RemainingDailyQuestSlots => Math.Max(0, DailyQuestAllowanceTotal - AcceptedDailyQuestCount);

    public bool HasDailyQuestSupport => Society.DailyQuestStart != 0 && Society.DailyQuestEnd != 0;

    public bool IsMaxRank => CurrentRank.MaximumReputation == 0;

    public string DailyStatus => HasDailyQuestSupport
        ? $"{AcceptedDailyQuestCount}/{DailyQuestAllowanceTotal} accepted"
        : "No dailies";
}
