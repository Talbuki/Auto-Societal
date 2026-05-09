namespace SocietalReputation.Models;

public sealed record SocietyPlannerRow(
    SocietyProgress Progress,
    DailyQuestStatus DailyStatus,
    SocietyAchievementStatus AchievementStatus)
{
    public bool IsActionable => Progress.IsUnlocked && DailyStatus.CanStartNextQuest;
}
