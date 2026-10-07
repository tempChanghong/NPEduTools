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

    private async void StartClicked(object sender, RoutedEventArgs e) => await StartClassIslandAsync((action, path, fingerprint) =>
        AdminClient.RunAsync(Path.Combine(AppContext.BaseDirectory, "Admin", "NPEduTools.ClassIsland.Admin.exe"), action, path, fingerprint));

    // Isolated checks exercise the full launch/verification flow without starting a native helper.
    internal async Task StartClassIslandAsync(Func<string, string, string?, Task<AdminResult>> run)
    {
        if (_actionInProgress || _adminBusy || _pendingStartId is not null || _exitBusy || _lifetime.IsCancellationRequested) return;
        if (string.IsNullOrWhiteSpace(_savedPath) || !string.Equals(ExecutablePathBox.Text.Trim(), _savedPath, StringComparison.OrdinalIgnoreCase))
        {
            ConfigurationMessage.Text = "请先保存当前程序路径，再启动 ClassIsland。";
            ShowClassIslandPathSettings(); return;
        }
        string path = _savedPath;
        bool restartRequested = _restartOfferedFor == path;
        _launchUiGeneration++;
        _actionInProgress = true;
        StartButton.IsEnabled = ExitButton.IsEnabled = false;
        RefreshAdminControls();
        SetLaunchMessage(restartRequested ? "等待 Windows 授权，以管理员身份重启…" : "正在检查运行实例和启动方式…");
        try
        {
            var result = await run(restartRequested ? "elevate" : "launch", path, null);
            if (_lifetime.IsCancellationRequested) return;
            if (result.Outcome == "NeedsElevation")
            {
                SetLaunchMessage(result.Message);
                result = await run(result.ErrorCode == "TaskAccessDenied" ? "launch-elevated" : "elevate", path, result.Status?.Fingerprint);
                if (_lifetime.IsCancellationRequested) return;
            }
            if (_savedPath != path) { SetLaunchMessage("程序路径已变更，请重新检查运行实例。"); return; }
            if (result.Status is not null) ApplyAdminStatus(path, result.Status);
            else if (result.Outcome != "Cancelled") ClearAdminStatus();
            _restartOfferedFor = result.Outcome == "NeedsRestart" || (restartRequested && result.Outcome == "Cancelled") ? path : null;
            StartButton.Content = _restartOfferedFor is null ? "启动" : "管理员重启";
            SetLaunchMessage(result.Message);
            if (result.Outcome != "Succeeded") return;

            // Verification has no start fallback. Even if the process exits, it cannot launch a duplicate.
            _pendingStartId = _unifiedVerification = Guid.NewGuid();
            var response = await ManagementRequestAsync(new(Protocol.Version, _pendingStartId.Value, "classisland.verify",
                ExecutablePath: path, ExpectedRevision: _configurationRevision));
            if (_lifetime.IsCancellationRequested) return;
            ShowLaunchData(response);
            if (response.Outcome is not ("Running" or "Succeeded"))
            {
                _pendingStartId = _unifiedVerification = null;
                SetLaunchMessage("进程状态已核实，但课程连接验证未受理：" + response.Message);
            }
        }
        catch (Exception ex) when (IsManagementError(ex) || ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            if (!_lifetime.IsCancellationRequested)
            {
                // A lost verification receipt does not invalidate the helper's confirmed process snapshot.
                if (_unifiedVerification is null) ClearAdminStatus();
                SetLaunchMessage("尚未确认启动或连接结果，请核实当前状态；不会自动重复启动。");
            }
        }
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
        DisplayAdminStatus(status);
    }

    private void SetLaunchMessage(string message)
    {
        _launchSucceeded = false;
        _launchMessage = message;
        HomeMessage.Text = message;
        RefreshQuick();
    }
}
