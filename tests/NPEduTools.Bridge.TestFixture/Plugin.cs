using System.Text.Json;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using ClassIsland.Services;
using ClassIsland.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NPEduTools.Bridge.TestFixture;

// Deliberately a different plugin/assembly. No test controls enter the production bridge.
[PluginEntrance]
public sealed class Plugin : PluginBase
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private string? _root, _lastId;
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        _root = Environment.GetEnvironmentVariable("NPEEDUTOOLS_BRIDGE_FIXTURE");
        if (_root is null || !Path.IsPathFullyQualified(_root) || !File.Exists(Path.Combine(_root, "fixture-marker")))
            throw new InvalidOperationException("Fixture requires an explicit isolated test directory.");
        AppBase.Current.AppStarted += Started;
        AppBase.Current.AppStopping += Stopping;
    }
    private void Started(object? sender, EventArgs args) { _timer.Tick += Tick; _timer.Start(); }
    private void Stopping(object? sender, EventArgs args)
    {
        _timer.Stop(); _timer.Tick -= Tick;
        AppBase.Current.AppStarted -= Started; AppBase.Current.AppStopping -= Stopping;
    }
    private void Tick(object? sender, EventArgs args)
    {
        string path = Path.Combine(_root!, "command.json");
        if (!File.Exists(path)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var request = document.RootElement;
            string id = request.GetProperty("id").GetString()!;
            if (id == _lastId) return;
            _lastId = id;
            string action = request.GetProperty("action").GetString()!;
            var lessons = IAppHost.GetService<ILessonsService>();
            switch (action)
            {
                case "offset":
                    IAppHost.GetService<SettingsService>().Settings.TimeOffsetSeconds = request.GetProperty("seconds").GetDouble(); break;
                case "enabled": lessons.IsClassPlanEnabled = request.GetProperty("value").GetBoolean(); break;
                case "timer":
                    if (request.GetProperty("value").GetBoolean()) lessons.StartMainTimer(); else lessons.StopMainTimer(); break;
                case "no-plan": IAppHost.GetService<IProfileService>().Profile.ClassPlans.Clear(); break;
                case "future-order":
                    var orderedProfile = IAppHost.GetService<IProfileService>().Profile;
                    var today = IAppHost.GetService<IExactTimeService>().GetCurrentLocalDateTime().Date;
                    var target = orderedProfile.ClassPlans.First(p => p.Value.Name == "Bridge day " + (int)today.DayOfWeek);
                    orderedProfile.OrderedSchedules[today.AddDays(1)] = new() { ClassPlanId = target.Key };
                    break;
                case "clear-orders": IAppHost.GetService<IProfileService>().Profile.OrderedSchedules.Clear(); break;
                case "block": case "stop": case "inspect": break;
                default: throw new InvalidDataException("Unknown fixture command");
            }
            var now = IAppHost.GetService<IExactTimeService>().GetCurrentLocalDateTime();
            var profile = IAppHost.GetService<IProfileService>().Profile;
            var settings = IAppHost.GetService<SettingsService>().Settings;
            var state = new { profile.Id, profile.SelectedClassPlanGroupId, profile.TempClassPlanId, profile.TempClassPlanSetupTime,
                profile.TempClassPlanGroupId, profile.TempClassPlanGroupExpireTime, profile.IsTempClassPlanGroupEnabled,
                ordered = JsonSerializer.Serialize(profile.OrderedSchedules), current = lessons.CurrentClassPlan?.Name,
                currentIdentity = lessons.CurrentClassPlan is { } plan ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(plan) : 0,
                lessons.IsClassPlanEnabled, lessons.IsTimerRunning, settings.TimeOffsetSeconds, settings.DebugTimeOffsetSeconds };
            File.WriteAllText(Path.Combine(_root!, "ack.json"), JsonSerializer.Serialize(new { id, action, effectiveTime = now.ToString("O"), state }));
            if (action == "block") Thread.Sleep(6500);
            if (action == "stop") AppBase.Current.Stop();
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(_root!, "fixture-error.txt"), error.ToString()); }
    }
}
