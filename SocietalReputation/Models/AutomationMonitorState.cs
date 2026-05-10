namespace SocietalReputation.Models;

public sealed record AutomationMonitorState(
    bool IsRunning,
    EAlliedSociety? TargetSocietyId,
    string? TargetSocietyName,
    DateTime? LastStartedUtc,
    DateTime? LastProgressUtc,
    DateTime? LastResultUtc,
    bool LastResultSucceeded,
    string LastMessage);
