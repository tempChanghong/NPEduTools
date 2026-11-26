using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using NPEduTools.App;

internal static partial class Program
{
    private static void RunWorkspaceSidebarChecks()
    {
        string pipe = "NPEduTools.Test.workspace-sidebar." + Guid.NewGuid().ToString("N");
        var main = PrivateWorkspace(pipe);
        // The real shell can hide and show again. Exercise's modal window cannot.
        main.Show();
        try
        {
            PumpUntil(() => main.IsLoaded, "the non-modal workspace did not finish loading");
            var hwnd = new WindowInteropHelper(main).Handle;
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
            var collapseToEdge = Button(main, "CollapseToSideRail", automation: true);
            var nav = items.Children.OfType<Button>().Where(button => !ReferenceEquals(button, more)).ToArray();
            var groups = new[] { "WorkspaceCommonGroupLabel", "WorkspaceClassroomGroupLabel", "WorkspaceConfigurationGroupLabel" }
                .Select(name => (FrameworkElement)main.FindName(name)).ToArray();

            Resize(1040, 760);
            Assert(nav.Select(button => button.Name).SequenceEqual(new[] { "HomeNav", "ShortcutNav", "StudyNav", "RecordingNav",
                "SecRandomNav", "ExamAwareNav", "ClassroomModeNav", "NpepSettingsTab", "SettingsNav", "AboutNav" }),
                "classroom tools are interrupted by configuration, or About still occupies the action footer");
            Assert(nav.All(button => button.IsVisible && Label(button).IsVisible) && groups.All(group => group.IsVisible) && !more.IsVisible &&
                footer.Children.OfType<Button>().SequenceEqual(new[] { quick, exit }),
                "wide navigation lost a group or the action footer contains a page");
            Assert(Label(quick).TranslatePoint(new Point(Label(quick).ActualWidth, 0), surface).X < surface.ActualWidth,
                "the full-size quick entry label is clipped");
            Assert(AutomationProperties.GetName(quick).Contains("打开屏边") &&
                AutomationProperties.GetName(collapseToEdge).Contains("收起到屏边") &&
                AutomationProperties.GetName(exit) == "退出后台" && Label(exit).Text == "退出后台…" &&
                AutomationProperties.GetName(fold) == "折叠导航栏",
                "window and background actions no longer have distinct accessible names");
            Snapshot(main, "navigation-wide.png");

            Resize(840, 600);
            Assert(nav.All(button => !Label(button).IsVisible && FullyIn(button, scroll)) && !more.IsVisible &&
                groups.All(group => !group.IsVisible),
                "the consented minimum window lost a navigation item or showed an overflow menu");
            Assert(scroll.ComputedVerticalScrollBarVisibility == Visibility.Collapsed, "the minimum rail still shows a vertical scrollbar");
            VerifyCenters(nav.Where(button => button.IsVisible).Concat(footer.Children.OfType<Button>()));
            Assert(Glyph(Button(main, "SettingsNav")).Text != Glyph(Button(main, "ClassroomModeNav")).Text &&
                new[] { Glyph(Button(main, "ShortcutNav")).Text, Glyph(quick).Text, Glyph(collapseToEdge).Text, fold.Content.ToString() }
                    .Distinct().Count() == 4 && AutomationProperties.GetName(fold) == "展开导航栏",
                "icon-only actions still share confusing glyphs or the fold label is reversed");
            Assert(nav.All(button => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)) && button.ToolTip is string { Length: > 0 }),
                "a narrow navigation action has no readable label");
            Snapshot(main, "navigation-840x600.png");
            Checks.Add("1040×760 shows the three ordered groups and a two-action footer. At 840×600 all nine business destinations and About fit without a scrollbar, with distinct icons and at least 44×44 DIP targets.");

            Click(main, "FoldWorkspaceSidebar", automation: true);
            PumpUntil(() => nav.All(button => Label(button).IsVisible), "manual expansion did not restore full labels");
            Assert(fold.IsVisible && main.IsVisible && new WindowInteropHelper(main).Handle == hwnd,
                "expanding navigation changed the window or opened a panel");
            Click(main, "FoldWorkspaceSidebar", automation: true);
            PumpUntil(() => nav.All(button => !Label(button).IsVisible), "manual folding did not restore the compact visual");

