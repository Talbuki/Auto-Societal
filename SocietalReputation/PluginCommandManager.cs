using Dalamud.Game.Command;
using Dalamud.Plugin.Services;

namespace SocietalReputation;

public sealed class PluginCommandManager : IDisposable
{
    public const string CommandName = "/socrep";

    private readonly ICommandManager commandManager;

    public PluginCommandManager(ICommandManager commandManager, Action toggleMainWindow)
    {
        this.commandManager = commandManager;
        this.commandManager.AddHandler(CommandName, new CommandInfo((_, _) => toggleMainWindow())
        {
            HelpMessage = "Toggle the Societal Reputation window.",
        });
    }

    public void Dispose()
    {
        this.commandManager.RemoveHandler(CommandName);
    }
}
