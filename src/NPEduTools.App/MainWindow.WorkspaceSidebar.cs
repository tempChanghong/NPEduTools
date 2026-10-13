using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace NPEduTools.App;

public partial class MainWindow
{
    private bool? _workspaceSidebarChoice;
    private readonly List<(Button Button, TextBlock Label, TextBlock Icon)> _workspaceNavigation = [];
    private void InitializeWorkspaceSidebar()
    {
        static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T typed) yield return typed;
                foreach (var descendant in Children<T>(child)) yield return descendant;
            }
        }
        Loaded += (_, _) =>
        {
            if (_workspaceNavigation.Count == 0)
            {
                foreach (var button in Children<Button>(WorkspaceSidebar))
                {
                    if (button.Content is string caption)
                    {
                        string glyph = AutomationProperties.GetAutomationId(button) switch
                        { "RecordingNav" => "\uE714", "OpenRecordingPlan" => "\uE787", "OpenExamAware" => "\uE7C3",
                            "OpenSecRandom" => "\uE8D4", "OpenClassroomMode" => "\uE713", "StopHost" => "\uE8AC", _ => "\uE8A7" };
                        var row = new StackPanel { Orientation = Orientation.Horizontal };
                        row.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), VerticalAlignment = VerticalAlignment.Center });
                        row.Children.Add(new TextBlock { Text = caption });
                        button.Content = row;
                    }
                    if (button.Content is StackPanel { Children.Count: 2 } content && content.Children[0] is TextBlock icon && content.Children[1] is TextBlock label)
                    {
                        button.ToolTip ??= label.Text;
                        AutomationProperties.SetName(button, label.Text);
                        _workspaceNavigation.Add((button, label, icon));
                    }
                }
            }
            ApplyWorkspaceSidebar();
        };
        SizeChanged += (_, _) => ApplyWorkspaceSidebar();
    }
    private void FoldWorkspaceSidebarClicked(object sender, RoutedEventArgs e)
    { _workspaceSidebarChoice = WorkspaceNavColumn.Width.Value > 100; ApplyWorkspaceSidebar(); }
    private void ApplyWorkspaceSidebar()
    {
        bool compact = _workspaceSidebarChoice ?? ActualWidth < 900;
        WorkspaceNavColumn.Width = new GridLength(compact ? 64 : 164);
        WorkspaceSidebar.Margin = new Thickness(compact ? 6 : 12, 24, compact ? 6 : 12, 18);
        foreach (var (button, label, icon) in _workspaceNavigation)
        {
            label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            icon.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 10, 0);
            button.Padding = new Thickness(compact ? 8 : 12, 8, compact ? 8 : 12, 8);
        }
        foreach (var label in WorkspaceBrand.Children.OfType<TextBlock>()) label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        foreach (var scroll in WorkspaceSidebar.Children.OfType<ScrollViewer>())
            if (scroll.Content is StackPanel items)
                foreach (var group in items.Children.OfType<TextBlock>()) group.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }
}
