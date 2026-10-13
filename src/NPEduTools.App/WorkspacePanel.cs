using System.Windows;
using System.Windows.Controls;

namespace NPEduTools.App;

// Hosts the existing dialog controller without creating a second native window.
// Its commands, revision fences and close/cancel path remain the same as the dialog.
internal sealed class WorkspacePanel : ContentControl
{
    internal Window Controller { get; }
    private bool _loaded;
    internal WorkspacePanel(Window controller, Window owner)
    {
        Controller = controller;
        controller.Owner = owner;
        var view = (FrameworkElement)controller.Content;
        controller.Content = null;
        Content = view;
        Resources = controller.Resources;
        FontFamily = controller.FontFamily;
        FontSize = controller.FontSize;
        Foreground = controller.Foreground;
        // Dialogs have their own outer margin; the workspace already provides one.
        var scroll = view as ScrollViewer ?? (view as Panel)?.Children.OfType<ScrollViewer>().FirstOrDefault();
        if (scroll?.Content is StackPanel body) body.Margin = new Thickness(4, 0, 4, 12);
        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            controller.RaiseEvent(new RoutedEventArgs(LoadedEvent, controller));
        };
        controller.SetValue(ViewProperty, view);
    }
    private static readonly DependencyProperty ViewProperty = DependencyProperty.RegisterAttached(
        "View", typeof(FrameworkElement), typeof(WorkspacePanel));
    internal static FrameworkElement? View(Window controller) => controller.Content as FrameworkElement ??
        controller.GetValue(ViewProperty) as FrameworkElement;
    internal static bool Embedded(Window controller) => controller.GetValue(ViewProperty) is FrameworkElement;
    internal static bool Visible(Window controller) => controller.IsVisible ||
        controller.GetValue(ViewProperty) is FrameworkElement { IsVisible: true };
    internal static Window DialogOwner(Window controller) => Embedded(controller) && controller.Owner is { } owner ? owner : controller;
}
