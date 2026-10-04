using System.Windows;
using System.Windows.Controls.Primitives;

namespace NPEduTools.App;

public partial class MainWindow
{
    private UniformGrid? _homeShortcutGrid;
    private void HomeLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement page) return;
        // Use the available page width in WPF units, including DPI scaling.
        int columns = page.ActualWidth >= 720 ? 3 : 2;
        HomeToolsGrid.Columns = columns;
        if (_homeShortcutGrid is not null) _homeShortcutGrid.Columns = columns;
    }

    private void HomeShortcutsLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is UniformGrid grid)
        {
            _homeShortcutGrid = grid;
            grid.Columns = HomeToolsGrid.Columns;
        }
    }

    private void HomeSchoolConnectionClicked(object sender, RoutedEventArgs e)
    {
        ShowSettings();
        SelectSettingsCategory("Npep");
    }
}
