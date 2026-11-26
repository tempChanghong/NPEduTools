using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using NPEduTools.App;

internal static partial class Program
{
    private static void RunWorkspaceSidebarChecks()
    {
        var main = PrivateWorkspace("NPEduTools.Test.workspace-sidebar." + Guid.NewGuid().ToString("N"));
        Exercise(main, owner =>
        {
            var hwnd = new WindowInteropHelper(owner).Handle;
            var surface = (Border)main.FindName("WorkspaceSidebarSurface");
            var brand = (StackPanel)main.FindName("WorkspaceBrand");
            var logo = (Image)brand.Children[0];
            var scroll = (ScrollViewer)main.FindName("WorkspaceNavigationScroll");
            var items = (Panel)main.FindName("WorkspaceNavigationItems");
            var footer = (Panel)main.FindName("WorkspaceNavigationFooter");
            var more = Button(main, "WorkspaceNavigationMore");
            var quick = Button(main, "WorkspaceOpenQuick");
            var exit = Button(main, "ExitButton");
            var fold = Button(main, "FoldWorkspaceSidebar", automation: true);
            var nav = items.Children.OfType<Button>().Where(button => !ReferenceEquals(button, more)).ToArray();

            Resize(1040, 760);
            Assert(nav.Length == 9 && nav.All(button => button.IsVisible && Label(button).IsVisible) && !more.IsVisible,
                "wide navigation lost an existing label or destination");
            Assert(Label(quick).TranslatePoint(new Point(Label(quick).ActualWidth, 0), surface).X < surface.ActualWidth,
                "the full-size quick entry label is clipped");
            Assert(AutomationProperties.GetName(quick).Contains("打开屏边") &&
                AutomationProperties.GetName(Button(main, "CollapseToSideRail", automation: true)).Contains("收起到屏边") &&
                AutomationProperties.GetName(exit) == "退出后台",
                "window and background actions no longer have distinct accessible names");
            Snapshot(main, "navigation-wide.png");

            Resize(840, 600);
            Assert(nav.All(button => button.IsVisible && !Label(button).IsVisible && FullyIn(button, scroll)) && !more.IsVisible,
                "the consented minimum window lost a navigation item or showed an overflow menu");
            Assert(scroll.ComputedVerticalScrollBarVisibility == Visibility.Collapsed, "the minimum rail still shows a vertical scrollbar");
            VerifyCenters(nav.Concat(footer.Children.OfType<Button>()));
            Assert(Glyph(Button(main, "SettingsNav")).Text != Glyph(Button(main, "ClassroomModeNav")).Text &&
                Glyph(Button(main, "ShortcutNav")).Text != Glyph(quick).Text, "icon-only actions still share confusing glyphs");
            Assert(nav.All(button => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)) && button.ToolTip is string { Length: > 0 }),
                "a narrow navigation action has no readable label");
            Snapshot(main, "navigation-840x600.png");
            Checks.Add("1040×760 shows the original text. At 840×600 all nine navigation buttons fit without a scrollbar, with centered icons and at least 44×44 DIP targets.");

            Click(main, "FoldWorkspaceSidebar", automation: true);
            PumpUntil(() => nav.All(button => Label(button).IsVisible), "manual expansion did not restore full labels");
            Assert(fold.IsVisible && owner.IsVisible && new WindowInteropHelper(owner).Handle == hwnd,
                "expanding navigation changed the window or opened a panel");
            Click(main, "FoldWorkspaceSidebar", automation: true);
            PumpUntil(() => nav.All(button => !Label(button).IsVisible), "manual folding did not restore the compact visual");

            Click(main, "ClassroomModeNav");
            var destination = (ContentControl)main.FindName("ClassroomModeWorkspaceHost");
            PumpUntil(() => destination.IsVisible && Equals(Button(main, "ClassroomModeNav").Tag, "active"),
                "the direct route did not enter classroom mode");
            var expectedFocus = Button(main, "ExamAwareNav");
            owner.Activate();
            Assert(expectedFocus.Focus(), "could not focus the original navigation item");
            Resize(840, 500);
            PumpUntil(() => more.IsVisible && more.IsKeyboardFocused, "shrinking lost keyboard focus instead of moving it to More");
            Assert(!expectedFocus.IsVisible && Equals(more.Tag, "active") && AutomationProperties.GetName(more).Contains("课堂模式") &&
                !Button(main, "ClassroomModeNav").IsVisible && exit.IsVisible,
                "overflow concealed the current page or a fixed footer action");
            Assert(FullyIn(more, scroll) && scroll.ComputedVerticalScrollBarVisibility == Visibility.Collapsed,
                "the short rail partially hides its overflow entry");
            VerifyCenters(nav.Where(button => button.IsVisible).Append(more).Concat(footer.Children.OfType<Button>()));
            Snapshot(main, "navigation-short.png");

            KeyPress(more, Key.Enter);
            PumpUntil(() => more.ContextMenu?.IsOpen == true, "Enter did not open the overflow menu");
            var moreMenu = more.ContextMenu!;
            var menuItems = moreMenu.Items.OfType<MenuItem>().ToArray();
            Assert(menuItems.Length is 2 or 3 && menuItems.Any(item => AutomationProperties.GetName(item).Contains("课堂模式（当前页面）")) &&
                !menuItems.Any(item => Equals(item.Header, "退出后台")), "overflow inventory or current-page indicator is wrong");
            KeyPress((UIElement)Keyboard.FocusedElement, Key.Escape);
            PumpUntil(() => more.ContextMenu?.IsOpen != true && more.IsKeyboardFocused,
                "Escape failed to return keyboard focus to More");
            Assert(destination.IsVisible, "escaping the menu changed the current page");
            KeyPress(more, Key.Enter);
            PumpUntil(() => more.ContextMenu?.IsOpen == true, "overflow could not be reopened from the keyboard");
            var exam = more.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "ExamAware2"));
            Assert(exam.Focus(), "an overflow destination was not keyboard focusable");
            KeyPress(exam, Key.Enter);
            var examWorkspace = (ContentControl)main.FindName("ExamAwareWorkspaceHost");
            PumpUntil(() => examWorkspace.IsVisible && more.ContextMenu?.IsOpen != true,
                "keyboard overflow navigation did not use the ExamAware2 workspace");
            Assert(AutomationProperties.GetName(more).Contains("ExamAware2") &&
                new WindowInteropHelper(owner).Handle == hwnd && owner.IsVisible,
                "overflow navigation opened another window or lost the active selection");
            Checks.Add("On a 500-DIP-high window, More contains only the displaced entries, follows the active destination, supports Enter and Escape, and maps the keyboard choice back to the original main-window page.");

            Resize(840, 380);
            more.BringIntoView();
            PumpUntil(() => scroll.ScrollableHeight > 0 && scroll.VerticalOffset > 0 && FullyIn(more, scroll),
                "the exceptionally short rail could not scroll More into view");
            var scrollbar = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
            Assert(scrollbar.IsVisible && scrollbar.ActualWidth <= 4.5 && exit.IsVisible,
                "the short-height fallback no longer uses a small overlay or obscures the footer");
            VerifyCenters([more, quick, exit]);
            Assert(more.Focus(), "could not focus More before expanding");
            Resize(840, 600);
            PumpUntil(() => !more.IsVisible && nav.All(button => FullyIn(button, scroll)) && expectedFocus.IsKeyboardFocused,
                "resizing back did not restore the original active button and tab focus");
            Assert(scroll.VerticalOffset == 0 && scroll.ComputedVerticalScrollBarVisibility == Visibility.Collapsed,
                "returning to normal height retained the scrolled position");
            foreach (string id in new[] { "MinimizeWindow", "MaximizeRestoreWindow", "CloseWindow" })
                Assert(Button(main, id, automation: true).IsVisible, "original window chrome action missing: " + id);
            Checks.Add("At very short height the thin overlay preserves alignment and scrolls; restoring normal height brings back all entries and restores the original focus.");
            main.Close();

            void Resize(double width, double height)
            {
                // Model a reduced monitor work area without altering Windows display settings.
                main.MinHeight = Math.Min(440, height);
                main.Width = width;
                main.Height = height;
                main.UpdateLayout();
                PumpUntil(() => Math.Abs(main.ActualWidth - width) < 2 && Math.Abs(main.ActualHeight - height) < 2,
                    $"sidebar fixture did not reach {width}×{height}");
            }

            void VerifyCenters(IEnumerable<Button> buttons)
            {
                double axis = Center(logo);
                foreach (var button in buttons)
                {
                    Assert(button.ActualWidth >= 43.5 && button.ActualHeight >= 43.5, $"short hit target: {button.Name}");
                    Assert(Math.Abs(Center(Glyph(button)) - axis) < 1.25, $"misaligned icon: {button.Name}");
                }
            }

            double Center(FrameworkElement element) =>
                element.TranslatePoint(new Point(element.ActualWidth / 2, 0), surface).X;
        });
    }

    private static TextBlock Glyph(Button button) => (TextBlock)((StackPanel)button.Content).Children[0];
    private static TextBlock Label(Button button) => (TextBlock)((StackPanel)button.Content).Children[1];

    private static bool FullyIn(FrameworkElement element, ScrollViewer viewport)
    {
        if (!element.IsVisible) return false;
        var top = element.TranslatePoint(new Point(), viewport);
        var bottom = element.TranslatePoint(new Point(element.ActualWidth, element.ActualHeight), viewport);
        return top.Y >= -1 && bottom.Y <= viewport.ViewportHeight + 1 &&
            top.X >= -1 && bottom.X <= viewport.ViewportWidth + 1;
    }

    private static void KeyPress(UIElement source, Key key)
    {
        var presentation = PresentationSource.FromVisual(source)!;
        InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, presentation, Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, presentation, Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyUpEvent });
    }
}
