using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NPEduTools.App;

internal static partial class Program
{
    private static void RunDesktopDensityChecks()
    {
        string pipe = "NPEduTools.Test.density." + Guid.NewGuid().ToString("N");
        var main = (MainWindow)Activator.CreateInstance(typeof(MainWindow), BindingFlags.NonPublic | BindingFlags.Instance,
            null, [pipe, null, false], null)!;
        double scale = (double)typeof(MainWindow).Assembly.GetType("NPEduTools.App.DesktopDensity")!.GetMethod("Resolve")!.Invoke(null, [0d, 1280d, 680d, 1040d, 760d])!;
        Exercise(main, owner =>
        {
            // Simulate available WPF units at 2560x1440 / 200% without changing Windows DPI.
            ((FrameworkElement)owner.Content).LayoutTransform = new ScaleTransform(scale, scale);
            owner.Width = 1040 * scale; owner.Height = 760 * scale;
            owner.UpdateLayout();
            Assert(owner.Width <= 1248 && owner.Height <= 648, "main window overflows 2K/200% work area");
            Assert(((ComboBox)owner.FindName("DensityChoiceBox")).Items.Count == 4, "density controls missing");
            Assert(((FrameworkElement)owner.Content).ActualWidth >= 1030, "shrinking window also cramped logical content");
            Snapshot(owner, "main-2k-200-home.png");
            ((Button)owner.FindName("SettingsNav")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); owner.UpdateLayout();
            Snapshot(owner, "main-2k-200-settings.png");
            owner.Close();
        });
        var quick = new QuickAccessWindow(pipe, () => { }, () => { }, () => { }, () => { }, _ => { }, () => { }, () => { });
        try
        {
            quick.Show(); quick.SetDensity(.8); quick.OpenPanel(); quick.UpdateLayout();
            Assert(((FrameworkElement)quick.Content).LayoutTransform is ScaleTransform { ScaleX: <= .8 }, "quick panel density not applied");
            Snapshot(quick, "quick-compact-expanded.png"); quick.Collapse(false); quick.UpdateLayout();
            Snapshot(quick, "quick-compact-rail.png");
        }
        finally { quick.Shutdown(); }
        Checks.Add("Real WPF home/settings at simulated 2K/200% available space; full/expanded rail scales consistently; no app startup, Host or sensors");
    }
}
