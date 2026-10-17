using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace NPEduTools.App;

public partial class MainWindow
{
    private double _densityPreference;
    private bool _densityReady;
    private sealed record DensityChoice(double Value, string Name);
    private void InitializeDensity(bool persist)
    {
        if (persist)
        {
            try { _densityPreference = DesktopDensity.Read(_pipe); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { DensityMessage.Text = "界面大小配置不可用，本次自动适配，原文件保留。"; }
        }
        DensityChoiceBox.ItemsSource = new[] { new DensityChoice(0, "自动适配（默认）"), new DensityChoice(.8, "紧凑 · 80%"),
            new DensityChoice(.9, "适中 · 90%"), new DensityChoice(1, "原始 · 100%") };
        DensityChoiceBox.SelectedValue = _densityPreference;
        _densityReady = persist;
        SourceInitialized += (_, _) => ApplyDensity();
        Loaded += (_, _) => ApplyDensity();
        IsVisibleChanged += (_, _) => { if (IsVisible) Dispatcher.BeginInvoke(ApplyDensity); };
        StateChanged += (_, _) => { if (WindowState == WindowState.Normal) Dispatcher.BeginInvoke(ApplyDensity); };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(ApplyDensity);
    }
    internal void ApplyDensity()
    {
        if (_lifetime.IsCancellationRequested) return;
        var area = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        double width = area.Width / dpi.DpiScaleX, height = area.Height / dpi.DpiScaleY;
        double scale = DesktopDensity.Resolve(_densityPreference, width, height);
        ((FrameworkElement)Content).LayoutTransform = new ScaleTransform(scale, scale);
        MinWidth = Math.Min(840 * scale, width - 16); MinHeight = Math.Min(600 * scale, height - 16);
        if (WindowState == WindowState.Normal)
        {
            Width = Math.Min(1040 * scale, width - 16); Height = Math.Min(760 * scale, height - 16);
            DesktopWindowBounds.KeepVisible(this);
        }
        _quick?.SetDensity(_densityPreference);
    }
    private void DensityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_densityReady || DensityChoiceBox.SelectedItem is not DensityChoice choice) return;
        try { DesktopDensity.Save(_pipe, choice.Value); _densityPreference = choice.Value; ApplyDensity(); DensityMessage.Text = "已保存，主页与快捷侧栏即时生效；全屏自习展示保持原有布局。"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { DensityMessage.Text = "未能保存界面大小，当前配置保留。"; }
    }
}
