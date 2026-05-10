using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using SocietalReputation.Services;
using SocietalReputation.Windows;

namespace SocietalReputation;

public sealed class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly PluginCommandManager commandManager;
    private readonly WindowSystem windowSystem = new("SocietalReputation");
    private readonly ReputationService reputationService;
    private readonly QuestionableAutomationService automationService;
    private readonly AchievementTrackingService achievementTrackingService;
    private readonly AlertMonitorService alertMonitorService;
    private readonly MainWindow mainWindow;
    private readonly IFramework framework;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IDataManager dataManager,
        IUnlockState unlockState,
        IPlayerState playerState,
        IFramework framework,
        IChatGui chatGui,
        IToastGui toastGui)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;

        Configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.reputationService = new ReputationService();
        this.automationService = new QuestionableAutomationService(pluginInterface);
        this.achievementTrackingService = new AchievementTrackingService(dataManager, unlockState, playerState, Configuration, pluginInterface);
        var alertDispatcher = new AlertDispatcher(Configuration, chatGui, toastGui);
        this.alertMonitorService = new AlertMonitorService(this.reputationService, this.automationService, alertDispatcher);
        this.mainWindow = new MainWindow(Configuration, pluginInterface, this.reputationService, this.automationService, this.achievementTrackingService)
        {
            IsOpen = Configuration.IsMainWindowOpen,
        };

        this.windowSystem.AddWindow(this.mainWindow);

        this.commandManager = new PluginCommandManager(commandManager, ToggleMainWindow);

        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;
        pluginInterface.UiBuilder.OpenConfigUi += ToggleMainWindow;
        this.framework.Update += this.alertMonitorService.OnFrameworkUpdate;
    }

    internal Configuration Configuration { get; }

    public void Dispose()
    {
        Configuration.IsMainWindowOpen = this.mainWindow.IsOpen;
        Configuration.Save(this.pluginInterface);

        this.pluginInterface.UiBuilder.Draw -= Draw;
        this.pluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;
        this.pluginInterface.UiBuilder.OpenConfigUi -= ToggleMainWindow;
        this.framework.Update -= this.alertMonitorService.OnFrameworkUpdate;

        this.commandManager.Dispose();
        this.windowSystem.RemoveAllWindows();
    }

    private void Draw()
    {
        this.windowSystem.Draw();
    }

    private void ToggleMainWindow()
    {
        this.mainWindow.IsOpen = !this.mainWindow.IsOpen;
        Configuration.IsMainWindowOpen = this.mainWindow.IsOpen;
        Configuration.Save(this.pluginInterface);
    }
}
