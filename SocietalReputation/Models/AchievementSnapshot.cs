namespace SocietalReputation.Models;

public sealed record AchievementSnapshot(
    IReadOnlyDictionary<EAlliedSociety, SocietyAchievementStatus> Societies,
    int CompletedAchievementCount,
    int TotalAchievementCount,
    int FullyCompletedSocietyCount,
    bool IsAchievementListLoaded,
    string StatusMessage)
{
    public SocietyAchievementStatus GetStatus(EAlliedSociety societyId)
    {
        return this.Societies.TryGetValue(societyId, out var status)
            ? status
            : SocietyAchievementStatus.CreateUnknown([]);
    }
}
