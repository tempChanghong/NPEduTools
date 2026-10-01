using System.Windows;

namespace NPEduTools.App;

public partial class MainWindow
{
    private void GeneralSettingsClicked(object sender, RoutedEventArgs e) => SelectSettingsCategory("General");
    private void TeachingSettingsClicked(object sender, RoutedEventArgs e) => SelectSettingsCategory("Teaching");
    private void ConnectionsSettingsClicked(object sender, RoutedEventArgs e) => ShowConnectionSettings();
    private void NpepSettingsClicked(object sender, RoutedEventArgs e) => SelectSettingsCategory("Npep");

    private void SelectSettingsCategory(string category)
    {
        GeneralSettingsPanel.Visibility = category == "General" ? Visibility.Visible : Visibility.Collapsed;
        TeachingSettingsPanel.Visibility = category == "Teaching" ? Visibility.Visible : Visibility.Collapsed;
        ConnectionsSettingsPanel.Visibility = category == "Connections" ? Visibility.Visible : Visibility.Collapsed;
        NpepSettingsPanel.Visibility = category == "Npep" ? Visibility.Visible : Visibility.Collapsed;
        GeneralSettingsTab.Tag = category == "General" ? "active" : null;
        TeachingSettingsTab.Tag = category == "Teaching" ? "active" : null;
        ConnectionsSettingsTab.Tag = category == "Connections" ? "active" : null;
        NpepSettingsTab.Tag = category == "Npep" ? "active" : null;
        SettingsScroll.ScrollToTop();
        if (category == "Connections") _ = RefreshAdminAsync();
        if (category == "Npep") _ = RefreshNpepAsync();
    }

    private void ShowConnectionSettings()
    {
        ShowSettings();
        SelectSettingsCategory("Connections");
    }

    private void ShowClassIslandPathSettings()
    {
        ShowConnectionSettings();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            ExecutablePathBox.BringIntoView();
            ExecutablePathBox.Focus();
        });
    }
}
