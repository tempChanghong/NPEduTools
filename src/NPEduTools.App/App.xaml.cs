using System.Windows;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class App : Application
{
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
        MainWindow = new MainWindow(pipe, upstream);
        MainWindow.Show();
    }
}
