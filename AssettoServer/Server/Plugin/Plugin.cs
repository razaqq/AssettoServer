﻿using System;
using System.Reflection;

namespace AssettoServer.Server.Plugin;

public class Plugin
{
    public string Name { get; }
    public Assembly Assembly { get; }
    public AssettoServerModule Instance { get; }
    public Type? ConfigurationType { get; }

    public Plugin(string name, Assembly assembly, AssettoServerModule instance, Type? configurationType)
    {
        Name = name;
        Assembly = assembly;
        Instance = instance;
        ConfigurationType = configurationType;
    }
}