            Click(main, "ClassroomModeNav");
            var destination = (ContentControl)main.FindName("ClassroomModeWorkspaceHost");
            PumpUntil(() => destination.IsVisible && Equals(Button(main, "ClassroomModeNav").Tag, "active"),
                "the direct route did not enter classroom mode");
            var expectedFocus = Button(main, "AboutNav");
            main.Activate();
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
            Assert(menuItems.Length == nav.Count(button => !button.IsVisible) &&
                menuItems.Any(item => Equals(item.Header, "关于")) &&
                menuItems.Any(item => AutomationProperties.GetName(item).Contains("课堂模式（当前页面）")) &&
                !menuItems.Any(item => Equals(item.Header, "退出后台")), "overflow inventory or current-page indicator is wrong");
            KeyPress((UIElement)Keyboard.FocusedElement, Key.Escape);
            PumpUntil(() => more.ContextMenu?.IsOpen != true && more.IsKeyboardFocused,
                "Escape failed to return keyboard focus to More");
            Assert(destination.IsVisible, "escaping the menu changed the current page");
            Resize(840, 600);
            PumpUntil(() => expectedFocus.IsKeyboardFocused, "regrowth restored the active route instead of the original focused entry");
            Assert(destination.IsVisible && Equals(Button(main, "ClassroomModeNav").Tag, "active"),
                "restoring keyboard focus switched the active page");
            Resize(840, 500);
            PumpUntil(() => more.IsKeyboardFocused, "shrinking a second time lost the focused destination");
            KeyPress(more, Key.Enter);
            PumpUntil(() => more.ContextMenu?.IsOpen == true, "overflow could not be reopened from the keyboard");
            var about = more.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "关于"));
            Assert(about.Focus(), "the About overflow entry was not keyboard focusable");
            KeyPress(about, Key.Enter);
            var aboutWorkspace = (FrameworkElement)main.FindName("AboutPageView");
            PumpUntil(() => aboutWorkspace.IsVisible && more.ContextMenu?.IsOpen != true,
                "keyboard overflow navigation did not use the original About page");
            // Native popup dismissal can reapply the physical display's density limits.
            // Restore the fixture's simulated short work area before checking the rail.
            Resize(840, 500);
            PumpUntil(() => more.IsVisible && !expectedFocus.IsVisible, "the simulated short rail did not overflow again");
            Snapshot(main, "navigation-about-short.png");
            Assert(AutomationProperties.GetName(more).Contains("关于") &&
                new WindowInteropHelper(main).Handle == hwnd && main.IsVisible,
                $"overflow navigation lost the original window or selection: {AutomationProperties.GetName(more)}, " +
                $"About tag={expectedFocus.Tag}, More tag={more.Tag}, About visible={expectedFocus.IsVisible}, " +
                $"main visible={main.IsVisible}, same HWND={new WindowInteropHelper(main).Handle == hwnd}");
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
            PumpUntil(() => nav.All(button => FullyIn(button, scroll)) && expectedFocus.IsKeyboardFocused,
                "resizing back did not restore the original active button and tab focus");
            Assert(scroll.VerticalOffset == 0 && scroll.ComputedVerticalScrollBarVisibility == Visibility.Collapsed,
                "returning to normal height retained the scrolled position");
            foreach (string id in new[] { "MinimizeWindow", "MaximizeRestoreWindow", "CloseWindow" })
                Assert(Button(main, id, automation: true).IsVisible, "original window chrome action missing: " + id);
            Checks.Add("At very short height the thin overlay preserves alignment and scrolls; restoring normal height brings back all entries and restores the original focus.");
            VerifyWindowActions();

            void VerifyWindowActions()
            {
                var edge = new QuickAccessWindow(pipe, () => { }, () => { }, () => { }, () => { }, _ => { }, () => { }, () => { });
                typeof(MainWindow).GetField("_quick", WorkspaceFlags)!.SetValue(main, edge);
                var panel = (FrameworkElement)edge.FindName("PanelSurface");
                var edgeHandle = (FrameworkElement)edge.FindName("EdgeHandle");
                try
                {
                    edge.ShowEntry();
                    Click(main, "FoldWorkspaceSidebar", automation: true);
                    PumpUntil(() => main.IsVisible && Label(Button(main, "StudyNav")).IsVisible && !panel.IsVisible,
                        "folding navigation opened the screen-edge panel or hid the main window");
                    Click(main, "FoldWorkspaceSidebar", automation: true);

                    Click(main, "CollapseToSideRail", automation: true);
                    PumpUntil(() => !main.IsVisible && edge.IsVisible && edgeHandle.IsVisible && !panel.IsVisible,
                        "title-bar collapse did not leave only the screen-edge entrance");
                    Snapshot(edge, "screen-edge-entry-only.png");
                    main.RestoreWindow();
                    Click(main, "WorkspaceOpenQuick");
                    PumpUntil(() => !main.IsVisible && edge.IsVisible && panel.IsVisible,
                        "the lower action left only a handle instead of opening the quick panel");
                    Snapshot(edge, "screen-edge-panel-open.png");
                    main.RestoreWindow();
                    Assert(new WindowInteropHelper(main).Handle == hwnd && aboutWorkspace.IsVisible,
                        "returning from the edge replaced the main window or its current page");

                    bool cancelledSafely = false;
                    var cancelConfirmation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
                    cancelConfirmation.Tick += (_, _) =>
                    {
                        if (Application.Current.Windows.OfType<StudyActionConfirmation>().FirstOrDefault(w => w.IsVisible) is not { } dialog) return;
                        cancelConfirmation.Stop();
                        var cancel = Button(dialog, "CancelAction");
                        cancelledSafely = dialog.Owner == main && cancel.IsDefault && cancel.IsCancel && main.IsVisible;
                        ((IInvokeProvider)new ButtonAutomationPeer(cancel).GetPattern(PatternInterface.Invoke)!).Invoke();
                    };
                    cancelConfirmation.Start();
                    try { Click(main, "ExitButton"); }
                    finally { cancelConfirmation.Stop(); }
                    Assert(cancelledSafely && main.IsVisible && exit.IsEnabled,
                        "the separate exit action no longer opens its protected confirmation and returns safely on cancel");
                    Checks.Add("Actual buttons: folding changes only the navigation, caption collapse leaves an edge entrance, and the lower action opens the existing edge panel. Returning preserves the native main window and page; exit defaults to cancel.");
                }
                finally
                {
                    typeof(MainWindow).GetField("_quick", WorkspaceFlags)!.SetValue(main, null);
                    edge.Shutdown();
                }
            }

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
        }
        finally { main.Close(); }
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
