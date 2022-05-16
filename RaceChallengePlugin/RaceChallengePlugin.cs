﻿using AssettoServer.Server;
using AssettoServer.Server.Plugin;
using Microsoft.Extensions.Hosting;

namespace RaceChallengePlugin;

public class RaceChallengePlugin : BackgroundService, IAssettoServerAutostart
{
    private readonly EntryCarManager _entryCarManager;
    private readonly Func<EntryCar, EntryCarRace> _entryCarRaceFactory;
    private readonly Dictionary<int, EntryCarRace> _instances = new();
    
    public RaceChallengePlugin(EntryCarManager entryCarManager, Func<EntryCar, EntryCarRace> entryCarRaceFactory)
    {
        _entryCarManager = entryCarManager;
        _entryCarRaceFactory = entryCarRaceFactory;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var entryCar in _entryCarManager.EntryCars)
        {
            _instances.Add(entryCar.SessionId, _entryCarRaceFactory(entryCar));
        }

        return Task.CompletedTask;
    }
    
    internal EntryCarRace GetRace(EntryCar entryCar) => _instances[entryCar.SessionId];
}
