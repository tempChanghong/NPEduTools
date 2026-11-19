using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace NPEduTools.App;

public partial class MainWindow
{
    private ResourceDictionary? _settingsContrast;

    private void SettingsRowsLoaded(object sender, RoutedEventArgs e)
    {
        SetSettingsContrast(SystemParameters.HighContrast);
        SystemParameters.StaticPropertyChanged += SettingsSystemParametersChanged;
    }
    private void SettingsRowsUnloaded(object sender, RoutedEventArgs e) =>
        SystemParameters.StaticPropertyChanged -= SettingsSystemParametersChanged;

    private void SettingsSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast)) SetSettingsContrast(SystemParameters.HighContrast);
    }
    private void SetSettingsContrast(bool enabled)
    {
        if (enabled == (_settingsContrast is not null)) return;
        var dictionaries = DisplayOperationSettings.Resources.MergedDictionaries;
        if (enabled)
        {
            _settingsContrast = new ResourceDictionary { Source = new Uri("/NPEduTools.App;component/Resources/SettingsRows.HighContrast.xaml", UriKind.Relative) };
            dictionaries.Add(_settingsContrast);
        }
        else
        {
            dictionaries.Remove(_settingsContrast!);
            _settingsContrast = null;
        }
    }

    private void EntrySideKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || e.Key is not (Key.Left or Key.Right)) return;
        var side = e.Key == Key.Left ? DockLeft : DockRight;
        side.Focus();
        side.IsChecked = true;
        e.Handled = true;
    }

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
        if (category == "Teaching") ReadManualStudyPreferences();
        if (category == "Connections") { _ = RefreshAdminAsync(); _ = RefreshClassIslandBridgeAsync(); }
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
        _classIslandConfigurationPresented = true;
        ClassIslandConfiguration.IsExpanded = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            ExecutablePathBox.BringIntoView();
            ExecutablePathBox.Focus();
        });
    }
    private void ConfigureClassIslandClicked(object sender, RoutedEventArgs e) => ShowClassIslandPathSettings();
}
