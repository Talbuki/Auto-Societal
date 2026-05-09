namespace SocietalReputation.Models;

public enum SocietyAchievementState
{
    Unknown,
    Incomplete,
    Partial,
    Complete,
}

public sealed record SocietyAchievementStatus(
    SocietyAchievementState State,
    int CompletedCount,
    int TotalCount,
    IReadOnlyList<TrackedAchievementInfo> Achievements,
    string StatusMessage)
{
    public static SocietyAchievementStatus CreateUnknown(IReadOnlyList<TrackedAchievementInfo> achievements)
    {
        return new SocietyAchievementStatus(
            SocietyAchievementState.Unknown,
            0,
            achievements.Count,
            achievements,
            achievements.Count == 0
                ? "No tracked achievements."
                : "Achievement data unavailable.");
    }
}
