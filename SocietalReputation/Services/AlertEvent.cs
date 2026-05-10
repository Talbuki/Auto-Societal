namespace SocietalReputation.Services;

internal sealed record AlertEvent(
    AlertType Type,
    string Title,
    string Message,
    string DedupKey,
    TimeSpan Cooldown);
