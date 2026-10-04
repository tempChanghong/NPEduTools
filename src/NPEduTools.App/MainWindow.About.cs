using System.Windows;

namespace NPEduTools.App;

public partial class MainWindow
{
    private void AboutNavClicked(object sender, RoutedEventArgs e)
    {
        SelectPage(false);
        HomePage.Visibility = Visibility.Collapsed;
        AboutPageView.Visibility = Visibility.Visible;
        HomeNav.Tag = null;
        AboutNav.Tag = "active";
        PageBreadcrumb.Text = "关于";
    }
}
