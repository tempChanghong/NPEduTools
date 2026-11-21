using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NPEduTools.App;

internal static partial class Program
{
    private static void CheckTeacherDpi(Window window, Size size)
    {
        window.UpdateLayout();
        var viewport = new Rect(0, 0, window.ActualWidth, window.ActualHeight);
        Assert(Math.Abs(viewport.Width - size.Width) <= 1 && Math.Abs(viewport.Height - size.Height) <= 1,
            "native viewport did not take the requested logical size");
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(window) { Stretch = Stretch.Fill }, null, viewport);
        foreach (double scale in new[] { 1d, 1.25, 1.5 })
        {
            var bitmap = new RenderTargetBitmap((int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(_output, $"teacher-{size.Width}x{size.Height}-{100 * scale}.png"));
            encoder.Save(file);
        }
    }

    private static void CheckTeacherThemeAndFont(MainWindow main)
    {
        var app = Application.Current.Resources;
        var nav = (Button)main.FindName("HomeNav");
        var mark = (TextBlock)main.FindName("HomeRecordingPhaseMark");
        var sidebar = (Border)main.FindName("WorkspaceSidebarSurface");
        var content = (Grid)main.FindName("WorkspaceContentSurface");
        var originalMark = ((SolidColorBrush)mark.Foreground).Color;
        try
        {
            NPEduTools.App.App.SetTeacherContrast(app, true);
            main.UpdateLayout();
            Assert(((SolidColorBrush)mark.Foreground).Color == SystemColors.WindowTextColor &&
                ((SolidColorBrush)nav.Background).Color == SystemColors.HighlightColor &&
                ((SolidColorBrush)nav.Foreground).Color == SystemColors.HighlightTextColor &&
                ((SolidColorBrush)sidebar.Background).Color == SystemColors.WindowColor &&
                ((SolidColorBrush)content.Background).Color == SystemColors.WindowColor,
                "an existing window did not follow the system contrast palette");
            Snapshot(main, "teacher-system-colors.png");
            app["TeacherFont"] = new FontFamily("Microsoft YaHei UI");
            main.UpdateLayout();
            Assert(nav.FontFamily.Source == "Microsoft YaHei UI", "the active navigation ignored the Chinese fallback font");
            var face = new Typeface(nav.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            Assert(face.TryGetGlyphTypeface(out var glyphs) && "课堂录制".All(ch => glyphs.CharacterToGlyphMap.ContainsKey(ch)),
                "Chinese fallback does not cover the task labels");
        }
        finally
        {
            app.Remove("TeacherFont");
            NPEduTools.App.App.SetTeacherContrast(app, false);
        }
        main.UpdateLayout();
        Assert(((SolidColorBrush)mark.Foreground).Color == originalMark, "the default palette was not restored");
        Checks.Add($"Home tasks rendered at 760×520, 1024×540 and 1280×700 DIP, each at 96/120/144 render DPI. Native window DPI {VisualTreeHelper.GetDpi(main).PixelsPerInchX:F0}. Live system colors and Microsoft YaHei UI glyphs verified.");
    }
}
