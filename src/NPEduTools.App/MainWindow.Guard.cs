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
            // Check before writing a new generation: rollback must retain the old identity.
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "guard-maintenance.json")))
                throw new IOException("安装器正在维护此目录，请等待维护完成后再启动应用。");
            bool existing = Mutex.TryOpenExisting($@"Local\{_pipe}.Guard", out var mutex);
            mutex?.Dispose();
            var registration = existing ? files.Read<GuardRegistration>("registration.json") : null;
            if (existing && (registration is null || files.Stopped(registration.Generation)))
                throw new IOException("旧守护正在停止，请稍后重新打开。");
            if (existing && registration!.Upstream != _upstream) throw new IOException("现有守护的 ClassIsland 测试端点不同，请维护人员先撤销旧守护登记。");
            if (existing && !string.Equals(registration!.BundleDirectory, Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
                throw new IOException("现有守护来自另一应用目录，请维护人员按 GUARD-MAINTENANCE.md 停止旧守护并撤销登记后切换目录。");
            registration = registration is null ? new(Guid.NewGuid(), GuardProcess.Current(), _upstream, Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))
                : registration with { App = GuardProcess.Current() };
            files.Write("registration.json", registration);
            var tasks = new GuardTasks(AppContext.BaseDirectory, _pipe, _upstream);
            tasks.Register();
            if (!existing) tasks.Run("Guard");
            _ = GuardWatchAsync(tasks, files, registration.Generation);
        }
        catch (Exception e) when (GuardTasks.StartupError(e))
        { HomeMessage.Text = "定时监测守护未启动：" + e.Message; }
    }
    private async Task GuardWatchAsync(GuardTasks tasks, GuardFiles files, Guid generation)
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(5000, _lifetime.Token);
                if (files.Stopped(generation) || files.Read<GuardRegistration>("registration.json")?.Generation != generation) return;
                if (!files.SessionEnding(generation))
                {
                    try { if (!tasks.GuardRunning()) tasks.Run("Guard"); }
                    catch (Exception e) when (GuardTasks.StartupError(e)) { HomeMessage.Text = "守护恢复请求失败：" + e.Message; }
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
}
