using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace NPEduTools.App;

public partial class MainWindow
{
    private bool? _workspaceSidebarChoice;
    private bool _workspaceSidebarCompact;
    private int _workspaceOverflowCount;
    private readonly List<(Button Button, TextBlock Label, TextBlock Icon)> _workspaceNavigation = [];
    private Button[] _workspaceOverflowCandidates = [];

    private void InitializeWorkspaceSidebar()
    {
        // In the compact rail the specialist tools move first. The classroom essentials
        // and explicit background action stay reachable at every height.
        _workspaceOverflowCandidates = [ClassroomModeNav, ExamAwareNav, SecRandomNav];
        var activeTag = DependencyPropertyDescriptor.FromProperty(TagProperty, typeof(Button));
        foreach (var button in _workspaceOverflowCandidates)
            activeTag.AddValueChanged(button, WorkspaceOverflowSelectionChanged);
        Closed += (_, _) =>
        {
            foreach (var button in _workspaceOverflowCandidates)
                activeTag.RemoveValueChanged(button, WorkspaceOverflowSelectionChanged);
        };
        Loaded += (_, _) =>
        {
            if (_workspaceNavigation.Count == 0)
            {
                foreach (var button in WorkspaceNavigationItems.Children.OfType<Button>()
                    .Concat(WorkspaceNavigationFooter.Children.OfType<Button>()))
                {
                    if (button.Content is StackPanel { Children.Count: 2 } content &&
                        content.Children[0] is TextBlock icon && content.Children[1] is TextBlock label)
                    {
                        string accessibleName = ReferenceEquals(button, WorkspaceOpenQuick) ? "打开屏边快捷面板" : label.Text;
                        button.ToolTip ??= accessibleName;
                        AutomationProperties.SetName(button, accessibleName);
                        _workspaceNavigation.Add((button, label, icon));
                    }
                }
            }
            ApplyWorkspaceSidebar();
        };
        SizeChanged += (_, _) => ApplyWorkspaceSidebar();
        WorkspaceNavigationScroll.SizeChanged += (_, _) => ApplyWorkspaceOverflow();
    }

    private void FoldWorkspaceSidebarClicked(object sender, RoutedEventArgs e)
    { _workspaceSidebarChoice = WorkspaceNavColumn.Width.Value > 100; ApplyWorkspaceSidebar(); }

