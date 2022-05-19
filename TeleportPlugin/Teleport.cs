using System.Reflection;
using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using Serilog;

namespace TeleportPlugin;

public class Teleport
{
    private readonly EntryCarManager _entryCarManager;

    public Teleport(
        EntryCarManager entryCarManager, ACServerConfiguration serverConfiguration,
        CSPServerScriptProvider scriptProvider)
    {
        _entryCarManager = entryCarManager;

        if (!serverConfiguration.Extra.EnableClientMessages)
        {
            Log.Error("TeleportModule requires EnableClientMessages");
            return;
        }

        scriptProvider.AddScript(
            new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("TeleportPlugin.lua.teleport-to-location.lua")!).ReadToEnd(),
            "teleport-to-location.lua"
        );
        scriptProvider.AddScript(
            new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("TeleportPlugin.lua.teleport-to-car.lua")!).ReadToEnd(),
            "teleport-to-car.lua"
        );
    }
}
