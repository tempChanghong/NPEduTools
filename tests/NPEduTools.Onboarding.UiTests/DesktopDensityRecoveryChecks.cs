using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using NPEduTools.App;

internal static partial class Program
{
    private static void RunDesktopDensityRecoveryChecks()
    {
        var failures = new List<Exception>();
        foreach (string invalid in new[] { "unsupported", "oversized", "invalid-json", "wrong-type" })
        foreach (string surface in new[] { "main", "quick", "dialog" })
            try { DesktopDensityRecoveryCheck(invalid, surface); }
            catch (Exception error) { failures.Add(new InvalidOperationException($"{invalid}/{surface}: {error.GetBaseException().Message}", error)); }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    private static void DesktopDensityRecoveryCheck(string invalid, string surface)
    {
        string endpoint = "NPEduTools.Test.density-recovery." + Guid.NewGuid().ToString("N");
        string path = StartupPreferencesStore.PathFor(endpoint) + ".density";
        string original = invalid switch
        {
            "unsupported" => "0.7",
            "oversized" => "0.8" + new string(' ', 128),
            "invalid-json" => "{",
            _ => "\"compact\""
        };
        var assembly = typeof(MainWindow).Assembly;
        var density = assembly.GetType("NPEduTools.App.DesktopDensity")!;
        var windows = new List<Window>();
        Window Open()
        {
            Window window;
            if (surface == "main")
            {
                var main = PrivateWorkspace(endpoint); windows.Add(main);
                // Exercise the real startup preference read without starting App services.
                typeof(MainWindow).GetMethod("InitializeDensity", WorkspaceFlags)!.Invoke(main, [true]);
                window = main;
            }
            else if (surface == "quick")
            {
                window = new QuickAccessWindow(endpoint, () => { }, () => { }, () => { }, () => { }, _ => { }, () => { }, () => { });
                windows.Add(window);
            }
            else
            {
                window = new Window { Width = 520, Height = 360, Content = new Border() };
                windows.Add(window);
                assembly.GetType("NPEduTools.App.DesktopDialogSizing")!.GetMethod("Attach")!.Invoke(null, [window, endpoint]);
            }
            new WindowInteropHelper(window).EnsureHandle();
            window.Show(); window.UpdateLayout();
            Assert(window.IsVisible && ((FrameworkElement)window.Content).LayoutTransform is ScaleTransform { ScaleX: > 0 and <= 1 },
                "invalid density prevented a usable automatically sized window");
            return window;
        }
        void CloseAll()
        {
            foreach (var window in windows)
                if (window is QuickAccessWindow quick) quick.Shutdown(); else window.Close();
            windows.Clear();
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, original);
            byte[] before = File.ReadAllBytes(path);
            var fallback = Open();
            if (fallback is MainWindow main)
                Assert(((TextBlock)main.FindName("DensityMessage")).Text.Contains("自动适配"), "main window omitted the density fallback explanation");
            Assert(File.ReadAllBytes(path).SequenceEqual(before), "automatic fallback overwrote the invalid preference");
            CloseAll();
            // An explicit subsequent choice must recover; fallback never locks out settings.
            density.GetMethod("Save")!.Invoke(null, [endpoint, .8]);
            var recovered = Open();
            if (surface != "dialog")
                Assert((double)recovered.GetType().GetField("_densityPreference", WorkspaceFlags)!.GetValue(recovered)! == .8,
                    "corrected density was not reloaded on reopening");
            Assert((double)density.GetMethod("Read")!.Invoke(null, [endpoint])! == .8, "explicit density choice did not remain saved");
            Checks.Add($"Density {invalid}/{surface}: real preference reader falls back without overwriting source; valid explicit choice reopens normally, no App startup or Host");
        }
        finally
        {
            CloseAll();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
