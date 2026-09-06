using System.Windows;
using NPEduTools.PowerPoint.Diagnostics;

namespace NPEduTools.PowerPoint.Assist;

public partial class App : Application
{
    private Mutex? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.SequenceEqual(["--probe-worker"])) { PowerPointTouchAssist.RunProbeWorker(); Shutdown(); return; }
        if (e.Args.Length != 0) { Shutdown(2); return; }
        _instance = new Mutex(false, $"Local\\NPEduTools.PowerPoint.TouchAssist.{Environment.UserName}.{System.Diagnostics.Process.GetCurrentProcess().SessionId}", out bool created);
        if (!created) { MessageBox.Show("PowerPoint 触摸翻页已经在运行。", "NPEduTools"); Shutdown(); return; }
        new MainWindow().Show();
    }
    protected override void OnExit(ExitEventArgs e) { _instance?.Dispose(); base.OnExit(e); }
}
