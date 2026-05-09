namespace SocietalReputation.Models;

public sealed record ReputationRank(
    string Name,
    int MinimumReputation,
    int MaximumReputation);
