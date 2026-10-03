using System.Windows;

namespace NPEduTools.App;

public partial class MainWindow
{
    private SecRandomWindow? _secRandomWindow;
    private void OpenSecRandomClicked(object sender, RoutedEventArgs e) => ShowSecRandom();
    private void ShowSecRandom()
    {
        _quick?.Collapse(false);
        _secRandomWindow ??= new SecRandomWindow(_pipe);
        _secRandomWindow.Show();
        if (_secRandomWindow.WindowState == WindowState.Minimized) _secRandomWindow.WindowState = WindowState.Normal;
        _secRandomWindow.Activate();
    }
    private Task<string> QuickDrawSecRandomAsync()
    {
        _quick?.Collapse(false);
        _secRandomWindow ??= new SecRandomWindow(_pipe);
        return _secRandomWindow.QuickDrawFromSidebarAsync();
    }
}
