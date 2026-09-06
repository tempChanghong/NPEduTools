using System.IO;
using System.Windows;
using NPEduTools.ClassIsland.Admin;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class MainWindow
{
    private string? _restartOfferedFor;
    private string? _launchMessage;
    private Guid? _unifiedVerification;
    private bool _launchSucceeded;

    private async void StartClicked(object sender, RoutedEventArgs e)
    {
        if (_actionInProgress || _adminBusy || _pendingStartId is not null || _exitBusy) return;
        if (string.IsNullOrWhiteSpace(_savedPath) || !string.Equals(ExecutablePathBox.Text.Trim(), _savedPath, StringComparison.OrdinalIgnoreCase))
        {
            ConfigurationMessage.Text = "请先保存当前程序路径，再启动 ClassIsland。";
            SelectPage(true); RestoreWindow(); return;
        }
        string path = _savedPath;
        bool restartRequested = _restartOfferedFor == path;
        _actionInProgress = true;
        StartButton.IsEnabled = ExitButton.IsEnabled = false;
        RefreshAdminControls();
        SetLaunchMessage(restartRequested ? "等待 Windows 授权，以管理员身份重启…" : "正在检查运行实例和启动方式…");
        string helper = Path.Combine(AppContext.BaseDirectory, "Admin", "NPEduTools.ClassIsland.Admin.exe");
        try
        {
            var result = await AdminClient.RunAsync(helper, restartRequested ? "elevate" : "launch", path);
            if (result.Outcome == "NeedsElevation")
            {
                SetLaunchMessage(result.Message);
                result = await AdminClient.RunAsync(helper, result.ErrorCode == "TaskAccessDenied" ? "launch-elevated" : "elevate", path, result.Status?.Fingerprint);
            }
            if (_savedPath != path) { SetLaunchMessage("程序路径已变更，请重新检查运行实例。"); return; }
            if (result.Status is not null) ApplyAdminStatus(path, result.Status);
            _restartOfferedFor = result.Outcome == "NeedsRestart" || (restartRequested && result.Outcome == "Cancelled") ? path : null;
            StartButton.Content = _restartOfferedFor is null ? "启动" : "管理员重启";
            SetLaunchMessage(result.Message);
            if (result.Outcome != "Succeeded") return;

            // Verification has no start fallback. Even if the process exits, it cannot launch a duplicate.
            _pendingStartId = _unifiedVerification = Guid.NewGuid();
            var response = await ManagementRequestAsync(new(Protocol.Version, _pendingStartId.Value, "classisland.verify",
                ExecutablePath: path, ExpectedRevision: _configurationRevision));
            ShowLaunchData(response);
            if (response.Outcome is not ("Running" or "Succeeded"))
            {
                _pendingStartId = _unifiedVerification = null;
                SetLaunchMessage("进程状态已核实，但课程连接验证未受理：" + response.Message);
            }
        }
        catch (Exception ex) when (IsManagementError(ex) || ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { SetLaunchMessage("尚未确认启动或连接结果，请核实当前状态；不会自动重复启动。"); }
        finally
        {
            _actionInProgress = false; ExitButton.IsEnabled = true;
            RefreshAdminControls(); RefreshQuick();
        }
    }

    private void ApplyAdminStatus(string path, AdminStatus status)
    {
        _adminPath = path; _adminStatus = status;
        if (status.ProcessState != "Standard" || status.TaskState != "Enabled")
        { _restartOfferedFor = null; StartButton.Content = "启动"; }
        AdminTaskStatus.Text = status.TaskMessage;
        AdminProcessStatus.Text = status.ProcessMessage;
        AdminPluginStatus.Text = status.PluginInstalled ? "兼容已安装的 StartUpAsAdmin，共用同一计划任务。" : "兼容 StartUpAsAdmin；未安装插件也可管理此任务。";
    }

    private void SetLaunchMessage(string message)
    {
        _launchSucceeded = false;
        _launchMessage = message;
        HomeMessage.Text = message;
        RefreshQuick();
    }
}
