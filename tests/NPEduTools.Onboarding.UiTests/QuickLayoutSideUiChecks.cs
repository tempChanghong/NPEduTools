using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunQuickLayoutSideChecks(nint slide, Func<int> slideNumber)
    {
        const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
        const uint down = 0x10006, move = 0x20006, up = 0x40000;
        string pipe = "NPEduTools.Test.quick-layout-side." + Guid.NewGuid().ToString("N");
        QuickAccessWindow MakeRail() => new(pipe, () => { }, () => { }, () => { }, () => { }, _ => { }, () => { }, () => { });
        var quick = MakeRail();
        string path = (string)typeof(QuickAccessWindow).GetField("_settingsPath", fields)!.GetValue(quick)!;
        var area = System.Windows.Forms.Screen.FromHandle(slide);
        string display = area.DeviceName;
        const double initialHeight = .69;
        nint hwnd = 0;
        try
        {
            quick.Shutdown();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new { LeftSide = false, RelativeY = initialHeight, Display = display, Compact = true }));
            quick = MakeRail();
            quick.SetDensity(.9);
            quick.Update(new TouchAssistState(true, false, false, "启用"), "辅助已开启", true, "就绪", true);
            quick.SetShortcuts(Enumerable.Range(1, 24)
                .Select(index => new ShortcutEntry(Guid.NewGuid(), $"课件 {index}", "file", Path.Combine(_output, $"{index}.pptx"))).ToArray());
            quick.ShowEntry();
            hwnd = new WindowInteropHelper(quick).Handle;
            Assert(InitializeTouchInjection(1, 3), "native touch injection unavailable: " + Marshal.GetLastWin32Error());
            OpenFromSlide();
            Click(quick, "LayoutButton");
            PumpUntil(() => Button(quick, "LayoutRight").IsVisible, "the same panel did not display layout options");
            Snapshot(quick, "01-layout-wide.png");
            Click(quick, "LayoutBack");

            quick.SetDensity(.8);
            CapHeight();
            Assert(ReadQuickTestRect(hwnd, out var shortBounds), "the short layout has no native rect");
            Checks.Add($"Short layout {shortBounds.Right - shortBounds.Left}×{shortBounds.Bottom - shortBounds.Top} px, {VisualTreeHelper.GetDpi(quick).PixelsPerInchX} DPI.");
            var ppt = Button(quick, "PauseButton");
            var pptPosition = ppt.PointToScreen(new Point());
            Click(quick, "LayoutButton");
            var left = Button(quick, "LayoutLeft");
            var right = Button(quick, "LayoutRight");
            var back = Button(quick, "LayoutBack");
            var layout = Control<FrameworkElement>("LayoutOptions");
            var panel = Control<FrameworkElement>("PanelSurface");
            quick.UpdateLayout();
            Assert(AutomationProperties.GetItemStatus(right) == "当前侧" &&
                layout.IsVisible && !ppt.IsVisible && new WindowInteropHelper(Window.GetWindow(left)).Handle == hwnd,
                "the subview lost the current-side indication or opened another window");
            var layoutBounds = RelativeBounds(layout, quick);
            foreach (var button in new[] { back, left, right, Button(quick, "LayoutShape"), Button(quick, "LayoutMove") })
            {
                var bounds = RelativeBounds(button, quick);
                Assert(button.IsVisible && bounds.Width >= 43.5 && bounds.Height >= 43.5 && layoutBounds.Contains(bounds),
                    "the short layout's touch target is clipped or undersized: " + button.Name);
            }
            Snapshot(quick, "02-layout-short.png");
            Tap(back);
            PumpUntil(() => !layout.IsVisible && ppt.IsVisible, "touch return did not restore the classroom tab");
            Assert(ppt.PointToScreen(new Point()) == pptPosition && Button(quick, "LayoutButton").IsKeyboardFocused,
                "return moved the fixed PPT control or lost its navigation focus");

            Click(quick, "ShortcutsTab");
            var shortcuts = Control<ScrollViewer>("ShortcutsPage");
            shortcuts.ScrollToVerticalOffset(300);
            PumpUntil(() => shortcuts.VerticalOffset > 100, "the shortcuts fixture did not scroll");
            double offset = shortcuts.VerticalOffset;
            Click(quick, "LayoutButton");
            Tap(back);
            PumpUntil(() => shortcuts.IsVisible, "touch return did not restore the shortcuts tab");
            Assert(shortcuts.VerticalOffset == offset && !ppt.IsVisible, "return replaced the previous scroll position or tab");
            Checks.Add("The layout lives in one HWND. First-screen controls are at least 44 effective DIP. Touch Return restored both the fixed PPT position and the prior shortcut offset.");

            Click(quick, "LayoutButton");
            string before = File.ReadAllText(path);
            DateTime lastWrite = File.GetLastWriteTimeUtc(path);
            var contact = Hold(left);
            try
            {
                var outside = ScreenCenter(right);
                SendTouch(move, outside);
                contact = outside;
                Pause(28);
                var inside = ScreenCenter(left);
                SendTouch(move, inside);
                contact = inside;
                Pause(28);
            }
            finally { SendTouch(up, contact); }
            Pause(90);
            Assert(!quick.LeftSide && layout.IsVisible && File.ReadAllText(path) == before && File.GetLastWriteTimeUtc(path) == lastWrite,
                "a finger that left the left button changed placement, even after returning");
            contact = Hold(left);
            try { left.ReleaseAllTouchCaptures(); Pause(30); }
            finally { SendTouch(up, contact); }
            Pause(90);
            Tap(right);
            Assert(!quick.LeftSide && layout.IsVisible && File.ReadAllText(path) == before && File.GetLastWriteTimeUtc(path) == lastWrite,
                "capture loss or a tap on the current side wrote placement or closed the layout");
            Checks.Add("Native slide-out/re-entry, lost capture and the current-side tap made no placement write.");

            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Tap(left);
                PumpUntil(() => Control<TextBlock>("LayoutSaveError").IsVisible, "a locked file did not explain the save error");
                Assert(!quick.LeftSide && layout.IsVisible && File.ReadAllText(path) == before, "failed save moved the rail or changed the original");
            }
            Tap(left);
            PumpUntil(() => quick.LeftSide && !panel.IsVisible && QuickForeground() == slide, "tap did not snap left, collapse and restore PowerPoint");
            var turned = ReadPlacement();
            Assert(turned.Left && turned.Compact && turned.Display == display && turned.Height == initialHeight && slideNumber() == 1,
                "tap changed the monitor, height, shape or original slide");
            Assert(PostSlideKey(slide, 0x100, 0x27, 0x014D0001) && PostSlideKey(slide, 0x101, 0x27, unchecked((nint)0xC14D0001)),
                "could not send the verified original slideshow a Right key");
            PumpUntil(() => slideNumber() == 2 && QuickForeground() == slide, "the restored PowerPoint did not advance");
            OpenFromSlide();
            Click(quick, "LayoutButton");
            CapHeight();
            Assert(AutomationProperties.GetItemStatus(left) == "当前侧", "reopening did not mark the saved side");
            Snapshot(quick, "03-left-after-reopen.png");
            Checks.Add("A write failure left the old rail in place; a fresh touch rolled the rail left, kept display/height/compact shape, and returned focus to the real PowerPoint for the next slide.");

            Click(quick, "LayoutMove");
            var edge = Button(quick, "EdgeHandle");
            var hint = Control<FrameworkElement>("MoveHint");
            PumpUntil(() => hint.IsVisible, "the original fine drag no longer opened");
            before = File.ReadAllText(path);
            contact = Hold(edge);
            try
            {
                var destination = new CursorPoint { X = contact.X, Y = contact.Y - 54 };
                SendTouch(move, destination);
                contact = destination;
                Pause(34);
                Assert(File.ReadAllText(path) == before && hint.IsVisible, "fine drag wrote before release");
            }
            finally { SendTouch(up, contact); }
            PumpUntil(() => !hint.IsVisible && QuickForeground() == slide && File.ReadAllText(path) != before,
                "fine drag did not persist on release and return focus");
            var moved = ReadPlacement();
            Assert(moved.Left && moved.Compact && moved.Display == display && moved.Height < turned.Height,
                "fine drag stopped retaining side, screen or shape");
            OpenFromSlide();
            Click(quick, "LayoutButton");
            Click(quick, "LayoutMove");
            PumpUntil(() => hint.IsVisible, "could not reopen fine drag");
            before = File.ReadAllText(path);
            Tap(edge);
            PumpUntil(() => !hint.IsVisible && QuickForeground() == slide, "tap no longer cancels the original drag mode");
            Assert(File.ReadAllText(path) == before, "cancelled fine drag wrote placement");

            var savedPoint = edge.PointToScreen(new Point());
            quick.Shutdown();
            quick = MakeRail();
            quick.SetDensity(.8);
            quick.ShowEntry();
            hwnd = new WindowInteropHelper(quick).Handle;
            quick.UpdateLayout();
            var reopenedEdge = Button(quick, "EdgeHandle");
            Assert(QuickForeground() == slide && quick.LeftSide && !Button(quick, "RailTools").IsVisible &&
                (reopenedEdge.PointToScreen(new Point()) - savedPoint).Length <= 2 && ReadPlacement() == moved,
                "a fresh window did not read the already saved side, height and shape");
            Pause(170);
            Tap(reopenedEdge);
            PumpUntil(() => HasForeground(quick) && Control<FrameworkElement>("PanelSurface").IsVisible,
                "native touch on the recreated left handle did not reopen the saved layout");
            Click(quick, "LayoutButton");
            quick.UpdateLayout();
            Tap(Button(quick, "LayoutRight"));
            PumpUntil(() => !quick.LeftSide && !Control<FrameworkElement>("PanelSurface").IsVisible && QuickForeground() == slide,
                "a native tap did not switch the recreated rail back to the right");
            var switchedBack = ReadPlacement();
            Assert(!switchedBack.Left && switchedBack.Height == moved.Height && switchedBack.Display == display && switchedBack.Compact,
                "returning to the right discarded the fine-tuned height, display or shape");
            Checks.Add("Native fine drag still saves on release and cancels by tap. A recreated rail reopens at its saved coordinates and switches back right without losing its tuned height.");

            T Control<T>(string name) where T : FrameworkElement => (T)quick.FindName(name);
            void OpenFromSlide()
            {
                quick.UpdateLayout();
                ClickFixturePoint(hwnd, quick.PointToScreen(QuickHandleCenter(quick)), slide,
                    () => PumpUntil(() => HasForeground(quick) && Control<FrameworkElement>("PanelSurface").IsVisible, "the physical handle did not open over PowerPoint"));
                Pause(170);
            }

            (bool Left, double Height, string? Display, bool Compact) ReadPlacement()
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                return (root.GetProperty("LeftSide").GetBoolean(), root.GetProperty("RelativeY").GetDouble(),
                    root.GetProperty("Display").GetString(), root.GetProperty("Compact").GetBoolean());
            }

            void CapHeight()
            {
                Pause(170);
                Assert(ReadQuickTestRect(hwnd, out var bounds), "the rail has no native bounds");
                int height = Math.Min(600, area.WorkingArea.Height);
                int y = Math.Clamp(bounds.Top, area.WorkingArea.Top, area.WorkingArea.Bottom - height);
                Assert(ResizeQuickTestWindow(hwnd, 0, bounds.Left, y, bounds.Right - bounds.Left, height, 0x14), "could not set the short panel bounds");
                PumpUntil(() => ReadQuickTestRect(hwnd, out var sized) && sized.Bottom - sized.Top == height &&
                    Math.Abs(quick.ActualHeight * VisualTreeHelper.GetDpi(quick).DpiScaleY - height) <= 1,
                    "the WPF layout did not settle at the native test height");
            }

            CursorPoint Hold(Button button)
            {
                var point = CheckHit(button);
                SendTouch(down, point);
                try
                {
                    for (int count = 0; count < 8 && !button.TouchesCaptured.Any(); count++)
                    {
                        Pause(24);
                        var nextPoint = new CursorPoint { X = point.X + (count % 2 == 0 ? 5 : -5), Y = point.Y };
                        SendTouch(move, nextPoint);
                        point = nextPoint;
                    }
                    PumpUntil(() => button.TouchesCaptured.Any(), "native touch did not capture " + button.Name);
                    return point;
                }
                catch { SendTouch(up, point); throw; }
            }

            void Tap(Button button)
            {
                var point = CheckHit(button);
                SendTouch(down, point);
                try { Pause(48); }
                finally { SendTouch(up, point); }
                Pause(95);
            }

            CursorPoint CheckHit(Button button)
            {
                var point = ScreenCenter(button);
                Assert(button.IsVisible && WindowFromPoint(point) == hwnd, "native touch would leave the isolated rail: " + button.Name);
                return point;
            }
        }
        finally
        {
            quick.Shutdown();
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }
}
