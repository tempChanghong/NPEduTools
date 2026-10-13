using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.App;

public partial class MainWindow
{
    private bool _bridgeInspectionBusy;
    private bool _classIslandConfigurationPresented;
    private readonly SchoolClockTracker _bridgeClock = new();
    private readonly Stopwatch _bridgeElapsed = Stopwatch.StartNew();
    private readonly DispatcherTimer _bridgeClockTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private void InitializeBridgeClock()
    {
        _bridgeClockTimer.Tick += (_, _) => RenderBridgeClock();
        Loaded += (_, _) => _bridgeClockTimer.Start();
        Closed += (_, _) => _bridgeClockTimer.Stop();
    }
    private void RenderBridgeClock()
    {
        var view = ClassIslandClockPresentation.From(_bridgeClock.Read(_bridgeElapsed.Elapsed));
        ClassIslandClockText.Text = view.Clock; ClassIslandClockDetail.Text = view.Detail;
    }
    private async void RefreshClassIslandBridgeClicked(object sender, RoutedEventArgs e) => await RefreshClassIslandBridgeAsync();
    private async Task RefreshClassIslandBridgeAsync()
    {
        if (_bridgeInspectionBusy || _lifetime.IsCancellationRequested) return;
        _bridgeInspectionBusy = true;
        long generation = _launchUiGeneration;
        try
        {
            var config = await ManagementRequestAsync(new(1, Guid.NewGuid(), "classisland.config.get"));
            string? path = config.Outcome == "Succeeded" ? config.Launch?.Settings.ExecutablePath : null;
            var start = Stopwatch.GetTimestamp();
            var response = await ManagementRequestAsync(new(1, Guid.NewGuid(), "classisland.school-clock"));
            if (_lifetime.IsCancellationRequested || generation != _launchUiGeneration) return;
            // Measure transport before file/UI work, and timestamp the clock at receipt.
            double roundTripMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var frame = response.Outcome == "Succeeded" ? response.SchoolClock : null;
            _bridgeClock.Accept(frame ?? SchoolClockFrame.Unavailable(response.Message), _bridgeElapsed.Elapsed, roundTripMs);
            RenderBridgeClock();
            bool pathAccessible = !string.IsNullOrWhiteSpace(path) &&
                await Task.Run(() => System.IO.File.Exists(path), _lifetime.Token);
            if (_lifetime.IsCancellationRequested || generation != _launchUiGeneration) return;
            ClassIslandProgramStatus.Text = config.Outcome != "Succeeded" || config.Launch is not { StorageWarning: null }
                ? "程序位置：本次检查未能读取保存的配置。" : string.IsNullOrWhiteSpace(path)
                ? "程序位置：尚未保存，请展开程序位置与保存。" : pathAccessible
                ? "程序位置：本次检查文件可访问；路径与插件连接分别核实。" : "程序位置：已保存的文件不可访问，请重新选择。";
            if (config.Outcome == "Succeeded" && config.Launch is not null && !_classIslandConfigurationPresented)
            {
                _classIslandConfigurationPresented = true;
                if (!ClassIslandConfiguration.IsKeyboardFocusWithin)
                    ClassIslandConfiguration.IsExpanded = config.Launch.StorageWarning is not null || !pathAccessible;
            }
            ClassIslandBridgeStatus.Text = ClassIslandBridgeInspection.Describe(path,
                frame, roundTripMs);
        }
        catch (Exception error) when (IsManagementError(error))
        {
            if (!_lifetime.IsCancellationRequested && generation == _launchUiGeneration)
            {
                _bridgeClock.Unavailable("后台状态读取失败，请检查时间桥接插件后刷新。", _bridgeElapsed.Elapsed);
                RenderBridgeClock();
                ClassIslandBridgeStatus.Text = "时间桥接插件：后台状态读取失败，旧结果已撤下；请检查连接后刷新。";
                ClassIslandProgramStatus.Text = "程序位置：后台读取失败，保存状态待重新核实。";
            }
        }
        finally { _bridgeInspectionBusy = false; }
    }
}
