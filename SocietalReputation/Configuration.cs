using Dalamud.Configuration;
using Dalamud.Plugin;
using SocietalReputation.Models;

namespace SocietalReputation;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool IsMainWindowOpen { get; set; }

    public bool IsAdvancedModeEnabled { get; set; }

    public bool ShowCompletedDailies { get; set; } = true;

    public bool OnlyShowActionableSocieties { get; set; } = true;

    public ActivityFilter PreferredActivityFilter { get; set; } = ActivityFilter.All;

    public SocietySortMode SortMode { get; set; } = SocietySortMode.ClosestToRankUp;

    public bool SortAscending { get; set; } = true;

    public bool ShowOnboardingWalkthrough { get; set; } = true;

    public bool OnboardingDismissed { get; set; }

    public bool EnableToastAlerts { get; set; }

    public bool EnableChatAlerts { get; set; }

    public bool NotifyDailyReset { get; set; }

    public bool NotifySocietyUnlocked { get; set; }

    public bool NotifyRankUpAvailable { get; set; }

    public bool NotifyAutomationStalled { get; set; }

    public bool NotifyPrerequisiteMet { get; set; }

    public bool EnableAutomaticStartTime { get; set; }

    public int AutomaticStartHourLocal { get; set; } = 15;

    public int AutomaticStartMinuteLocal { get; set; }

    public List<EAlliedSociety> AutomaticStartSocietyOrder { get; set; } = [];

    public DateOnly? LastAutomaticStartDate { get; set; }

    public Dictionary<string, CharacterAchievementCache> AchievementCacheByCharacter { get; set; } = new(StringComparer.Ordinal);

    public void Save(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.SavePluginConfig(this);
    }
}

[Serializable]
public sealed class CharacterAchievementCache
{
    public Dictionary<EAlliedSociety, CachedSocietyAchievementStatus> Societies { get; set; } = [];

    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
}

[Serializable]
public sealed class CachedSocietyAchievementStatus
{
    public SocietyAchievementState State { get; set; } = SocietyAchievementState.Unknown;

    public int CompletedCount { get; set; }

    public int TotalCount { get; set; }
}
