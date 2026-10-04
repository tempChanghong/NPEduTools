using System.Diagnostics;
using System.IO;
using NPEduTools.Core;

namespace NPEduTools.App;

public partial class MainWindow
{
    private void StartGuard()
    {
        var files = GuardFiles.ForPipe(_pipe);
        try
        {
            bool existing = Mutex.TryOpenExisting($@"Local\{_pipe}.Guard", out var mutex);
            mutex?.Dispose();
            var registration = existing ? files.Read<GuardRegistration>("registration.json") : null;
            if (existing && (registration is null || files.Stopped(registration.Generation)))
                throw new IOException("旧守护正在停止，请稍后重新打开。");
            if (existing && registration!.Upstream != _upstream) throw new IOException("现有守护的 ClassIsland 测试端点不同，请先退出原实例。");
            if (existing && !string.Equals(registration!.BundleDirectory, Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
                throw new IOException("现有守护来自另一应用目录，请先正常退出原实例后再使用当前版本。");
            registration = registration is null ? new(Guid.NewGuid(), GuardProcess.Current(), _upstream, Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))
                : registration with { App = GuardProcess.Current() };
            files.Write("registration.json", registration);
            if (!existing)
            {
                string executable = Path.Combine(AppContext.BaseDirectory, "Guard", "NPEduTools.Guard.exe");
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(executable)! };
                start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(_pipe);
                using var process = Process.Start(start);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { HomeMessage.Text = "定时监测守护未启动：" + e.Message; }
    }
}
