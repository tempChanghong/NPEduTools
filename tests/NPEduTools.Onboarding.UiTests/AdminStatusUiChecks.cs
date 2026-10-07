using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.ClassIsland.Admin;

internal static partial class Program
{
    private const string AdminFixturePath = @"C:\Isolated\ClassIsland.exe";
    private static readonly AdminStatus AdminFixtureStatus = new("Enabled", "隔离任务已启用", "isolated-fingerprint",
        "Administrator", "隔离管理员进程运行中", true);
    private static void AdminField(MainWindow window, string name, object? value) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static void AdminRefreshControls(MainWindow window) =>
        typeof(MainWindow).GetMethod("RefreshAdminControls", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
    private static void AdminSnapshot(MainWindow window, string path, AdminStatus status) =>
        typeof(MainWindow).GetMethod("ApplyAdminStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [path, status]);
    private static Task AdminRun(MainWindow window, string action, Func<string, string, string?, Task<AdminResult>> run) =>
        (Task)typeof(MainWindow).GetMethod("RunAdminAsync", BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(string), typeof(Func<string, string, string?, Task<AdminResult>>)], null)!.Invoke(window, [action, run])!;
    private static string AdminText(Window window, string name) => ((TextBlock)window.FindName(name)).Text;
    private static void AssertAdminUnknown(MainWindow window)
    {
        Assert(!AdminText(window, "AdminProcessStatus").Contains("隔离管理员") && !AdminText(window, "AdminPluginStatus").Contains("已安装"),
            "unknown administrator state still claims the previous administrator process or installed plugin");
        Assert(AdminText(window, "AdminTaskStatus").Contains("待核实") &&
            !Button(window, "AdminCreate").IsEnabled && !Button(window, "AdminDelete").IsEnabled,
            "unknown task state permits mutation or lacks an uncertainty message");
    }
    private static void RunAdminStatusChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            foreach (string scenario in new[] { "exception", "unknown", "new-path", "late-path", "cancelled" })
            {
                var window = IsolatedMainWindow("NPEduTools.Test.admin-ui." + Guid.NewGuid().ToString("N"));
                try
                {
                    Exercise(window, owner =>
                    {
                        try
                        {
                            AdminField(window, "_savedPath", AdminFixturePath);
                            ((TextBox)window.FindName("ExecutablePathBox")).Text = AdminFixturePath;
                            AdminSnapshot(window, AdminFixturePath, AdminFixtureStatus);
                            AdminRefreshControls(window);
                            Assert(Button(owner, "AdminDelete").IsEnabled && !Button(owner, "AdminElevate").IsEnabled, "initial synthetic administrator state did not render");
                            var completion = new TaskCompletionSource<AdminResult>();
                            int calls = 0;
                            string requested = scenario == "unknown" ? "delete" : scenario == "cancelled" ? "inspect" : "status";
                            Task<AdminResult> Run(string action, string path, string? fingerprint)
                            {
                                calls++;
                                Assert(action == requested && path == AdminFixturePath && fingerprint == AdminFixtureStatus.Fingerprint,
                                    "unexpected action or stale request identity");
                                return completion.Task;
                            }
                            Task? pending = null;
                            if (scenario != "new-path")
                            {
                                pending = AdminRun(window, requested, Run);
                                Assert(!Button(owner, "AdminRefresh").IsEnabled && !Button(owner, "AdminCreate").IsEnabled && !Button(owner, "AdminDelete").IsEnabled,
                                    "administrator controls remain available during a request");
                            }
                            if (scenario is "new-path" or "late-path")
                            {
                                AdminField(window, "_savedPath", @"C:\Other\ClassIsland.exe");
                                ((TextBox)window.FindName("ExecutablePathBox")).Text = @"C:\Other\ClassIsland.exe";
                                AdminRefreshControls(window);
                            }
                            if (pending is not null)
                            {
                                if (scenario == "exception") completion.SetException(new IOException("isolated unavailable helper"));
                                else completion.SetResult(scenario switch
                                {
                                    "unknown" => new("Unknown", "隔离丢失回执"),
                                    "cancelled" => new("Cancelled", "隔离取消授权"),
                                    _ => new("Succeeded", "隔离旧路径响应", AdminFixtureStatus)
                                });
                                PumpUntil(() => pending.IsCompleted, "administrator request did not finish"); pending.GetAwaiter().GetResult();
                                Assert(calls == 1, "administrator operation was replayed");
                            }
                            if (scenario == "cancelled")
                            {
                                Assert(AdminText(owner, "AdminProcessStatus") == AdminFixtureStatus.ProcessMessage &&
                                    Button(owner, "AdminDelete").IsEnabled && !Button(owner, "AdminElevate").IsEnabled,
                                    "cancelled authorization discarded an unchanged known snapshot");
                            }
                            else AssertAdminUnknown(window);
                            if (scenario == "exception")
                            {
                                // Render the real settings panel without navigation handlers, which would launch the real helper.
                                window.Width = 840; window.Height = 600;
                                foreach (string name in new[] { "HomePage", "GeneralSettingsPanel", "TeachingSettingsPanel", "NpepSettingsPanel" })
                                    ((FrameworkElement)window.FindName(name)).Visibility = Visibility.Collapsed;
                                ((FrameworkElement)window.FindName("SettingsPage")).Visibility = Visibility.Visible;
                                ((FrameworkElement)window.FindName("ConnectionsSettingsPanel")).Visibility = Visibility.Visible;
                                ((Expander)window.FindName("AdminSettingsExpander")).IsExpanded = true;
                                window.UpdateLayout();
                                ((FrameworkElement)window.FindName("AdminSection")).BringIntoView();
                                window.UpdateLayout();
                                Snapshot(owner, "administrator-unknown.png");
                            }

                            var fresh = new AdminStatus("Missing", "隔离新任务未创建", "fresh-fingerprint", "Stopped", "隔离新进程未运行", false);
                            int reads = 0;
                            var recovery = AdminRun(window, "status", (action, path, fingerprint) =>
                            {
                                reads++; Assert(action == "status" && fingerprint == (scenario == "cancelled" ? AdminFixtureStatus.Fingerprint : null),
                                    "recovery reused an invalid fingerprint or sent a mutation");
                                return Task.FromResult(new AdminResult("Succeeded", "隔离新状态", fresh));
                            });
                            PumpUntil(() => recovery.IsCompleted, "fresh administrator query did not finish"); recovery.GetAwaiter().GetResult();
                            Assert(reads == 1 && AdminText(owner, "AdminProcessStatus") == fresh.ProcessMessage &&
                                AdminText(owner, "AdminPluginStatus").Contains("未安装") && Button(owner, "AdminCreate").IsEnabled &&
                                !Button(owner, "AdminDelete").IsEnabled && Button(owner, "AdminElevate").IsEnabled,
                                "fresh status did not restore the correct administrator controls");
                            var pathBox = (TextBox)window.FindName("ExecutablePathBox");
                            string savedInput = pathBox.Text;
                            pathBox.Text = @"C:\Unsaved\ClassIsland.exe";
                            AdminRefreshControls(window);
                            Assert(!Button(owner, "AdminCreate").IsEnabled && !Button(owner, "AdminElevate").IsEnabled &&
                                AdminText(owner, "AdminPluginStatus").Contains("保存路径"), "unsaved input still exposes the saved path's controls or plugin status");
                            pathBox.Text = savedInput; AdminRefreshControls(window);
                            Assert(AdminText(owner, "AdminTaskStatus") == fresh.TaskMessage && AdminText(owner, "AdminProcessStatus") == fresh.ProcessMessage &&
                                AdminText(owner, "AdminPluginStatus").Contains("未安装") && Button(owner, "AdminCreate").IsEnabled,
                                "reverting unsaved input did not restore the known saved-path snapshot");
                            Checks.Add($"Administrator {scenario}: stale identity cleared or cancellation preserved; one fresh read recovers without mutation");
                        }
                        finally { window.Close(); }
                    });
                }
                finally { window.Close(); }
            }
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
}
