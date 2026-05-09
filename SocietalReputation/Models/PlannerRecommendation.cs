namespace SocietalReputation.Models;

public sealed record PlannerRecommendation(
    SocietyPlannerRow? Row,
    string Summary,
    string Reason);
