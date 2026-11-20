using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Core;

internal static partial class Program
{
    private static void RunStudyHomeworkReaderChecks(bool manual)
    {
        var clock = new StudyUiClock();
        int hides = 0, ends = 0;
        var surface = new StudySurface(() => { hides++; return Task.CompletedTask; }, () => { }, true, clock,
            end: manual ? () => { ends++; return Task.CompletedTask; } : null);
        var owner = new Window { Content = surface, Width = 1920, Height = 1080, WindowStyle = WindowStyle.None };
        var state = StudyFixture(seconds: 50);
        var originalId = new string('a', 64);
        var updatedId = new string('b', 64);
        state = state with { Homework = new("READY", originalId, 1, "2026-10-12", state.Version) };
        if (manual)
            state = state with
            {
                Execution = state.Execution! with { Owner = "Manual" },
                Manual = new(Guid.NewGuid(), 1, state.Execution!.SessionId, "Active", null, null, null, true, 600),
                ClockSource = "Windows"
            };
        StudyHomeworkItem Work(string id, string group, string body, string title = "") =>
            new(id, 1, "物理", title, body, [new(id + "-source", group)]);
        string longText = string.Concat(Enumerable.Range(1, 26).Select(i => $"第 {i} 步：记录实验现象，写出判断依据。\n"));
        StudyHomeworkItem[] work =
        [
            Work("lab", "行政一班", longText, "实验记录"),
            Work("a", "物理走班 A", "完成 42 页练习，写出受力分析。"),
            Work("b", "物理走班 B", "旧版作业。"),
            Work("empty", "空白来源", " ")
        ];
        var original = new StudyHomeworkSnapshot(state.Version!, "2026-10-12", work);
        var superseded = original with { Items = [Work("b", "物理走班 B", "中间版作业。") with { Revision = 2 }] };
        var updated = original with { Items = [Work("b", "物理走班 B", "新版作业。") with { Revision = 3 }] };
        string kind = manual ? "manual" : "school";
        T Element<T>(string name) where T : FrameworkElement => (T)surface.FindName(name);
        var body = Element<HomeworkReaderView>("ReaderBody");
        var overview = Element<Canvas>("StudyOverview");
        var reader = Element<Border>("HomeworkReader");
        var open = Element<Button>("OpenHomeworkReader");
        var back = Element<Button>("ReturnFromReader");
        var picker = Element<Grid>("HomeworkPicker");
        var choices = Element<ListBox>("HomeworkChoices");
        var pick = Element<Button>("PickHomework");
        var pager = (StudyHomeworkPager)typeof(StudySurface).GetField("_pager", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        StudyHomeworkPage[] Pages() =>
            (StudyHomeworkPage[])typeof(StudySurface).GetField("_pages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        void Apply() => surface.Apply(state, null);
        void Activate(UIElement control) => control.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        void Press(UIElement control, Key key) => control.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(control)!, Environment.TickCount, key) { RoutedEvent = UIElement.PreviewKeyDownEvent });
        void Ready()
        {
            try
            {
                owner.UpdateLayout();
                PumpUntil(() => !body.IsLayingOut && body.PageCount > 0, "reader did not finish WPF layout", 12);
            }
            catch (InvalidOperationException error)
            {
                string message = string.Join(" ", body.Children.OfType<TextBlock>().Select(label => label.Text));
                throw new InvalidOperationException(
                    $"Reader: visible={reader.IsVisible}, size={body.RenderSize}, pending={body.IsLayingOut}, pages={body.PageCount}, position={body.Position}, message={message}", error);
            }
        }
        void Target(FrameworkElement control) => AssertStudyReaderTarget(surface, control);
        void Blocked(bool escapeClosesSeating = false)
        {
            var input = Element<Grid>("ReadingInput");
            Assert(!input.IsEnabled && !input.IsHitTestVisible && Element<Border>("ReaderInputShield").IsVisible,
                "covered reading controls still accept input");
            var location = back.TranslatePoint(new Point(back.ActualWidth / 2, back.ActualHeight / 2), surface);
            Assert(surface.InputHitTest(location) is DependencyObject hit && !input.IsAncestorOf(hit),
                "pointer hit a covered reading control");
            Activate(back);
            Press(surface, Key.Escape);
            Assert(reader.IsVisible, "covered reader accepted Return or Escape");
            if (escapeClosesSeating)
                Assert(!Element<Border>("SeatingOverlay").IsVisible, "Escape did not close only the visible seat map");
        }

        try
        {
            owner.Show();
            var window = owner;
            PumpUntil(() => window.IsActive, "reader fixture did not activate");
            Apply();
            Assert(!open.IsEnabled, "reader opened before an eligible snapshot loaded");
            surface.ApplyHomework(originalId, original);
            Activate(Element<Button>("Next"));
            int openedPage = pager.Page;
            var first = Pages()[openedPage].Items[0];
            Assert(first.StartOffset > 0, "fixture did not reach an overview continuation");
            Snapshot(window, $"study-reader-{kind}-overview.png");

            Activate(open);
            Ready();
            Assert(reader.IsVisible && !overview.IsEnabled && back.IsKeyboardFocused &&
                body.Position == new HomeworkReadingPosition(first.Item.Id, first.StartOffset),
                "reader did not open at the overview homework and focus Return");
            Assert(Element<RadioButton>("ReaderFit").IsChecked == true &&
                Math.Abs(body.ActualWidth - 1776) < 1 && Math.Abs(body.ActualHeight - 714) < 1,
                "default Fit or reference content dimensions changed");
            Snapshot(window, $"study-reader-{kind}-fit.png");

            if (!manual)
            {
                state = state with { SchoolNow = state.Window!.Start };
                Apply();
                Assert(Element<Border>("StageNotice").IsVisible, "fixture did not show the school start notice");
                Blocked();
                clock.Ms += 6000;
                state = state with { SchoolNow = state.Window!.Start.AddSeconds(50) };
                Apply();
                Assert(back.IsKeyboardFocused && body.Position?.ItemId == "lab", "stage cover did not restore the reader focus and item");
                var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 255, 255, 255, 255 }, 4);
                surface.ApplySeating("cover", 1, image);
                Assert(Element<Border>("SeatingOverlay").IsVisible, "automatic seating did not cover the reader");
                Blocked(escapeClosesSeating: true);
                Assert(back.IsKeyboardFocused && body.Position?.ItemId == "lab", "seating did not restore reader focus and item");
            }

