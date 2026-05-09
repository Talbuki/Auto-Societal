namespace SocietalReputation.Models;

public sealed record SocietyPlannerRow(
    SocietyProgress Progress,
    DailyQuestStatus DailyStatus)
{
    public bool IsActionable => Progress.IsUnlocked && DailyStatus.CanStartNextQuest;
}
