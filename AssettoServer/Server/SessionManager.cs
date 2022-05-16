﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AssettoServer.Network.Packets.Outgoing;
using AssettoServer.Network.Tcp;
using AssettoServer.Server.Configuration;
using Serilog;

namespace AssettoServer.Server;

public class SessionManager
{
    private readonly ACServerConfiguration _configuration;
    private readonly Func<SessionConfiguration, SessionState> _sessionStateFactory;
    private readonly Stopwatch _timeSource = new();
    private readonly EntryCarManager _entryCarManager;

    public int CurrentSessionIndex { get; private set; } = -1;
    public bool IsLastRaceInverted { get; private set; } = false;
    public bool MustInvertGrid { get; private set; } = false;
    public SessionState CurrentSession { get; private set; } = null!;

    public long ServerTimeMilliseconds => _timeSource.ElapsedMilliseconds;
    
    /// <summary>
    /// Fires when a new session is started
    /// </summary>
    public event EventHandler<SessionManager, SessionChangedEventArgs>? SessionChanged;
    
    public SessionManager(ACServerConfiguration configuration, Func<SessionConfiguration, SessionState> sessionStateFactory, EntryCarManager entryCarManager)
    {
        _configuration = configuration;
        _sessionStateFactory = sessionStateFactory;
        _entryCarManager = entryCarManager;
    }

    internal void Initialize()
    {
        _timeSource.Start();
        NextSession();
    }

    public void NextSession()
    {
        // TODO StallSessionSwitch
        // TODO reset sun angle

        if (_configuration.Sessions.Count - 1 == CurrentSessionIndex)
        {
            if (_configuration.Server.Loop)
            {
                Log.Information("Looping sessions");
            }
            else if (CurrentSession.Configuration.Type != SessionType.Race || _configuration.Server.InvertedGridPositions == 0 || IsLastRaceInverted)
            {
                // TODO exit
            }

            if (CurrentSession.Configuration.Type == SessionType.Race && _configuration.Server.InvertedGridPositions != 0)
            {
                if (_configuration.Sessions.Count <= 1)
                {
                    MustInvertGrid = true;
                }
                else if (!IsLastRaceInverted)
                {
                    MustInvertGrid = true;
                    IsLastRaceInverted = true;
                    --CurrentSessionIndex;
                }
            }
        }

        if (++CurrentSessionIndex >= _configuration.Sessions.Count)
        {
            CurrentSessionIndex = 0;
        }

        var previousSession = CurrentSession;
        var previousSessionResults = CurrentSession?.Results;

        CurrentSession = _sessionStateFactory(_configuration.Sessions[CurrentSessionIndex]);
        CurrentSession.Results = new Dictionary<byte, EntryCarResult>();
        CurrentSession.StartTimeMilliseconds = ServerTimeMilliseconds;

        foreach (var entryCar in _entryCarManager.EntryCars)
        {
            CurrentSession.Results.Add(entryCar.SessionId, new EntryCarResult());
        }

        Log.Information("Next session: {SessionName}", CurrentSession.Configuration.Name);

        if (CurrentSession.Configuration.Type == SessionType.Race)
        {
            CurrentSession.StartTimeMilliseconds = ServerTimeMilliseconds + (CurrentSession.Configuration.WaitTime * 1000);
        }
        else
        {
            IsLastRaceInverted = false;
        }

        // TODO dynamic track
        // TODO weather
        // TODO reset mandatory pits and P2P count

        if (previousSessionResults == null)
        {
            CurrentSession.Grid = _entryCarManager.EntryCars;
        }
        else
        {
            CurrentSession.Grid = previousSessionResults
                .OrderBy(result => result.Value.BestLap)
                .Select(result => _entryCarManager.EntryCars[result.Key]);
        }

        SessionChanged?.Invoke(this, new SessionChangedEventArgs(previousSession, CurrentSession));
        SendCurrentSession();
    }
    
    internal void SendCurrentSession(ACTcpClient? target = null)
    {
        var packet = new CurrentSessionUpdate
        {
            CurrentSession = CurrentSession.Configuration,
            Grid = CurrentSession.Grid,
            TrackGrip = Math.Clamp(_configuration.Server.DynamicTrack != null ? _configuration.Server.DynamicTrack.BaseGrip + (_configuration.Server.DynamicTrack.GripPerLap * _configuration.Server.DynamicTrack.TotalLapCount) : 1, 0, 1),
        };

        if (target == null)
        {
            foreach (var car in _entryCarManager.EntryCars.Where(c => c.Client != null && c.Client.HasSentFirstUpdate))
            {
                packet.StartTime = CurrentSession.StartTimeMilliseconds - car.TimeOffset;
                car.Client?.SendPacket(packet);
            }
        }
        else
        {
            target.SendPacket(packet);
        }
    }
}