            var fitPosition = body.Position;
            var fitMode = Element<RadioButton>("ReaderFit");
            fitMode.Focus();
            Press(fitMode, Key.Right);
            Ready();
            Assert(Element<RadioButton>("ReaderEachItem").IsChecked == true && body.PageCount == 3 &&
                body.Position is { ItemId: "lab", Offset: > 0 } &&
                fitPosition is { ItemId: "lab" }, "keyboard mode switch lost the continuation or included an empty assignment");
            Snapshot(window, $"study-reader-{kind}-each.png");
            body.Focus();
            int beforeScroll = body.Position!.Value.Offset;
            Press(body, Key.PageDown);
            PumpUntil(() => body.Position is { ItemId: "lab" } moved && moved.Offset > beforeScroll,
                "Page Down did not scroll the long assignment in place");
            Press(body, Key.Right);
            Assert(body.Position?.ItemId == "a", "Right in the reading area did not advance one assignment");

            Activate(pick);
            Assert(picker.IsVisible && !Element<Grid>("ReadingControls").IsEnabled && choices.Items.Count == 3,
                "picker did not isolate its input or filtered assignment list");
            if (!manual) Snapshot(window, "study-reader-school-picker.png");
            Press(choices, Key.Escape);
            Assert(reader.IsVisible && !picker.IsVisible && pick.IsKeyboardFocused, "first Escape did not close only the picker");
            Activate(pick);
            choices.SelectedIndex = 2;
            choices.Focus();
            Press(choices, Key.Enter);
            Assert(!picker.IsVisible && body.Position?.ItemId == "b", "keyboard picker failed to distinguish the second same-subject assignment");

            Activate(Element<RadioButton>("ReaderOnePage"));
            Ready();
            Assert(body.PageCount == 1 && body.Position?.ItemId == "b" && !Element<FrameworkElement>("ReaderPageNavigation").IsVisible,
                "One Page lost the selected item or exposed pagination");
            if (!manual) Snapshot(window, "study-reader-school-one.png");

