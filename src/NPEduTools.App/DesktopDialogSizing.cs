using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Runtime.CompilerServices;
using Forms = System.Windows.Forms;

namespace NPEduTools.App;

internal static class DesktopDialogSizing
{
    private static readonly ConditionalWeakTable<Window, object> Attached = new();
    internal static bool IsAttached(Window window) => Attached.TryGetValue(window, out _);
    public static void Attach(Window window, string? endpoint = null)
    {
        if (IsAttached(window)) return;
        Attached.Add(window, new());
        double designWidth = window.Width, designHeight = window.Height, minimumWidth = window.MinWidth, minimumHeight = window.MinHeight;
        bool automaticHeight = !double.IsFinite(designHeight) || window.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight;
        bool fitting = false, closed = false;
        void Fit()
        {
            if (fitting || closed) return;
            fitting = true;
            try
            {
                var area = Forms.Screen.FromHandle(new WindowInteropHelper(window).Handle).WorkingArea;
                var dpi = VisualTreeHelper.GetDpi(window);
                double width = area.Width / dpi.DpiScaleX, height = area.Height / dpi.DpiScaleY, preference = 0;
                string? currentEndpoint = endpoint ?? (window.Owner as MainWindow)?.PipeName;
                try { if (currentEndpoint is not null) preference = DesktopDensity.Read(currentEndpoint); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
                if (!double.IsFinite(designWidth)) designWidth = Math.Max(320, window.ActualWidth);
                if (automaticHeight && window.Content is FrameworkElement natural)
                {
                    natural.Measure(new Size(designWidth, double.PositiveInfinity));
                    designHeight = Math.Max(160, natural.DesiredSize.Height + 40);
                    // Auto-sized dialogs need a scrollable viewport instead of growing past the display.
                    window.Content = null;
                    window.Content = new ScrollViewer { Content = natural, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
                    window.SizeToContent = SizeToContent.Manual;
                    automaticHeight = false;
                }
                double scale = DesktopDensity.Resolve(preference, width, height, designWidth, designHeight);
                ((FrameworkElement)window.Content).LayoutTransform = new ScaleTransform(scale, scale);
                window.MinWidth = Math.Max(0, Math.Min(minimumWidth * scale, width - 16)); window.MinHeight = Math.Max(0, Math.Min(minimumHeight * scale, height - 16));
                if (window.WindowState == WindowState.Normal)
                {
                    window.Width = Math.Max(1, Math.Min(designWidth * scale, width - 16)); window.Height = Math.Max(1, Math.Min(designHeight * scale, height - 16));
                    DesktopWindowBounds.KeepVisible(window);
                }
            }
            finally { fitting = false; }
        }
        window.SourceInitialized += (_, _) => Fit();
        window.Closed += (_, _) => closed = true;
        // WPF can center a dialog after SourceInitialized. Correct the final native bounds.
        window.Loaded += (_, _) => Fit();
        window.IsVisibleChanged += (_, _) => { if (window.IsVisible) window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)Fit); };
        window.StateChanged += (_, _) => { if (window.WindowState == WindowState.Normal) window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)Fit); };
        window.DpiChanged += (_, _) => window.Dispatcher.BeginInvoke(Fit);
    }
}
