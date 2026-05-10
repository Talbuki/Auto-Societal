using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using SocietalReputation.Models;

namespace SocietalReputation.Services;

public sealed class AchievementTrackingService
{
    private const string AchievementListSetupMessage = "Open the in-game Achievements window once to load tracked achievement data.";
    private const string CachedAchievementMessage = "Showing last known achievement progress for this character. Open the in-game Achievements window to refresh.";

    private readonly IUnlockState unlockState;
    private readonly IPlayerState playerState;
    private readonly Configuration configuration;
    private readonly IDalamudPluginInterface pluginInterface;
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

    public AchievementTrackingService(
        IDataManager dataManager,
        IUnlockState unlockState,
        IPlayerState playerState,
        Configuration configuration,
        IDalamudPluginInterface pluginInterface)
    {
        this.unlockState = unlockState;
        this.playerState = playerState;
        this.configuration = configuration;
        this.pluginInterface = pluginInterface;
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
        var characterCache = GetActiveCharacterCache();
        var usedCachedData = false;
        var isLoaded = false;
        var cacheChanged = false;

        try
        {
            isLoaded = this.unlockState.IsAchievementListLoaded;
            foreach (var society in societies)
            {
                var usedCacheForSociety = false;
                var status = isLoaded
                    ? BuildLoadedStatus(society.Id)
                    : BuildCachedOrUnknownStatus(society.Id, characterCache, out usedCacheForSociety);

                statuses[society.Id] = status;
                totalCompleted += status.CompletedCount;
                totalTracked += status.TotalCount;
                usedCachedData |= usedCacheForSociety;

                if (isLoaded)
                {
                    cacheChanged |= UpdateCharacterCacheEntry(characterCache, society.Id, status);
                }

                if (status.State == SocietyAchievementState.Complete)
                {
                    fullyCompletedSocieties++;
                }
            }

            if (isLoaded && cacheChanged)
            {
                SaveConfiguration();
            }

            var statusMessage = isLoaded
                ? $"{totalCompleted}/{totalTracked} tracked achievement milestones complete."
                : usedCachedData
                    ? CachedAchievementMessage
                    : AchievementListSetupMessage;

            return new AchievementSnapshot(
                statuses,
                totalCompleted,
                totalTracked,
                fullyCompletedSocieties,
                isLoaded,
                statusMessage);
        }
        catch (Exception)
        {
            foreach (var society in societies)
            {
                var status = BuildCachedOrUnknownStatus(society.Id, characterCache, out var usedCacheForSociety);
                statuses[society.Id] = status;
                totalCompleted += status.CompletedCount;
                totalTracked += status.TotalCount;
                usedCachedData |= usedCacheForSociety;
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
                false,
                usedCachedData
                    ? CachedAchievementMessage
                    : AchievementListSetupMessage);
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

    private SocietyAchievementStatus BuildCachedOrUnknownStatus(
        EAlliedSociety societyId,
        CharacterAchievementCache? characterCache,
        out bool usedCachedData)
    {
        usedCachedData = false;
        if (characterCache == null || !characterCache.Societies.TryGetValue(societyId, out var cachedStatus))
        {
            return BuildUnknownStatus(societyId);
        }

        usedCachedData = true;
        var trackedAchievements = GetTrackedAchievements(societyId, allowCompletionChecks: false);
        var cappedTotal = Math.Max(0, Math.Min(cachedStatus.TotalCount, trackedAchievements.Count));
        var cappedCompleted = Math.Max(0, Math.Min(cachedStatus.CompletedCount, cappedTotal));
        var state = cappedTotal == 0
            ? SocietyAchievementState.Unknown
            : cappedCompleted == 0
                ? SocietyAchievementState.Incomplete
                : cappedCompleted >= cappedTotal
                    ? SocietyAchievementState.Complete
                    : SocietyAchievementState.Partial;
        var statusMessage = cappedTotal == 0
            ? "No tracked achievements."
            : $"Achievements: {cappedCompleted}/{cappedTotal} complete (last known)";

        return new SocietyAchievementStatus(
            state,
            cappedCompleted,
            cappedTotal,
            trackedAchievements,
            statusMessage);
    }

    private CharacterAchievementCache? GetActiveCharacterCache()
    {
        var key = GetCharacterKey();
        if (key == null)
        {
            return null;
        }

        if (!this.configuration.AchievementCacheByCharacter.TryGetValue(key, out var cache))
        {
            cache = new CharacterAchievementCache();
            this.configuration.AchievementCacheByCharacter[key] = cache;
        }

        return cache;
    }

    private string? GetCharacterKey()
    {
        if (!this.playerState.IsLoaded || this.playerState.ContentId == 0)
        {
            return null;
        }

        var worldId = this.playerState.HomeWorld.RowId;
        return $"{this.playerState.ContentId}:{worldId}";
    }

    private static bool UpdateCharacterCacheEntry(
        CharacterAchievementCache? characterCache,
        EAlliedSociety societyId,
        SocietyAchievementStatus status)
    {
        if (characterCache == null)
        {
            return false;
        }

        var changed = !characterCache.Societies.TryGetValue(societyId, out var existing)
            || existing.State != status.State
            || existing.CompletedCount != status.CompletedCount
            || existing.TotalCount != status.TotalCount;

        characterCache.Societies[societyId] = new CachedSocietyAchievementStatus
        {
            State = status.State,
            CompletedCount = status.CompletedCount,
            TotalCount = status.TotalCount,
        };

        if (changed)
        {
            characterCache.LastUpdatedUtc = DateTime.UtcNow;
        }

        return changed;
    }

    private void SaveConfiguration()
    {
        this.configuration.Save(this.pluginInterface);
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