            clock.Ms += 61000;
            state = state with { Homework = state.Homework! with { SnapshotId = updatedId } };
            Apply();
            surface.ApplyHomework(updatedId, superseded);
            var newer = Element<Button>("ReaderUpdate");
            Assert(newer.IsVisible && pager.Page == openedPage, "copying did not pause rotation or announce a new snapshot");
            var supersedingId = new string('c', 64);
            state = state with { Homework = state.Homework! with { SnapshotId = supersedingId } };
            Apply();
            surface.ApplyHomework(updatedId, superseded);
            Assert(!newer.IsVisible && !Element<TextBlock>("WorkUpdateLabel").Text.Contains("作业已更新"),
                "the reader offered a superseded revision while the current assignment was downloading");
            Activate(newer);
            Activate(pick);
            Assert(choices.Items.Count == 3, "an obsolete update replaced the student's pinned assignments");
            Press(choices, Key.Escape);
            surface.ApplyHomework(supersedingId, updated);
            Assert(newer.IsVisible, "the latest verified assignment did not restore the update action");
            Activate(newer);
            Ready();
            Assert(body.Position?.ItemId == "b" && !newer.IsVisible && Pages().SelectMany(p => p.Items).Any(p => p.Item.Id == "lab"),
                "adopting the update in the reader also replaced the overview");
            surface.Apply(state, null, TimeSpan.FromSeconds(2));
            Assert(reader.IsVisible && Element<TextBlock>("ReaderStatus").Text.Contains("最近作业"),
                "short loss of transport removed still-eligible cached work or failed to label it");
            Apply();
            Press(back, Key.Escape);
            Assert(!reader.IsVisible && overview.IsEnabled && open.IsKeyboardFocused && pager.Page == openedPage,
                "Escape did not restore the original overview page and entry focus");
            clock.Ms += 59000;
            Apply();
            Assert(pager.Page == openedPage && Pages().Length > 1, "overview applied pending work before the new interval elapsed");
            clock.Ms += 1000;
            Apply();
            Assert(Pages() is [{ Items: [{ Text: "新版作业。" }] }], "overview did not adopt the pending snapshot at the boundary");

            Activate(open);
            Ready();
            Assert(Element<RadioButton>("ReaderOnePage").IsChecked == true, "reopening did not retain the session's chosen layout");
            Activate(pick);
            state = state with { Homework = manual ? null : state.Homework! with { State = "DISABLED", SnapshotId = null } };
            Apply();
            surface.ApplyHomework(supersedingId, updated);
            Assert(!reader.IsVisible && !open.IsEnabled && choices.Items.Count == 0 && body.PageCount == 0 &&
                Pages() is [{ Items.Count: 0 }], "scope revocation or a late read left stale homework in one of the views");
            Assert(hides == 0 && ends == 0, "reader interaction invoked a Host exit");

