using System.IO;
using System.Windows;
using NPEduTools.ClassIsland.Admin;

namespace NPEduTools.App;

public partial class MainWindow
{
    private AdminStatus? _adminStatus;
    private string? _adminPath;
    private bool _adminBusy;

    private void AdminPanelClicked(object sender, RoutedEventArgs e)
    {
        ShowConnectionSettings();
        AdminSettingsExpander.IsExpanded = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => AdminSection.BringIntoView());
    }

    private void AdminRefreshClicked(object sender, RoutedEventArgs e) => _ = RunAdminAsync("status");
    private void AdminInspectClicked(object sender, RoutedEventArgs e) => _ = RunAdminAsync("inspect");
    private void AdminCreateClicked(object sender, RoutedEventArgs e) => _ = RunAdminAsync("create");
    private void AdminDeleteClicked(object sender, RoutedEventArgs e) => _ = RunAdminAsync("delete");
    private void AdminElevateClicked(object sender, RoutedEventArgs e) => _ = RunAdminAsync("elevate");

    private Task RefreshAdminAsync() => _adminBusy ? Task.CompletedTask : RunAdminAsync("status");

    private void RefreshAdminControls()
    {
        bool configured = !string.IsNullOrWhiteSpace(_savedPath) && string.Equals(_savedPath, ExecutablePathBox.Text.Trim(), StringComparison.OrdinalIgnoreCase);
        bool available = configured && !_adminBusy && !_actionInProgress && _pendingStartId is null && !_exitBusy;
        bool current = _adminStatus is not null && string.Equals(_adminPath, _savedPath, StringComparison.OrdinalIgnoreCase);
        AdminRefresh.IsEnabled = AdminInspect.IsEnabled = AdminElevate.IsEnabled = available;
        AdminCreate.IsEnabled = available && current && _adminStatus!.TaskState is "Missing" or "Enabled" or "Disabled";
        AdminDelete.IsEnabled = available && current && _adminStatus!.TaskState is "Enabled" or "Disabled";
        AdminCreate.Content = current && _adminStatus!.TaskState != "Missing" ? "更新并启用" : "启用管理员自启动";
        AdminElevate.Content = current && _adminStatus!.ProcessState == "Standard" ? "以管理员身份重启" : "管理员启动／重启";
        if (current && _adminStatus!.ProcessState == "Administrator") { AdminElevate.Content = "已以管理员运行"; AdminElevate.IsEnabled = false; }
        if (!configured && !_adminBusy)
        {
            AdminTaskStatus.Text = "请先保存 ClassIsland 程序路径";
            AdminProcessStatus.Text = "保存后可检查运行权限和自启动任务。";
        }
    }

    private async Task RunAdminAsync(string action)
    {
        if (_adminBusy || _actionInProgress || _pendingStartId is not null || _exitBusy) return;
        if (string.IsNullOrWhiteSpace(_savedPath) || !string.Equals(_savedPath, ExecutablePathBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        { RefreshAdminControls(); return; }
        string path = _savedPath;
        if (action is "create" or "delete" && (_adminStatus is null || _adminPath != path)) return;
        _adminBusy = true;
        AdminMessage.Text = action == "status" ? "正在检查…" : "等待 Windows 管理员授权并执行操作…";
        RefreshAdminControls();
        StartButton.IsEnabled = false;
        ExitButton.IsEnabled = false;
        try
        {
            var result = await AdminClient.RunAsync(Path.Combine(AppContext.BaseDirectory, "Admin", "NPEduTools.ClassIsland.Admin.exe"),
                action, path, _adminStatus?.Fingerprint);
            if (_savedPath != path) { _adminStatus = null; AdminMessage.Text = "程序路径已变更，请重新检查。"; return; }
            _adminPath = path;
            if (result.Status is { } status)
            {
                ApplyAdminStatus(path, status);
            }
            else if (result.Outcome != "Cancelled")
            {
                _adminStatus = null;
                AdminTaskStatus.Text = "状态待核实";
                AdminProcessStatus.Text = "权限不足时，可点击“管理员检查”。";
            }
            AdminMessage.Text = result.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _adminStatus = null;
            AdminTaskStatus.Text = "状态待核实";
            AdminMessage.Text = "未能完成检查或操作，请刷新状态后重试。";
        }
        finally
        {
            _adminBusy = false;
            ExitButton.IsEnabled = true;
            RefreshAdminControls();
            RefreshQuick();
        }
    }
}
