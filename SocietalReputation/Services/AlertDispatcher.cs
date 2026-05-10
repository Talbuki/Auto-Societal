using Dalamud.Game.Gui.Toast;
using Dalamud.Plugin.Services;

namespace SocietalReputation.Services;

internal sealed class AlertDispatcher
{
    private readonly Configuration configuration;
    private readonly IChatGui chatGui;
    private readonly IToastGui toastGui;
    private readonly Dictionary<string, DateTime> lastFiredByKey = new(StringComparer.Ordinal);

    public AlertDispatcher(Configuration configuration, IChatGui chatGui, IToastGui toastGui)
    {
        this.configuration = configuration;
        this.chatGui = chatGui;
        this.toastGui = toastGui;
    }

    public void Dispatch(AlertEvent alert, DateTime utcNow)
    {
        if (!IsEnabled(alert.Type))
        {
            return;
        }

        if (this.lastFiredByKey.TryGetValue(alert.DedupKey, out var lastFiredUtc)
            && utcNow - lastFiredUtc < alert.Cooldown)
        {
            return;
        }

        this.lastFiredByKey[alert.DedupKey] = utcNow;
        var combinedMessage = $"{alert.Title}: {alert.Message}";

        if (this.configuration.EnableToastAlerts)
        {
            this.toastGui.ShowNormal(combinedMessage, new ToastOptions());
        }

        if (this.configuration.EnableChatAlerts)
        {
            this.chatGui.Print(combinedMessage, "SocietalReputation");
        }
    }

    private bool IsEnabled(AlertType type)
    {
        return type switch
        {
            AlertType.DailyReset => this.configuration.NotifyDailyReset,
            AlertType.SocietyUnlocked => this.configuration.NotifySocietyUnlocked,
            AlertType.RankUpAvailable => this.configuration.NotifyRankUpAvailable,
            AlertType.AutomationStalled => this.configuration.NotifyAutomationStalled,
            AlertType.PrerequisiteMet => this.configuration.NotifyPrerequisiteMet,
            _ => false,
        };
    }
}
