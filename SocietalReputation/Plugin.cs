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
    private readonly MainWindow mainWindow;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager)
    {
        this.pluginInterface = pluginInterface;

        Configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.reputationService = new ReputationService();
        this.automationService = new QuestionableAutomationService(pluginInterface);
        this.mainWindow = new MainWindow(Configuration, pluginInterface, this.reputationService, this.automationService)
        {
            IsOpen = Configuration.IsMainWindowOpen,
        };

        this.windowSystem.AddWindow(this.mainWindow);

        this.commandManager = new PluginCommandManager(commandManager, ToggleMainWindow);

        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;
        pluginInterface.UiBuilder.OpenConfigUi += ToggleMainWindow;
    }

    internal Configuration Configuration { get; }

    public void Dispose()
    {
        Configuration.IsMainWindowOpen = this.mainWindow.IsOpen;
        Configuration.Save(this.pluginInterface);

        this.pluginInterface.UiBuilder.Draw -= Draw;
        this.pluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;
        this.pluginInterface.UiBuilder.OpenConfigUi -= ToggleMainWindow;

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
