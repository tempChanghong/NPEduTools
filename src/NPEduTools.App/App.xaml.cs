using System.Windows;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class App : Application
{
    private Mutex? _instance;
    private EventWaitHandle? _activation;
    private RegisteredWaitHandle? _activationWait;
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
        MainWindow = new MainWindow(pipe, options.Upstream);
        _activationWait = ThreadPool.RegisterWaitForSingleObject(_activation, (_, _) =>
            Dispatcher.BeginInvoke(() => ((MainWindow)MainWindow).RestoreWindow()), null, Timeout.Infinite, false);
        ((MainWindow)MainWindow).Start(options.AtLogin);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationWait?.Unregister(null); _activation?.Dispose(); _instance?.Dispose();
        base.OnExit(e);
    }
}
