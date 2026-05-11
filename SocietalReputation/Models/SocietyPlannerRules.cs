namespace SocietalReputation.Models;

internal static class SocietyPlannerRules
{
    public static bool IsActionable(SocietyProgress progress, DailyQuestStatus dailyStatus)
    {
        return progress.IsUnlocked
            && dailyStatus.CanStartNextQuest
            && dailyStatus.Readiness != DailyQuestReadiness.PickupPending;
    }

    public static bool IsRankUpAvailable(SocietyProgress progress)
    {
        return progress.IsUnlocked
            && !progress.IsMaxRank
            && !progress.RankedUpToday
            && progress.CurrentRank.MaximumReputation > 0
            && progress.CurrentReputation >= progress.CurrentRank.MaximumReputation;
    }
}
