using AssettoServer.Commands;
using AssettoServer.Commands.Attributes;
using Qmmands;

namespace ChangeWeatherPlugin;

[RequireAdmin]
public class ChangeWeatherCommandModule : ACModuleBase
{
    private readonly ChangeWeather _changeWeather;

    public ChangeWeatherCommandModule(ChangeWeather changeWeather)
    {
        _changeWeather = changeWeather;
    }

    [Command("cw")]
    public void ChangeWeather(int choice)
    {
        _changeWeather.ProcessChoice(Context.Client, choice);
    }

    [Command("cwl")]
    public void ChangeWeatherList()
    {
        _changeWeather.GetWeathers(Context.Client);
    }
}
