using Dalamud.Configuration;
using Dalamud.Plugin;

namespace SocietalReputation;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool IsMainWindowOpen { get; set; }

    public bool ShowCompletedDailies { get; set; } = true;

    public bool OnlyShowActionableSocieties { get; set; }

    public ActivityFilter PreferredActivityFilter { get; set; } = ActivityFilter.All;

    public SocietySortMode SortMode { get; set; } = SocietySortMode.Recommended;

    public void Save(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.SavePluginConfig(this);
    }
}