            window.Width = 1280d / 1.5;
            window.Height = 720d / 1.5;
            state = state with { Homework = new("READY", originalId, 1, "2026-10-12", state.Version) };
            Apply();
            surface.ApplyHomework(originalId, original);
            window.UpdateLayout();
            Target(open);
            Target(Element<Button>("Previous"));
            var hold = Element<Button>(manual ? "EndButton" : "HoldButton");
            Target(hold);
            Press(hold, Key.Space);
            clock.Ms += 1000;
            Apply();
            var progress = Element<Rectangle>(manual ? "EndHoldProgress" : "TemporaryHoldProgress");
            PumpUntil(() => progress.IsVisible && progress.Width > 0, "two-second hold did not produce visible button progress");
            Snapshot(window, $"study-reader-{kind}-compact-hold.png");
            surface.CancelHold();
            Assert(!progress.IsVisible && hides == 0 && ends == 0, "cancelling the hold kept progress or triggered exit");
            Activate(open);
            Ready();
            window.UpdateLayout();
            Target(back);
            Target(pick);
            Target(Element<RadioButton>("ReaderFit"));
            Snapshot(window, $"study-reader-{kind}-compact.png");
            surface.Suspend();
            Assert(!reader.IsVisible && body.PageCount == 0, "whole-window suspend retained an interactive reading layer");
            Checks.Add($"Study reader ({kind}): outline continuation, three layouts, same-subject selection, two snapshot positions, input shield, revocation, 44-DIP compact targets and hold progress");
            if (!manual) CaptureClassroomHomeworkScreens(window, surface, state);
            window.Close();
        }
        finally
        {
            surface.Stop();
            owner.Close();
        }
    }

    private static void AssertStudyReaderTarget(StudySurface surface, FrameworkElement control)
    {
        var bounds = control.TransformToAncestor(surface).TransformBounds(new Rect(control.RenderSize));
        Assert(bounds.Width >= 43.995 && bounds.Height >= 43.995, $"compact touch target {control.Name} is {bounds.Size}");
        var edge = control.TranslatePoint(new Point(control.ActualWidth / 2, 4), surface);
        Assert(surface.InputHitTest(edge) is DependencyObject hit &&
            (ReferenceEquals(hit, control) || control.IsAncestorOf(hit)), $"compact touch target {control.Name} was clipped");
    }

    private static void CaptureClassroomHomeworkScreens(Window window, StudySurface surface, StudyHostState state)
    {
        // Arrange the exact logical viewport even when the test monitor would cap the HWND size.
        window.Content = null;
        window.Content = new Canvas { Children = { surface } };
        var formClass = new StudyHomeworkSource("form-class", "高二（3）班");
        var physicsClass = new StudyHomeworkSource("physics-class", "物理走班 A");
        var chemistryClass = new StudyHomeworkSource("chemistry-class", "化学走班 B");
        StudyHomeworkItem Work(string id, string subject, string title, string text, StudyHomeworkSource source) =>
            new(id, 1, subject, title, text, [source]);
        StudyHomeworkItem[] work =
        [
            Work("chinese", "语文", "《荷塘月色》批注", "标出两处描写光影的句子，各写一句批注。选一处说明景物描写怎样表现作者的心情。", formClass),
            Work("maths", "数学", "椭圆与双曲线", "完成练习册第 42 页第 3、5、7 题。第 7 题写清设点、联立和检验的步骤。", formClass),
            Work("english", "英语", "阅读理解订正", "订正阅读 B 篇第 25—28 题，在原文画出依据。整理两处误判，并注明干扰项改动了什么。", formClass),
            Work("physics", "物理", "小车运动实验记录", """
                1. 标出纸带上的计数点，注明相邻点的时间间隔。
                2. 比较前后两段的点距变化，写出判断依据。
                3. 画出小车的速度—时间图像，注明坐标轴单位。
                4. 用受力分析解释小车加速的原因。
                5. 附上原始记录，并写一条下次可做的改进。
                """.ReplaceLineEndings("\n"), physicsClass),
            Work("chemistry", "化学", "酸碱滴定错题", "订正学案第 6 题。画出滴定管读数示意图，写出终点判断，并说明一次俯视读数会怎样影响结果。", chemistryClass),
            Work("biology", "生物", "光合作用速率", "根据教材图中两条曲线，标明光补偿点和光饱和点。用一句话解释两条曲线起点为何不同。", formClass)
        ];
        var snapshotId = new string('d', 64);
        state = state with
        {
            SchoolNow = state.Window!.Start.AddMinutes(42).AddSeconds(18),
            Display = state.Display! with { InstanceId = Guid.NewGuid() },
            Homework = new("READY", snapshotId, 1, "2026-10-12", state.Version)
        };
        T Element<T>(string name) where T : FrameworkElement => (T)surface.FindName(name);
        void ClickControl(UIElement control) =>
            control.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        void SizeTo(int width, int height, double scale)
        {
            window.Width = surface.Width = width / scale;
            window.Height = surface.Height = height / scale;
            window.UpdateLayout();
            Assert(Math.Abs(surface.ActualWidth - width / scale) < .01 && Math.Abs(surface.ActualHeight - height / scale) < .01,
                "equivalent-DIP viewport was not arranged at the requested size");
        }
        var body = Element<HomeworkReaderView>("ReaderBody");
        void Ready()
        {
            window.UpdateLayout();
            PumpUntil(() => !body.IsLayingOut && body.PageCount > 0, "classroom screenshot layout did not finish", 12);
        }

        SizeTo(1920, 1080, 1);
        surface.Apply(state, null);
        surface.ApplyHomework(snapshotId, new(state.Version!, "2026-10-12", work));
        var open = Element<Button>("OpenHomeworkReader");
        window.UpdateLayout();
        AssertStudyReaderTarget(surface, open);
        SnapshotStudyReaderViewport(surface, 1920, 1080, 1, "classroom-1920x1080-100-overview.png");
        ClickControl(open);
        Ready();
        SnapshotStudyReaderViewport(surface, 1920, 1080, 1, "classroom-1920x1080-100-fit.png");
        ClickControl(Element<RadioButton>("ReaderOnePage"));
        Ready();
        SnapshotStudyReaderViewport(surface, 1920, 1080, 1, "classroom-1920x1080-100-one.png");
        ClickControl(Element<RadioButton>("ReaderEachItem"));
        Ready();
        body.SelectItem("physics");
        SnapshotStudyReaderViewport(surface, 1920, 1080, 1, "classroom-1920x1080-100-each.png");

        // The classroom runs its 2560×1440 panel at 200%. Keep the same assignment
        // selected so the three captures can be compared at its actual pixel size.
        SizeTo(2560, 1440, 2);
        foreach (var (control, label) in new[] { ("ReaderOnePage", "one"), ("ReaderFit", "fit"), ("ReaderEachItem", "each") })
        {
            var mode = Element<RadioButton>(control);
            ClickControl(mode);
            Ready();
            body.SelectItem("physics");
            window.UpdateLayout();
            Assert(mode.IsChecked == true && body.Position?.ItemId == "physics",
                $"classroom 200% reader did not show the selected physics assignment in {label}");
            if (control == "ReaderOnePage") Assert(body.PageCount == 1, "classroom one-page mode paginated");
            if (control == "ReaderEachItem") Assert(body.PageCount == work.Length, "classroom per-item mode lost an assignment");
            foreach (string action in new[] { "ReaderOnePage", "ReaderFit", "ReaderEachItem", "ReturnFromReader", "PickHomework" })
                AssertStudyReaderTarget(surface, Element<FrameworkElement>(action));
            SnapshotStudyReaderViewport(surface, 2560, 1440, 2, $"classroom-2560x1440-200-{label}.png");
        }
        Checks.Add("Classroom 2560×1440 at 200%: three layouts, selected physics assignment, reachable layout controls and return.");

        foreach (var (width, height, captureScale) in new[] { (1920, 1080, 1.0), (1366, 768, 1.25), (1280, 720, 1.5) })
            foreach (double scale in new[] { 1.0, 1.25, 1.5 })
            {
                SizeTo(width, height, scale);
                foreach (string name in new[] { "ReturnFromReader", "PickHomework", "ReaderNext" })
                    AssertStudyReaderTarget(surface, Element<Button>(name));
                foreach (string name in new[] { "ReaderOnePage", "ReaderFit", "ReaderEachItem" })
                    AssertStudyReaderTarget(surface, Element<RadioButton>(name));
                if (scale == captureScale && width != 1920)
                    SnapshotStudyReaderViewport(surface, width, height, scale, $"classroom-{width}x{height}-{scale * 100:0}-each.png");
            }
        Checks.Add($"Classroom reading controls: nine equivalent-DIP combinations of 1920×1080, 1366×768, 1280×720 and 100/125/150%; actual fixture monitor DPI {VisualTreeHelper.GetDpi(surface).PixelsPerDip:P0}");
        surface.Suspend();
    }

    private static void SnapshotStudyReaderViewport(StudySurface surface, int width, int height, double scale, string file)
    {
        surface.UpdateLayout();
        var drawing = new DrawingVisual();
        var viewport = new Rect(0, 0, width / scale, height / scale);
        using (var dc = drawing.RenderOpen())
            dc.DrawRectangle(new VisualBrush(surface) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = viewport }, null, viewport);
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(System.IO.Path.Combine(_output, file));
        encoder.Save(stream);
    }
}