    private void ApplyWorkspaceSidebar()
    {
        bool compact = _workspaceSidebarChoice ?? ActualWidth < 900;
        _workspaceSidebarCompact = compact;
        WorkspaceNavColumn.Width = new GridLength(compact ? 64 : 164);
        WorkspaceSidebar.Margin = compact ? new Thickness(6, 8, 6, 8) : new Thickness(12, 13, 12, 14);
        WorkspaceBrand.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        WorkspaceBrand.Margin = compact ? new Thickness(0) : new Thickness(8, 0, 0, 0);
        foreach (var (button, label, icon) in _workspaceNavigation)
        {
            label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            icon.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 9, 0);
            button.Padding = compact ? new Thickness(0) : new Thickness(12, 8, 12, 8);
            button.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 0, 2);
            button.HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        }
        WorkspaceNavigationScroll.Margin = compact ? new Thickness(0, 6, 0, 2) : new Thickness(0, 18, 0, 8);
        WorkspaceNavDivider1.Margin = WorkspaceNavDivider2.Margin = compact ? new Thickness(8, 2, 8, 2) : new Thickness(10, 8, 10, 8);
        WorkspaceFooterDivider.Margin = compact ? new Thickness(8, 2, 8, 2) : new Thickness(12, 8, 12, 8);
        foreach (var label in WorkspaceBrand.Children.OfType<TextBlock>()) label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ApplyWorkspaceOverflow();
    }

    private void ApplyWorkspaceOverflow()
    {
        if (_workspaceNavigation.Count == 0) return;

        int count = 0;
        double available = WorkspaceNavigationScroll.ActualHeight;
        bool needsScroll = false;
        if (_workspaceSidebarCompact && available > 0)
        {
            double required = WorkspaceNavigationItems.Children.OfType<Button>()
                .Where(button => !ReferenceEquals(button, WorkspaceNavigationMore)).Sum(SidebarHeight) +
                SidebarHeight(WorkspaceNavDivider1) + SidebarHeight(WorkspaceNavDivider2);
            if (required > available + 1)
            {
                required += SidebarHeight(WorkspaceNavigationMore);
                while (count < _workspaceOverflowCandidates.Length && required > available + 1)
                    required -= SidebarHeight(_workspaceOverflowCandidates[count++]);
                if (count == _workspaceOverflowCandidates.Length)
                    required -= SidebarHeight(WorkspaceNavDivider2);
                needsScroll = required > available + 1;
            }
        }

        // Only an undersized rail needs the overlay. It does not reserve width
        // beside the buttons when wheel, touch or keyboard scrolling is needed.
        WorkspaceNavigationScroll.VerticalScrollBarVisibility = _workspaceSidebarCompact
            ? needsScroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
        if (!needsScroll && _workspaceSidebarCompact && WorkspaceNavigationScroll.VerticalOffset != 0)
            WorkspaceNavigationScroll.ScrollToTop();
        if (_workspaceOverflowCount == count) return;

        var focused = Keyboard.FocusedElement;
        bool focusMore = _workspaceOverflowCandidates.Take(count).Any(button => ReferenceEquals(button, focused));
        bool restoreVisible = count == 0 && (ReferenceEquals(focused, WorkspaceNavigationMore) || WorkspaceNavigationMore.ContextMenu?.IsOpen == true);
        if (WorkspaceNavigationMore.ContextMenu is { IsOpen: true } popup) popup.IsOpen = false;
        _workspaceOverflowCount = count;
        for (int index = 0; index < _workspaceOverflowCandidates.Length; index++)
            _workspaceOverflowCandidates[index].Visibility = index < count ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceNavDivider2.Visibility = count == _workspaceOverflowCandidates.Length ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceNavigationMore.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceOverflowSelectionChanged(this, EventArgs.Empty);
        if (focusMore) WorkspaceNavigationMore.Focus();
        else if (restoreVisible)
            (_workspaceOverflowCandidates.FirstOrDefault(button => Equals(button.Tag, "active")) ?? ClassroomModeNav).Focus();
    }

    private static double SidebarHeight(FrameworkElement element) =>
        (double.IsNaN(element.Height) ? element.MinHeight : element.Height) + element.Margin.Top + element.Margin.Bottom;

    private void WorkspaceOverflowSelectionChanged(object? sender, EventArgs e)
    {
        var current = _workspaceNavigation.Find(item => Equals(item.Button.Tag, "active") &&
            _workspaceOverflowCandidates.Take(_workspaceOverflowCount).Contains(item.Button)).Label?.Text;
        WorkspaceNavigationMore.Tag = current is null ? null : "active";
        string label = current is null ? "更多导航" : $"更多导航 · 当前：{current}";
        WorkspaceNavigationMore.ToolTip = label;
        AutomationProperties.SetName(WorkspaceNavigationMore, label);
    }

    private void WorkspaceNavigationMoreClicked(object sender, RoutedEventArgs e)
    {
        if (_workspaceOverflowCount == 0) return;
        var menu = new ContextMenu { PlacementTarget = WorkspaceNavigationMore, Placement = PlacementMode.Right };
        bool openedDestination = false;
        foreach (var (button, label, icon) in _workspaceNavigation)
        {
            if (!_workspaceOverflowCandidates.Take(_workspaceOverflowCount).Contains(button)) continue;
            bool current = Equals(button.Tag, "active");
            var item = new MenuItem
            {
                Header = label.Text, IsEnabled = button.IsEnabled, MinHeight = 44,
                Icon = new TextBlock { Text = current ? "\uE73E" : icon.Text, FontFamily = icon.FontFamily }
            };
            AutomationProperties.SetName(item, current ? $"{label.Text}（当前页面）" : label.Text);
            AutomationProperties.SetAutomationId(item, AutomationProperties.GetAutomationId(button) + "Overflow");
            item.Click += (_, _) =>
            {
                if (!button.IsEnabled) return;
                openedDestination = true;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            };
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (WorkspaceNavigationMore.IsVisible && IsActive &&
                (!openedDestination || Keyboard.FocusedElement is null or MenuItem or System.Windows.Controls.ContextMenu))
                WorkspaceNavigationMore.Focus();
            if (ReferenceEquals(WorkspaceNavigationMore.ContextMenu, menu)) WorkspaceNavigationMore.ContextMenu = null;
        });
        WorkspaceNavigationMore.ContextMenu = menu;
        menu.IsOpen = true;
    }
}
