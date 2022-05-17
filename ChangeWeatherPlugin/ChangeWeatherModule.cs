using AssettoServer.Server;
using AssettoServer.Server.Plugin;
using Autofac;

namespace ChangeWeatherPlugin;

public class ChangeWeatherModule : AssettoServerModule
{ 
    protected override void Load(ContainerBuilder builder)
    { 
        builder.RegisterType<ChangeWeather>().AsSelf().AutoActivate().SingleInstance();
    }
}
