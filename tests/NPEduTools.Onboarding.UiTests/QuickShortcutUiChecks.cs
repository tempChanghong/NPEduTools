using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NPEduTools.App;

internal static partial class Program
{
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scanCode, uint flags, nuint extraInfo);

    private static void RunQuickShortcutChecks()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        const uint down = 0x10006, move = 0x20006, up = 0x40000;
        string pipe = "NPEduTools.Test.quick-shortcuts." + Guid.NewGuid().ToString("N");
        string catalogPath = ShortcutCatalog.PathFor(pipe);
        var items = Enumerable.Range(0, ShortcutCatalog.MaximumItems).Select(i => new ShortcutEntry(Guid.NewGuid(),
            $"课堂资料 {i + 1}", "file", Path.Combine(_output, $"测试文件 {i + 1}.pptx"))).ToArray();
        items[0] = items[0] with { Name = "课堂练习", Target = Path.Combine(_output, "练习-A.pptx") };
        items[^1] = items[^1] with { Name = "课堂练习", Target = Path.Combine(_output, "练习-B.pptx") };
        items[1] = items[1] with { Name = "打开关于 Windows", Kind = "app", Target = Path.Combine(Environment.SystemDirectory, "winver.exe") };
        var catalog = new ShortcutCatalog(catalogPath);
        catalog.Save(items);
        var main = IsolatedMainWindow(pipe);
        var mainType = typeof(MainWindow);
        void Invoke(string name, params object[] args) => mainType.GetMethod(name, flags)!.Invoke(main, args);
        int openCount = 0;
        var quick = new QuickAccessWindow(pipe, () => { }, () => { }, () => { }, main.RestoreWindow, entry =>
        {
            openCount++;
            Invoke("OpenShortcutFromRail", entry);
        }, () => Invoke("ShowShortcutManager"), () => Invoke("RepairShortcut"));
        var quickField = mainType.GetField("_quick", flags)!;
        quickField.SetValue(main, quick);
        var priorContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(main.Dispatcher));
        nint externalWindow = 0;
        var existingWindows = Process.GetProcessesByName("winver").Select(process =>
        {
            using (process) return process.Id;
        }).ToHashSet();
        try
        {
            Invoke("InitializeShortcuts");
            ActivateFocusFixture(quick, main);
            nint mainWindow = new WindowInteropHelper(main).Handle;
            quick.SetSide(false);
            quick.SetDensity(.9);
            quick.ShowEntry();
            quick.OpenPanel();
            Click(quick, "ShortcutsTab");
            PumpUntil(() => HasForeground(quick), "the real desktop shortcut panel did not take focus");
            quick.UpdateLayout();
            var rail = new WindowInteropHelper(quick).Handle;
            var panel = (FrameworkElement)quick.FindName("PanelSurface");
            var scroll = (ScrollViewer)quick.FindName("ShortcutsPage");
            var status = (TextBlock)quick.FindName("ShortcutStatus");
            var repair = Button(quick, "ShortcutRepair");
            var shortcutItems = (ItemsControl)quick.FindName("ShortcutItems");
            var first = Button(quick, items[0].OpenId, automation: true);
            Assert(InitializeTouchInjection(1, 3), "Windows touch injection unavailable: " + Marshal.GetLastWin32Error());
            Assert(FindQuickGrid(shortcutItems).Columns == 2, "the wider panel lost its two-column scan layout");
            Snapshot(quick, "01-shortcuts-wide.png");

            quick.SetDensity(.8);
            quick.UpdateLayout();
            Assert(ReadQuickTestRect(rail, out var before), "could not read the shortcut panel bounds");
            var work = System.Windows.Forms.Screen.FromHandle(rail).WorkingArea;
            int width = Math.Min(470, before.Right - before.Left), height = Math.Min(600, work.Height);
            int top = Math.Clamp(before.Top, work.Top, work.Bottom - height);
            Assert(ResizeQuickTestWindow(rail, 0, before.Right - width, top, width, height, 0x14),
                "could not cap the panel at the short native work area");
            // Position fixes the two grid columns to the current monitor work area.
            // Mirror that narrower panel column for this temporary HWND cap.
            var workspaceColumn = (ColumnDefinition)quick.FindName("LeftColumn");
            double pixelsPerLayoutUnit = VisualTreeHelper.GetDpi(quick).DpiScaleX * .8;
            workspaceColumn.Width = new GridLength(workspaceColumn.Width.Value - (before.Right - before.Left - width) / pixelsPerLayoutUnit);
            quick.UpdateLayout();
            Assert(FindQuickGrid(shortcutItems).Columns == 1, "the narrow native panel did not switch to a single column");
            var firstLocation = first.PointToScreen(new Point());
            var firstSize = RelativeBounds(first, quick);
            Assert(firstSize.Width >= 43.5 && firstSize.Height >= 43.5 && scroll.ScrollableHeight > 100,
                "the compact card is too small or the 24-item list does not overflow");
            Touch(first);
            PumpUntil(() => repair.IsVisible && repair.IsEnabled && status.Text.Contains("文件缺失"),
                "opening the first missing item did not offer a repair");
            var panelBounds = RelativeBounds(panel, quick);
            var repairBounds = RelativeBounds(repair, quick);
            Assert(panelBounds.Contains(RelativeBounds(status, quick)) && panelBounds.Contains(repairBounds) &&
                repairBounds.Height >= 43.5 && panelBounds.Contains(RelativeBounds(Button(quick, "ShortcutsFooter"), quick)),
                "the opening error and edit action were below the visible first screen");
            Assert(first.PointToScreen(new Point()) == firstLocation && scroll.VerticalOffset == 0 && openCount == 1,
                "the feedback moved the first card, scrolled the list or opened twice");
            Snapshot(quick, "02-shortcuts-narrow-missing.png");
            Checks.Add($"First missing item in a {width}×{height} px, {VisualTreeHelper.GetDpi(quick).PixelsPerInchX} DPI panel: failure and repair visible with no jump.");

            var point = ScreenCenter(Button(quick, items[2].OpenId, automation: true));
            Assert(WindowFromPoint(point) == rail && HasForeground(quick), "the drag did not begin on the shortcut card");
            SendTouch(down, point);
            try
            {
                for (int step = 0; step < 7; step++)
                {
                    Pause(22);
                    point.Y -= 14;
                    SendTouch(move, point);
                }
                Pause(22);
            }
            finally { SendTouch(up, point); }
            PumpUntil(() => scroll.VerticalOffset > 15, "touch did not scroll from the shortcut card");
            Assert(openCount == 1 && panel.IsVisible, "touch scrolling opened a different item or folded the panel");

            scroll.ScrollToEnd();
            PumpUntil(() => Math.Abs(scroll.ScrollableHeight - scroll.VerticalOffset) < .5, "could not reach the last shortcut");
            quick.UpdateLayout();
            double endOffset = scroll.VerticalOffset;
            ClickVisible(Button(quick, items[^1].OpenId, automation: true),
                () => openCount == 2 && repair.IsVisible && repair.IsEnabled, "the last shortcut did not offer its repair");
            Assert(scroll.VerticalOffset == endOffset && panelBounds.Contains(RelativeBounds(repair, quick)),
                "the last failing item's action jumped or was clipped");
            Touch(repair);
            var editor = (ShortcutEditorView)main.FindName("ShortcutEditor");
            var list = (ListBox)main.FindName("ShortcutList");
            var target = (TextBox)editor.FindName("TargetBox");
            PumpUntil(() => editor.IsVisible && HasForeground(main), "repair did not open inside the active main window");
            Assert((list.SelectedItem as ShortcutEntry)?.Id == items[^1].Id && target.Text == items[^1].Target &&
                (PresentationSource.FromVisual(editor) as HwndSource)?.Handle == mainWindow && !panel.IsVisible,
                "repair retargeted another same-named item or used another native window");
            string revised = Path.Combine(_output, "已修正练习-B.txt");
            File.WriteAllText(revised, "isolated class handout");
            target.Text = revised;
            Click(main, "ShortcutSave", true);
            var saved = catalog.Read();
            Assert(saved.Length == items.Length && saved[..^1].SequenceEqual(items[..^1]) && saved[^1] == items[^1] with { Target = revised },
                "repair changed the item ID, its position or the identically named shortcut");
            Checks.Add("Native pan on a card sent no launcher action. A second same-name failure repaired the exact ID in the same main HWND.");

            ReopenAtTop(items[1]);
            var revision = typeof(QuickAccessWindow).GetProperty("ShortcutViewRevision", flags)!;
            var handoff = typeof(QuickAccessWindow).GetMethod("CompleteShortcutHandoff", flags)!;
            object oldView = revision.GetValue(quick)!;
            Click(quick, "ToolsTab");
            handoff.Invoke(quick, [oldView]);
            Assert(panel.IsVisible && HasForeground(quick), "a delayed handoff from shortcuts folded the newly selected classroom tab");
            Click(quick, "ShortcutsTab");
            handoff.Invoke(quick, [revision.GetValue(quick)]);
            PumpUntil(() => !panel.IsVisible && HasForeground(main), "handoff with no newly focused window did not restore the prior focus");
            Checks.Add("A delayed handoff left the newly selected tab open. A handoff with no new foreground restored the original window.");

            ReopenAtTop(items[1]);
            Click(quick, items[1].OpenId, automation: true);
            Assert(openCount == 3, "the external shortcut did not receive the click");
            try
            {
                PumpUntil(() => ObserveExternalWindow() && !panel.IsVisible && status.Text.Contains("交给 Windows"),
                    "Windows did not hand off to the real external window");
            }
            finally
            {
                Checks.Add($"External launch: edge status={status.Text}, native window={externalWindow:X}, current foreground={QuickForeground():X}, rail opened={panel.IsVisible}.");
            }
            Assert(openCount == 3 && QuickForeground() == externalWindow, "the shortcut submitted twice or lost the external foreground");
            keybd_event(0x1B, 0, 0, 0);
            keybd_event(0x1B, 0, 2, 0);
            PumpUntil(() => QuickForeground() != externalWindow, "the opened system application did not receive the real Escape key");
            Checks.Add("A real Windows Shell handoff opened About Windows and folded the edge panel. The new system window received Escape.");

            bool ObserveExternalWindow()
            {
                nint candidate = QuickForeground();
                GetWindowThreadProcessId(candidate, out uint owner);
                if (owner == 0 || existingWindows.Contains((int)owner)) return false;
                try
                {
                    using var process = Process.GetProcessById((int)owner);
                    if (!process.ProcessName.Equals("winver", StringComparison.OrdinalIgnoreCase)) return false;
                    externalWindow = candidate;
                    return true;
                }
                catch (ArgumentException) { return false; }
            }

            void ReopenAtTop(ShortcutEntry firstVisible)
            {
                quick.OpenPanel();
                Click(quick, "ShortcutsTab");
                scroll.ScrollToTop();
                PumpUntil(() => scroll.VerticalOffset < .5 && HasForeground(quick) && Button(quick, firstVisible.OpenId, automation: true).IsEnabled,
                    "could not return to the original shortcut list");
                quick.UpdateLayout();
            }

            void Touch(Button button)
            {
                var center = ScreenCenter(button);
                Assert(WindowFromPoint(center) == rail && HasForeground(quick), "the shortcut control was obscured or outside the window");
                SendTouch(down, center);
                try { Pause(47); }
                finally { SendTouch(up, center); }
            }

            void ClickVisible(Button button, Func<bool> done, string failure)
            {
                var screen = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                ClickFixturePoint(rail, screen, rail, () => PumpUntil(done, failure));
            }
        }
        finally
        {
            if (externalWindow != 0) PostSlideKey(externalWindow, 0x10, 0, 0);
            quickField.SetValue(main, null);
            main.Close();
            quick.Shutdown();
            SynchronizationContext.SetSynchronizationContext(priorContext);
            File.Delete(catalogPath);
        }
    }

    private static Rect RelativeBounds(FrameworkElement element, Visual root) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
}
