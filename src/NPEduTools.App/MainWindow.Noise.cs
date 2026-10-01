using System.Windows;

namespace NPEduTools.App;

public partial class MainWindow
{
    private NoiseWindow? _noiseWindow;
    private void OpenNoiseClicked(object sender, RoutedEventArgs e)
    {
        _quick?.Collapse(false);
        if (_noiseWindow is null)
        {
            _noiseWindow = new NoiseWindow(_pipe);
            Closed += (_, _) => _noiseWindow.Shutdown();
        }
        _noiseWindow.Show();
        if (_noiseWindow.WindowState == WindowState.Minimized) _noiseWindow.WindowState = WindowState.Normal;
        _noiseWindow.Activate();
    }
}
