using System.Diagnostics;
using System.Windows;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class MainWindow
{
    private bool _bridgeInspectionBusy;
    private async void RefreshClassIslandBridgeClicked(object sender, RoutedEventArgs e) => await RefreshClassIslandBridgeAsync();
    private async Task RefreshClassIslandBridgeAsync()
    {
        if (_bridgeInspectionBusy || _lifetime.IsCancellationRequested) return;
        _bridgeInspectionBusy = true;
        try
        {
            var config = await ManagementRequestAsync(new(1, Guid.NewGuid(), "classisland.config.get"));
            string? path = config.Outcome == "Succeeded" ? config.Launch?.Settings.ExecutablePath : null;
            var start = Stopwatch.GetTimestamp();
            var response = await ManagementRequestAsync(new(1, Guid.NewGuid(), "classisland.school-clock"));
            if (_lifetime.IsCancellationRequested) return;
            ClassIslandBridgeStatus.Text = ClassIslandBridgeInspection.Describe(path,
                response.Outcome == "Succeeded" ? response.SchoolClock : null, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        catch (Exception error) when (IsManagementError(error))
        { if (!_lifetime.IsCancellationRequested) ClassIslandBridgeStatus.Text = "时间桥接插件：后台状态读取失败，旧结果已撤下；请检查连接后刷新。"; }
        finally { _bridgeInspectionBusy = false; }
    }
}
