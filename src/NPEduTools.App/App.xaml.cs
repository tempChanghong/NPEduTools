using System.Windows;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.App;

public partial class App : Application
{
    private Mutex? _instance;
    private EventWaitHandle? _activation;
    private RegisteredWaitHandle? _activationWait;
    private bool _sessionEndRequested;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLaunchOptions options;
        try { options = AppLaunchOptions.Parse(e.Args, PipeEndpoint.DefaultName); }
        catch (ArgumentException error)
        {
            MessageBox.Show(error.Message, "NPEduTools");
            Shutdown(2);
            return;
        }
        string pipe = options.Pipe;
        try
        {
            if (!StartupElevation.Ensure(e.Args)) { Shutdown(); return; }
            await StartupElevation.CheckExistingHostAsync(pipe);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.IO.IOException or
            UnauthorizedAccessException or InvalidOperationException or OperationCanceledException or ArgumentException)
        {
            MessageBox.Show(error is OperationCanceledException ? "已有后台尚未就绪，请稍后重新启动。" : error.Message,
                "NPEduTools · 启动权限", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(3);
            return;
        }
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{pipe}.App.Activate");
        _instance = new Mutex(false, $@"Local\{pipe}.App", out bool created);
        if (!created) { if (!options.AtLogin) _activation.Set(); Shutdown(); return; }
        // Consent precedes MainWindow construction, Host startup, school polling and device features.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var agreements = AgreementCatalog.Load();
            AgreementAcceptanceRecord? accepted = null;
            try { accepted = new AgreementAcceptanceStore(AgreementAcceptanceStore.PathFor(pipe)).Read(); }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.IO.InvalidDataException) { }
            if (accepted?.IsCurrent(agreements) != true && new AgreementsWindow(pipe).ShowDialog() != true)
            { Shutdown(); return; }
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            MessageBox.Show("协议无法读取或确认，请使用完整程序包并检查配置目录权限。", "NPEduTools · 服务与隐私", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(4); return;
        }
        MainWindow = new MainWindow(pipe, options.Upstream);
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        _activationWait = ThreadPool.RegisterWaitForSingleObject(_activation, (_, _) =>
            Dispatcher.BeginInvoke(() => ((MainWindow)MainWindow).RestoreWindow()), null, Timeout.Infinite, false);
        ((MainWindow)MainWindow).Start(options.AtLogin, options.GuardRecovery);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // WPF can exit during QUERYENDSESSION, before Windows has made its final decision.
        if (!_sessionEndRequested && _instance is not null && MainWindow is MainWindow window) MarkGuardExit(window.PipeName, "AppNormalExit");
        _activationWait?.Unregister(null); _activation?.Dispose(); _instance?.Dispose();
        base.OnExit(e);
    }

    private static void MarkGuardExit(string pipe, string reason)
    {
        var files = GuardFiles.ForPipe(pipe);
        try
        {
            if (files.Read<GuardRegistration>("registration.json") is { } r && r.App == GuardProcess.Current()) files.Stop(r.Generation, reason);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _sessionEndRequested = true;
        if (MainWindow is MainWindow window)
        {
            var files = GuardFiles.ForPipe(window.PipeName);
            try
            {
                if (files.Read<GuardRegistration>("registration.json") is { } r && r.App == GuardProcess.Current()) files.SessionQuery(r.Generation, true);
            }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { }
        }
        base.OnSessionEnding(e);
        if (e.Cancel) _sessionEndRequested = false;
    }
}
