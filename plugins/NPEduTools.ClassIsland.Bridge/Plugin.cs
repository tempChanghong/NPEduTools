using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NPEduTools.ClassIsland.Bridge;

[PluginEntrance]
public sealed class Plugin : PluginBase
{
    private BridgeService? _bridge;
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<BridgeService>();
        AppBase.Current.AppStarted += Started;
        AppBase.Current.AppStopping += Stopping;
    }
    private void Started(object? sender, EventArgs args)
    {
        try { _bridge = IAppHost.GetService<BridgeService>(); _bridge.Start(); }
        catch (Exception error) { Console.Error.WriteLine("NPEduTools bridge startup failed: " + error.GetType().Name); }
    }
    private void Stopping(object? sender, EventArgs args)
    {
        AppBase.Current.AppStarted -= Started;
        AppBase.Current.AppStopping -= Stopping;
        _bridge?.Dispose();
    }
}
