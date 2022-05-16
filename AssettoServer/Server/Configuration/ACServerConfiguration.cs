﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using AssettoServer.Server.Plugin;
using AssettoServer.Utils;
using Autofac;
using Newtonsoft.Json;
using Serilog;

namespace AssettoServer.Server.Configuration;

public class ACServerConfiguration
{
    public ServerConfiguration Server { get; }
    public EntryList EntryList { get; }
    public IReadOnlyList<SessionConfiguration> Sessions { get; }
    public string FullTrackName { get; }
    public string WelcomeMessage { get; } = "";
    public ACExtraConfiguration Extra { get; private set; } = new();
    public CMContentConfiguration? ContentConfiguration { get; private set; }
    public string ServerVersion { get; }
    public string? CSPExtraOptions { get; }

    public event EventHandler<ACServerConfiguration, EventArgs>? Reload;

    private readonly string _configBaseFolder;

    /*
     * Search paths are like this:
     *
     * When no options are set, all config files must be located in "cfg/".
     * WELCOME_MESSAGE path is relative to the working directory of the server.
     *
     * When "preset" is set, all configs must be located in "presets/<preset>/".
     * WELCOME_MESSAGE path must be relative to the preset folder.
     *
     * When "serverCfgPath" is set, server_cfg.ini will be loaded from the specified path.
     * All other configs must be located in the same folder.
     *
     * When "entryListPath" is set, it takes precedence and entry_list.ini will be loaded from the specified path.
     */
    public ACServerConfiguration(string preset, string serverCfgPath, string entryListPath)
    {
        _configBaseFolder = string.IsNullOrEmpty(preset) ? "cfg" : Path.Join("presets", preset);

        if (string.IsNullOrEmpty(entryListPath))
        {
            entryListPath = Path.Join(_configBaseFolder, "entry_list.ini");
        }

        if (string.IsNullOrEmpty(serverCfgPath))
        {
            serverCfgPath = Path.Join(_configBaseFolder, "server_cfg.ini");
        }
        else
        {
            _configBaseFolder = Path.GetDirectoryName(serverCfgPath)!;
        }

        Server = ServerConfiguration.FromFile(serverCfgPath);
        EntryList = EntryList.FromFile(entryListPath);
        ServerVersion = ThisAssembly.AssemblyInformationalVersion;

        FullTrackName = string.IsNullOrEmpty(Server.TrackConfig) ? Server.Track : Server.Track + "-" + Server.TrackConfig;

        string welcomeMessagePath = string.IsNullOrEmpty(preset) ? Server.WelcomeMessagePath : Path.Join(_configBaseFolder, Server.WelcomeMessagePath);
        if (File.Exists(welcomeMessagePath))
        {
            WelcomeMessage = File.ReadAllText(welcomeMessagePath);
        }
        else if(!string.IsNullOrEmpty(welcomeMessagePath))
        {
            Log.Warning("Welcome message not found at {Path}", Path.GetFullPath(welcomeMessagePath));
        }

        string cspExtraOptionsPath = Path.Join(_configBaseFolder, "csp_extra_options.ini"); 
        if (File.Exists(cspExtraOptionsPath))
        {
            CSPExtraOptions = File.ReadAllText(cspExtraOptionsPath);
        }

        var sessions = new List<SessionConfiguration>();

        if (Server.Practice != null)
        {
            Server.Practice.Id = 0;
            sessions.Add(Server.Practice);
        }
            
        if (Server.Qualify != null)
        {
            Server.Qualify.Id = 1;
            sessions.Add(Server.Qualify);
        }
            
        if (Server.Race != null)
        {
            Server.Race.Id = 2;
            sessions.Add(Server.Race);
        }
            
        Sessions = sessions;

        LoadExtraConfig();
    }

    public void LoadExtraConfig() {

        string extraCfgPath = Path.Join(_configBaseFolder, "extra_cfg.yml");
        if (File.Exists(extraCfgPath))
        {
            Extra = ACExtraConfiguration.FromFile(extraCfgPath);
        }
        else
        {
            Extra = new ACExtraConfiguration();
            Extra.ToFile(extraCfgPath);
        }

        if (Regex.IsMatch(Server.Name, @"x:\w+$"))
        {
            const string errorMsg =
                "Server details are configured via ID in server name. This interferes with native AssettoServer server details. More info: https://github.com/compujuckel/AssettoServer/wiki/Common-configuration-errors#wrong-server-details";
            if (Extra.IgnoreConfigurationErrors.WrongServerDetails)
            {
                Log.Warning(errorMsg);
            }
            else
            {
                throw new ConfigurationException(errorMsg);
            }
        }

        if (Extra.RainTrackGripReductionPercent is < 0 or > 0.5)
        {
            throw new ConfigurationException("RainTrackGripReductionPercent must be in the range 0..0.5");
        }
        
        if (Extra.AiParams.MaxSpeedVariationPercent is < 0 or > 1)
        {
            throw new ConfigurationException("MaxSpeedVariationPercent must be in the range 0..1");
        }
        
        if (Extra.AiParams.HourlyTrafficDensity != null && Extra.AiParams.HourlyTrafficDensity.Count != 24)
        {
            throw new ConfigurationException("HourlyTrafficDensity must have exactly 24 entries");
        }
        
        if (Extra.BatchedPositionUpdateBehavior != null)
        {
            Log.Warning("The option BatchedPositionUpdateBehavior is deprecated and will be removed in a future release");
        }

        if (Extra.EnableServerDetails)
        {
            string cmContentPath = Path.Join(_configBaseFolder, "cm_content/content.json");
            if (File.Exists(cmContentPath))
            {
                ContentConfiguration = JsonConvert.DeserializeObject<CMContentConfiguration>(File.ReadAllText(cmContentPath));
            }
        }
    }

    private (PropertyInfo? Property, object Parent) GetNestedProperty(string key)
    {
        string[] path = key.Split('.');
            
        object parent = this;
        PropertyInfo? propertyInfo = null;

        foreach (string property in path)
        {
            propertyInfo = parent.GetType().GetProperty(property);
            if (propertyInfo == null) continue;
                
            var propertyType = propertyInfo.PropertyType;
            if (!propertyType.IsPrimitive && !propertyType.IsEnum && propertyType != typeof(string))
            {
                parent = propertyInfo.GetValue(parent)!;
            }
        }

        return (propertyInfo, parent);
    }

    public bool SetProperty(string key, string value)
    {
        (var propertyInfo, object? parent) = GetNestedProperty(key);

        if (propertyInfo == null)
            throw new ConfigurationException($"Could not find property {key}");

        bool ret = false;
        try
        {
            ret = propertyInfo.SetValueFromString(parent, value);
        }
        catch (TargetInvocationException)
        {
            // ignored
        }
        
        if (ret) TriggerReload();

        return ret;
    }

    public void TriggerReload()
    {
        Reload?.Invoke(this, EventArgs.Empty);
    }
}
