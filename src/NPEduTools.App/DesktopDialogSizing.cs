using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace NPEduTools.App;

internal static class DesktopDialogSizing
{
    public static void Attach(Window window, string endpoint)
    {
        double designWidth = window.Width, designHeight = window.Height, minimumWidth = window.MinWidth, minimumHeight = window.MinHeight;
        void Fit()
        {
            var area = Forms.Screen.FromHandle(new WindowInteropHelper(window).Handle).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(window);
            double width = area.Width / dpi.DpiScaleX, height = area.Height / dpi.DpiScaleY, preference = 0;
            try { preference = DesktopDensity.Read(endpoint); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
            double scale = DesktopDensity.Resolve(preference, width, height, designWidth, designHeight);
            ((FrameworkElement)window.Content).LayoutTransform = new ScaleTransform(scale, scale);
            window.MinWidth = Math.Min(minimumWidth * scale, width - 16); window.MinHeight = Math.Min(minimumHeight * scale, height - 16);
            window.Width = Math.Min(designWidth * scale, width - 16); window.Height = Math.Min(designHeight * scale, height - 16);
        }
        window.SourceInitialized += (_, _) => Fit();
        window.DpiChanged += (_, _) => window.Dispatcher.BeginInvoke(Fit);
    }
}
