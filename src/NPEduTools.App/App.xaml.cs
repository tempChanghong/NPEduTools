using System.Windows;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class App : Application
{
    private Mutex? _instance;
    private EventWaitHandle? _activation;
    private RegisteredWaitHandle? _activationWait;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string pipe = PipeEndpoint.DefaultName;
        string? upstream = null;
        for (int i = 0; i < e.Args.Length; i += 2)
        {
            if (i + 1 >= e.Args.Length || e.Args[i] is not ("--pipe" or "--classisland-pipe") ||
                e.Args[i + 1].Length is 0 or > 200 || e.Args[i + 1].IndexOfAny(['/', '\\', ':']) >= 0)
            {
                MessageBox.Show("启动参数无效。支持 --pipe NAME 和 --classisland-pipe NAME。", "NPEduTools");
                Shutdown(2);
                return;
            }
            if (e.Args[i] == "--pipe") pipe = e.Args[i + 1];
            else upstream = e.Args[i + 1];
        }
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{pipe}.App.Activate");
        _instance = new Mutex(false, $@"Local\{pipe}.App", out bool created);
        if (!created) { _activation.Set(); Shutdown(); return; }
        MainWindow = new MainWindow(pipe, upstream);
        _activationWait = ThreadPool.RegisterWaitForSingleObject(_activation, (_, _) =>
            Dispatcher.BeginInvoke(() => ((MainWindow)MainWindow).RestoreWindow()), null, Timeout.Infinite, false);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationWait?.Unregister(null); _activation?.Dispose(); _instance?.Dispose();
        base.OnExit(e);
    }
}
