using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using SocietalReputation.Models;

namespace SocietalReputation.Services;

public sealed class AchievementTrackingService
{
    private const string AchievementListSetupMessage = "Open the in-game Achievements window once to load tracked achievement data.";

    private readonly IUnlockState unlockState;
    private readonly IReadOnlyDictionary<EAlliedSociety, uint[]> trackedAchievementIds =
        new Dictionary<EAlliedSociety, uint[]>
        {
            [EAlliedSociety.Amaljaa] = [863, 864, 865, 866],
            [EAlliedSociety.Sylphs] = [867, 868, 869, 870],
            [EAlliedSociety.Kobolds] = [904, 905, 906, 907],
            [EAlliedSociety.Sahagin] = [908, 909, 910, 911],
            [EAlliedSociety.Ixal] = [1022, 1023, 1024, 1025],
            [EAlliedSociety.VanuVanu] = [1395, 1396, 1397, 1398],
            [EAlliedSociety.Vath] = [1495, 1496, 1497, 1498],
            [EAlliedSociety.Moogles] = [1618, 1619, 1620, 1621],
            [EAlliedSociety.Kojin] = [1997, 1998, 1999, 2000],
            [EAlliedSociety.Ananta] = [2014, 2015, 2016, 2017],
            [EAlliedSociety.Namazu] = [2099, 2100, 2101, 2102],
            [EAlliedSociety.Pixies] = [2436, 2437, 2438, 2439],
            [EAlliedSociety.Qitari] = [2597, 2598, 2599, 2600],
            [EAlliedSociety.Dwarves] = [2638, 2639, 2640, 2641],
            [EAlliedSociety.Arkasodara] = [3055, 3056, 3057, 3058],
            [EAlliedSociety.Omicrons] = [3123, 3124, 3125, 3126],
            [EAlliedSociety.Loporrits] = [3188, 3189, 3190, 3191],
            [EAlliedSociety.Pelupelu] = [3588, 3589, 3590, 3591],
            [EAlliedSociety.MamoolJa] = [3621, 3622, 3623, 3624],
            [EAlliedSociety.YokHuy] = [3774, 3775, 3776, 3777],
        };

    private readonly IReadOnlyDictionary<uint, Achievement> achievementsById;

    public AchievementTrackingService(IDataManager dataManager, IUnlockState unlockState)
    {
        this.unlockState = unlockState;
        this.achievementsById = dataManager.GetExcelSheet<Achievement>()?
            .ToDictionary(achievement => achievement.RowId)
            ?? new Dictionary<uint, Achievement>();
    }

    public AchievementSnapshot GetSnapshot(IEnumerable<SocietyInfo> societies)
    {
        var statuses = new Dictionary<EAlliedSociety, SocietyAchievementStatus>();
        var totalCompleted = 0;
        var totalTracked = 0;
        var fullyCompletedSocieties = 0;

        try
        {
            var isLoaded = this.unlockState.IsAchievementListLoaded;
            foreach (var society in societies)
            {
                var status = isLoaded
                    ? BuildLoadedStatus(society.Id)
                    : BuildUnknownStatus(society.Id);

                statuses[society.Id] = status;
                totalCompleted += status.CompletedCount;
                totalTracked += status.TotalCount;
                if (status.State == SocietyAchievementState.Complete)
                {
                    fullyCompletedSocieties++;
                }
            }

            return new AchievementSnapshot(
                statuses,
                totalCompleted,
                totalTracked,
                fullyCompletedSocieties,
                isLoaded,
                isLoaded
                    ? $"{totalCompleted}/{totalTracked} tracked achievement milestones complete."
                    : AchievementListSetupMessage);
        }
        catch (Exception)
        {
            foreach (var society in societies)
            {
                var status = BuildUnknownStatus(society.Id);
                statuses[society.Id] = status;
                totalTracked += status.TotalCount;
            }

            return new AchievementSnapshot(
                statuses,
                0,
                totalTracked,
                0,
                false,
                AchievementListSetupMessage);
        }
    }

    private SocietyAchievementStatus BuildLoadedStatus(EAlliedSociety societyId)
    {
        var trackedAchievements = GetTrackedAchievements(societyId, allowCompletionChecks: true);
        var completedCount = trackedAchievements.Count(achievement => achievement.IsCompleted);
        var totalCount = trackedAchievements.Count;
        var state = completedCount switch
        {
            0 when totalCount == 0 => SocietyAchievementState.Unknown,
            0 => SocietyAchievementState.Incomplete,
            _ when completedCount >= totalCount => SocietyAchievementState.Complete,
            _ => SocietyAchievementState.Partial,
        };

        var statusMessage = totalCount == 0
            ? "No tracked achievements."
            : $"Achievements: {completedCount}/{totalCount} complete";

        return new SocietyAchievementStatus(state, completedCount, totalCount, trackedAchievements, statusMessage);
    }

    private SocietyAchievementStatus BuildUnknownStatus(EAlliedSociety societyId)
    {
        return SocietyAchievementStatus.CreateUnknown(GetTrackedAchievements(societyId, allowCompletionChecks: false));
    }

    private IReadOnlyList<TrackedAchievementInfo> GetTrackedAchievements(EAlliedSociety societyId, bool allowCompletionChecks)
    {
        if (!this.trackedAchievementIds.TryGetValue(societyId, out var achievementIds))
        {
            return [];
        }

        var achievements = new List<TrackedAchievementInfo>(achievementIds.Length);
        foreach (var achievementId in achievementIds)
        {
            if (!this.achievementsById.TryGetValue(achievementId, out var achievement))
            {
                achievements.Add(new TrackedAchievementInfo(achievementId, $"Achievement {achievementId}", false));
                continue;
            }

            var isCompleted = allowCompletionChecks && this.unlockState.IsAchievementComplete(achievement);
            achievements.Add(new TrackedAchievementInfo(achievement.RowId, achievement.Name.ToString(), isCompleted));
        }

        return achievements;
    }
}
