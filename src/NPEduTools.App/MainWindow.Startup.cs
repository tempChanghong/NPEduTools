using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class MainWindow
{
    private StartupPreferencesStore _startupStore = null!;
    private StartupPreferences _startupPreferences = new();
    private bool _updatingStartup, _startupPreferencesReadable = true, _startupTouchPending;
    private readonly Stopwatch _startupClock = Stopwatch.StartNew();

    private void InitializeStartupPreferences()
    {
        _startupStore = new(StartupPreferencesStore.PathFor(_pipe));
        try { _startupPreferences = _startupStore.Read(); }
        catch (Exception error) when (IsStartupError(error))
        {
            _startupPreferencesReadable = false;
            StartupPreferencesMessage.Text = "启动偏好未能读取，本次使用默认设置且不自动开启辅助。原文件已保留。";
            HomeMessage.Text = StartupPreferencesMessage.Text;
        }
        _startupTouchPending = _startupPreferences.EnableTouchOnLaunch;
        ShowStartupPreferences();
        RefreshLoginStartup();
    }

    public void Start(bool atLogin)
    {
        // Initialize the edge and services without ever showing the main window on a quiet login.
        _quick = new QuickAccessWindow(_pipe, () => TouchPowerClicked(this, new RoutedEventArgs()),
            () => TouchPauseClicked(this, new RoutedEventArgs()), () => StartClicked(this, new RoutedEventArgs()), ShowSettings,
            entry => _ = OpenShortcutAsync(entry), ShowShortcutManager, RepairShortcut);
        _quick.SetShortcuts(_shortcuts);
        RefreshShortcutControls();
        _quick.Show();
        DockLeft.IsChecked = _quick.LeftSide;
        DockRight.IsChecked = !_quick.LeftSide;
        RefreshQuick();
        if (!atLogin || !_startupPreferences.EdgeOnlyAtLogin) Show();
        _watch = WatchAsync(_lifetime.Token);
        _management = ManagementLoopAsync(_lifetime.Token);
        _touchPoll = TouchPollAsync(_lifetime.Token);
    }

    private void ShowStartupPreferences()
    {
        _updatingStartup = true;
        EdgeOnlyAtLogin.IsChecked = _startupPreferences.EdgeOnlyAtLogin;
        AutoTouchAtLaunch.IsChecked = _startupPreferences.EnableTouchOnLaunch;
        EdgeOnlyAtLogin.IsEnabled = AutoTouchAtLaunch.IsEnabled = _startupPreferencesReadable;
        _updatingStartup = false;
    }

    private void StartupPreferenceChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingStartup || _startupStore is null || !_startupPreferencesReadable) return;
        var next = _startupPreferences with
        {
            EdgeOnlyAtLogin = EdgeOnlyAtLogin.IsChecked == true,
            EnableTouchOnLaunch = AutoTouchAtLaunch.IsChecked == true
        };
        try
        {
            _startupStore.Save(next);
            _startupPreferences = next;
            if (!next.EnableTouchOnLaunch) _startupTouchPending = false;
            StartupPreferencesMessage.Text = "已保存，下次启动时生效。";
        }
        catch (Exception error) when (IsStartupError(error))
        { StartupPreferencesMessage.Text = "未能保存启动偏好，请检查配置目录的访问权限。"; }
        ShowStartupPreferences();
    }

    private bool CanRegisterLogin => _pipe == PipeEndpoint.DefaultName && _upstream is null;

    private void RefreshLoginStartup()
    {
        _updatingStartup = true;
        try
        {
            LoginStartupToggle.IsEnabled = CanRegisterLogin;
            if (!CanRegisterLogin)
            {
                LoginStartupToggle.IsChecked = false;
                LoginStartupMessage.Text = "独立测试实例不登记 Windows 登录启动项。";
                return;
            }
            using var key = Registry.CurrentUser.OpenSubKey(LoginStartup.RunKey);
            string state = key is null ? "Missing" : new LoginStartup(key, Environment.ProcessPath!).ReadState();
            LoginStartupToggle.IsChecked = state == "Registered";
            LoginStartupToggle.IsEnabled = state != "Conflict";
            LoginStartupMessage.Text = state switch
            {
                "Registered" => "已登记当前用户的登录启动项。若在 Windows 中禁用，请到“启动应用”重新启用。",
                "Conflict" => "同名启动项指向其他位置。请先在原位置关闭自启动，再使用此处设置。",
                _ => "尚未设置登录启动。开启后，从当前程序位置启动。"
            };
        }
        catch (Exception error) when (IsStartupError(error))
        { LoginStartupToggle.IsEnabled = false; LoginStartupToggle.IsChecked = false; LoginStartupMessage.Text = "无法读取登录启动项，请检查当前用户权限。"; }
        finally { _updatingStartup = false; }
    }

    private void LoginStartupChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingStartup || !CanRegisterLogin) return;
        string? failure = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(LoginStartup.RunKey, true);
            new LoginStartup(key, Environment.ProcessPath!).SetEnabled(LoginStartupToggle.IsChecked == true);
        }
        catch (Exception error) when (IsStartupError(error))
        { failure = error is InvalidDataException or InvalidOperationException ? error.Message : "未能更新登录启动项，请检查当前用户权限。"; }
        RefreshLoginStartup();
        if (failure is not null) LoginStartupMessage.Text = failure;
    }

    private void OpenWindowsStartupClicked(object sender, RoutedEventArgs e)
    {
        try { using var process = Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        { LoginStartupMessage.Text = "无法打开 Windows 设置，可在任务管理器的“启动应用”中查看。"; }
    }

    private async Task TryStartupTouchAsync()
    {
        if (!_startupTouchPending || _touchBusy || _exitBusy || _lifetime.IsCancellationRequested) return;
        if (_startupClock.Elapsed > TimeSpan.FromSeconds(30))
        {
            _startupTouchPending = false;
            HomeMessage.Text = "启动时未能连接触摸辅助，请在快捷面板中手动开启。";
            return;
        }
        if (_touchState is null) return;
        _startupTouchPending = false; // Consume before awaiting: never replay after failure or Host restart.
        if (_touchState.Running) return; // Preserve an existing pause as well.
        if (_touchState.Error is null) await ChangeTouchAsync("enable");
        if (_touchState?.Running != true || _touchState.Error is not null)
            HomeMessage.Text = "触摸辅助未能自动开启，请查看辅助状态并手动重试。";
    }

    private static bool IsStartupError(Exception error) => error is IOException or UnauthorizedAccessException or
        JsonException or SecurityException or InvalidOperationException or ArgumentException;
}
