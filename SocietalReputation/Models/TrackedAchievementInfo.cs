namespace SocietalReputation.Models;

public sealed record TrackedAchievementInfo(
    uint Id,
    string Name,
    bool IsCompleted);
