using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Core;

internal static partial class Program
{
    private static readonly Size ReadingArea = new(1776, 714);
    private const string ReadingFamily = "MiSans, Microsoft YaHei UI";
    private static void RunHomeworkReadingLayoutChecks()
    {
        var group = new StudyHomeworkSource("demo", "高二（3）班");
        var lab = new StudyHomeworkSource("lab", "物理走班 A");
        StudyHomeworkItem Entry(string id, string subject, string body) => new(id, 1, subject, "", body, [group]);
        StudyHomeworkItem[] shortItems =
        [
            Entry("cn", "语文", "摘录两处描写荷塘月色的句子，写下自己的批注。"),
            Entry("math", "数学", "完成练习册第 42 页第 3、5、7 题，订正后写出关键步骤。"),
            Entry("en", "英语", "订正阅读理解，标出支持答案的原句。"),
            Entry("phy", "物理", "画出小车的受力图，标出各力的方向。")
        ];
        var longItem = Entry("lab", "物理", """
            1. 在记录纸上标出各计数点，写明相邻计数点的时间间隔。
            2. 比较前后两段位移，说明小车的速度变化及判断依据。
            3. 用坐标纸画出速度随时间变化的图像，标明横纵轴的量、单位和刻度。
            4. 结合图像和受力图解释小车加速的原因，写出你用到的条件。
            5. 和同桌交换记录，找出一个可能的误差来源，并写下一条可以实施的改进。
            6. 将原始记录和图像贴在实验报告后，保留修改痕迹。
            复核记录
            选一段点距差异最大的纸带，标出比较的两个区间。写清楚每个区间包含几个时间间隔，再用带单位的算式算出平均速度。
            将你的结果与同桌对照。如果两人的结论不同，在图像上圈出导致差异的那个数据点，解释该点应当保留还是重新测量。
            最后在报告末尾写两句话，分别说明哪条结论得到了数据支持，以及还需要补做哪一次测量。
            """.ReplaceLineEndings("\n")) with { Title = "实验记录与分析", Sources = [group, lab] };
        StudyHomeworkSnapshot Snapshot(params StudyHomeworkItem[] items) => new(new string('a', 64), "2026-11-19", items);
        ReadingPlan Layout(StudyHomeworkSnapshot snapshot, HomeworkReadingLayout mode, double dpi = 1)
        {
            using var planner = new HomeworkReadingPlanner(snapshot, ReadingArea, ReadingFamily, dpi, default);
            var plan = planner.Build(mode);
            CheckReadingCoverage(snapshot, plan, mode);
            return plan;
        }

        var empty = Snapshot(Entry("empty", "空白", " \r\n"));
        var fourShort = Snapshot(shortItems);
        var fourLong = Snapshot(Enumerable.Range(0, 4).Select(i => longItem with { Id = "long" + i }).ToArray());
        var unicode = Entry("unicode", "物理", string.Concat(Enumerable.Repeat("第 2 组 Δv = 2.4 m/s。\r\nA\u0301，👩‍🏫 与𠮷。\r\n\r\n", 35)));
        var mixed = Snapshot([.. shortItems, Entry("geo", "地理", "画图"), longItem,
            Entry("other-class", "物理", "标注各力的方向。") with { Sources = [lab] }, unicode]);
        foreach (var mode in Enum.GetValues<HomeworkReadingLayout>())
        {
            Assert(Layout(empty, mode).Pages.Count == 0, "empty homework generated pages");
            var shortPlan = Layout(fourShort, mode);
            var longPlan = Layout(fourLong, mode);
            var mixedPlan = Layout(mixed, mode, dpi: 1.5);
            if (mode == HomeworkReadingLayout.Fit)
            {
                Assert(shortPlan.Pages.Count == 1 && longPlan.Pages.Count == 8,
                    $"four short/long assignments were measured as {shortPlan.Pages.Count}/{longPlan.Pages.Count} pages");
                var second = longPlan.Pages[1].Cells[0].Card.Slice;
                Assert(second.Start > 0 && longPlan.Locate(new(second.Work.Item.Id, second.Start)) == 1, "continuation offset on a page boundary");
            }
            Checks.Add($"{mode} small/long/mixed: {shortPlan.Pages.Count}/{longPlan.Pages.Count}/{mixedPlan.Pages.Count} pages");
        }
        var veryDense = Snapshot(Enumerable.Range(0, 11).Select(i => longItem with
            { Id = "dense" + i, Content = string.Concat(Enumerable.Repeat(longItem.Content, 3)) }).ToArray());
        var denseSheet = Layout(veryDense, HomeworkReadingLayout.OnePage).Pages.Single();
        Assert(denseSheet.Font * denseSheet.Scale < 36, "dense page did not reach readability hint threshold");
        Checks.Add($"dense one-page equivalent body size: {denseSheet.Font * denseSheet.Scale:F1}");

        var initial = Snapshot(shortItems[0], unicode, shortItems[2]);
        int offset = unicode.Content.IndexOf("𠮷", StringComparison.Ordinal) + 3;
        var edited = unicode with { Content = "新增说明。\r\n" + unicode.Content };
        var afterEdit = HomeworkReadingPositionResolver.Resolve(initial, Snapshot(shortItems[0], edited), new(unicode.Id, offset));
        Assert(afterEdit == new HomeworkReadingPosition(unicode.Id, offset + edited.Content.Length - unicode.Content.Length), "offset after inserted text");
        Assert(HomeworkReadingPositionResolver.Resolve(initial, fourShort, new(unicode.Id, offset))?.ItemId == shortItems[2].Id,
            "deleted item did not select a surviving neighbour");
        var overviewParts = StudyHomeworkPages.Build(initial.Items, (text, _) => text.Length * 3D)
            .SelectMany(p => p.Items).Where(p => p.Item.Id == unicode.Id).ToArray();
        Assert(overviewParts.Length > 1 && overviewParts.All(p => unicode.Content.AsSpan(p.StartOffset, p.Text.Length).SequenceEqual(p.Text)),
            "overview continuation is not indexed into original text");
        Checks.Add("full UTF-16 ranges, grapheme/CRLF boundaries, same-subject IDs, overview and edited/deleted anchors");
        CheckReadingScroll(initial, unicode);
        var glyphs = Snapshot(Entry("fonts", "物理", "速度 Δv = 2.4 m/s，A\u0301 👩‍🏫，𠮷。"));
        CheckReadingFontRuns(Layout(glyphs, HomeworkReadingLayout.Fit).Pages.Single());
        var inkSample = Snapshot(Entry("raster", "语文", string.Concat(Enumerable.Repeat("1. 阅读第一段，抄下描述天气的句子，写出自己的批注。", 680))));
        CheckReadingRaster(Layout(inkSample, HomeworkReadingLayout.OnePage).Pages.Single());
        var large = Snapshot(Entry("large", "自习", new string('a', 2_085_000)));
        CheckReadingCancellation(large, fourShort);
        CheckOverviewPreparation(large, fourShort);
    }

    private static void CheckReadingScroll(StudyHomeworkSnapshot snapshot, StudyHomeworkItem longItem)
    {
        var view = new HomeworkReaderView { Width = ReadingArea.Width, Height = ReadingArea.Height };
        view.Measure(ReadingArea); view.Arrange(new Rect(ReadingArea));
        int middle = longItem.Content.IndexOf("𠮷", longItem.Content.Length / 2, StringComparison.Ordinal);
        view.Show(snapshot, HomeworkReadingLayout.EachItem, new(longItem.Id, middle));
        PumpUntil(() => !view.IsLayingOut && view.PageCount > 0, "item failed to open");
        view.UpdateLayout();
        var page = (Grid)((Viewbox)view.Children[0]).Child;
        var viewer = page.Children.OfType<ScrollViewer>().Single();
        var scroll = (HomeworkReadingScroll)viewer.Content;
        Assert(scroll.ViewportHeight > 0 && scroll.VerticalOffset > 0 && scroll.ViewportHeight < scroll.ExtentHeight, "item did not scroll to the overview anchor");
        var original = view.Position;
        view.ScrollItem(1);
        var moved = view.Position;
        Assert(moved?.ItemId == longItem.Id && moved.Value.Offset > original!.Value.Offset, "PageDown did not move the original-text position");
        view.Show(snapshot, HomeworkReadingLayout.Fit);
        PumpUntil(() => !view.IsLayingOut && view.PageCount > 0, "fit did not restore the scrolled paragraph");
        Assert(view.Position == moved && view.PageIndex > 0, "switching modes returned to the first line");
        view.Clear();
        Checks.Add("native ScrollViewer restores a continuation, PageDown and mode switch retain its new original-text offset");
    }

    private static void CheckReadingRaster(ReadingSheet sheet)
    {
        Assert(sheet.Preview is { IsFrozen: true }, "dense single page was not prepared on the layout worker");
        byte[] Paint(ReadingSheet content)
        {
            var paper = new HomeworkReadingPaper(content, HomeworkReadingLayout.OnePage, ReadingFamily, 1, _ => { });
            paper.Measure(ReadingArea); paper.Arrange(new Rect(ReadingArea));
            int width = (int)ReadingArea.Width, height = (int)ReadingArea.Height;
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            image.Render(paper);
            var pixels = new byte[width * height * 4];
            image.CopyPixels(pixels, width * 4, 0);
            return pixels;
        }
        var expected = Paint(sheet with { Preview = null });
        var captured = Paint(sheet);
        for (int band = 0; band < 4; band++)
        {
            int expectedInk = 0, capturedInk = 0;
            for (int i = expected.Length * band / 4; i < expected.Length * (band + 1) / 4; i += 4)
            {
                if (expected[i + 1] >= 96) expectedInk++;
                if (captured[i + 1] >= 96) capturedInk++;
            }
            Assert(expectedInk > 0 && Math.Abs(capturedInk - expectedInk) <= expectedInk * .06,
                $"dense raster band {band} lost text: native={expectedInk}, cached={capturedInk}");
        }
        Checks.Add("dense single-page rendering matched native glyph coverage across four horizontal bands");
    }

    private static void CheckReadingFontRuns(ReadingSheet sheet)
    {
        var paper = new HomeworkReadingPaper(sheet, HomeworkReadingLayout.Fit, ReadingFamily, 1, _ => { });
        paper.Measure(ReadingArea); paper.Arrange(new Rect(ReadingArea));
        var image = new RenderTargetBitmap((int)ReadingArea.Width, (int)ReadingArea.Height, 96, 96, PixelFormats.Pbgra32);
        image.Render(paper);
        var faces = new SortedSet<string>(StringComparer.Ordinal);
        int missing = 0;
        void Read(Drawing drawing)
        {
            if (drawing is DrawingGroup group)
                foreach (var child in group.Children) Read(child);
            else if (drawing is GlyphRunDrawing { GlyphRun: { } run })
            {
                var names = run.GlyphTypeface.FamilyNames;
                faces.Add(names.TryGetValue(CultureInfo.GetCultureInfo("en-US"), out var name) ? name : names.Values.First());
                missing += run.GlyphIndices.Count(index => index == 0);
            }
        }
        Read(VisualTreeHelper.GetDrawing(paper));
        Assert(faces.Count > 0, "native text did not produce glyph runs");
        Checks.Add($"font runs on mixed CJK/Latin/emoji sample: {string.Join(", ", faces)}; missing glyph indices {missing}");
    }

    private static void CheckReadingCoverage(StudyHomeworkSnapshot snapshot, ReadingPlan plan, HomeworkReadingLayout mode)
    {
        var works = snapshot.Items.Where(StudyHomeworkPages.IsDisplayable).ToArray();
        var cells = plan.Pages.SelectMany(p => p.Cells).ToArray();
        Assert(cells.Select(c => c.Card.Slice.Work.Item.Id).Distinct().SequenceEqual(works.Select(w => w.Id)), "work order or coverage");
        if (mode == HomeworkReadingLayout.EachItem) Assert(plan.Pages.Count == works.Length && plan.Pages.All(p => p.Cells.Count == 1), "item page count");
        if (mode == HomeworkReadingLayout.OnePage) Assert(plan.Pages.Count == Math.Min(1, works.Length), "single-page count");
        foreach (var work in works)
        {
            var original = StudyHomeworkPages.ReadingText(work);
            var starts = StringInfo.ParseCombiningCharacters(original).ToHashSet();
            int position = 0;
            foreach (var cell in cells.Where(c => c.Card.Slice.Work.Item.Id == work.Id))
            {
                var card = cell.Card; var slice = card.Slice;
                Assert(starts.Contains(slice.Start) && slice.Start == position && card.Body.Text == original &&
                    card.Body.Starts[0] == slice.Start && card.Body.End == slice.End && slice.End > position, "lost, repeated or split text");
                Assert(card.Header.Subject.Text == work.Subject && card.Header.Sources.Text == string.Join(" · ", work.Sources.Select(s => s.Name)),
                    "header/source mismatch");
                position = slice.End;
            }
            Assert(position == original.Length, "incomplete item");
        }
        foreach (var page in plan.Pages)
        {
            Assert(mode != HomeworkReadingLayout.Fit || page.Font is 48 or 54 or 60, "fit font floor");
            foreach (var cell in page.Cells)
            {
                Assert(cell.Bounds.X >= 0 && cell.Bounds.Y >= 0 && cell.Bounds.Right * page.Scale <= ReadingArea.Width + .01 &&
                    cell.Bounds.Bottom * page.Scale <= ReadingArea.Height + .01, "card outside the reading area");
                Assert(mode == HomeworkReadingLayout.EachItem || cell.Card.Height <= cell.Bounds.Height + .01, "cropped card body");
            }
        }
    }

    private static void CheckReadingCancellation(StudyHomeworkSnapshot large, StudyHomeworkSnapshot small)
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(large, Protocol.Json);
        StudyHomeworkContract.Parse(bytes);
        var view = new HomeworkReaderView { Width = ReadingArea.Width, Height = ReadingArea.Height };
        view.Measure(ReadingArea); view.Arrange(new Rect(ReadingArea));
        var timer = Stopwatch.StartNew();
        view.Show(large, HomeworkReadingLayout.Fit);
        Assert(view.IsLayingOut, "heavy calculation must yield to dispatcher");
        var frame = new DispatcherFrame();
        var interrupt = new DispatcherTimer(TimeSpan.FromMilliseconds(45), DispatcherPriority.Normal, (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            view.Show(large, HomeworkReadingLayout.OnePage);
            view.Clear();
            view.Show(small, HomeworkReadingLayout.EachItem);
        }, Dispatcher.CurrentDispatcher);
        var check = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Background, (_, _) =>
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(15) || view.PageCount == 4 && !view.IsLayingOut) frame.Continue = false;
        }, Dispatcher.CurrentDispatcher);
        Dispatcher.PushFrame(frame);
        interrupt.Stop(); check.Stop();
        Assert(view.PageCount == 4 && view.Position?.ItemId == small.Items[0].Id && !view.IsLayingOut, "cancelled work replaced the active snapshot");
        view.Clear();
        Assert(view.Position is null && view.PageCount == 0 && view.Children.Count == 0, "revocation left visible text");
        Checks.Add($"cancel/switch/revoke for {bytes.Length:N0}-byte valid snapshot and replacement {timer.ElapsedMilliseconds} ms");
    }

    private static void CheckOverviewPreparation(StudyHomeworkSnapshot large, StudyHomeworkSnapshot small)
    {
        var surface = new StudySurface(() => Task.CompletedTask, () => { }, true, new StudyUiClock());
        var state = StudyFixture(seconds: 50);
        var open = (Button)surface.FindName("OpenHomeworkReader");
        var grid = (Grid)surface.FindName("HomeworkGrid");
        void Receive(StudyHomeworkSnapshot snapshot, char version)
        {
            var id = new string(version, 64);
            state = state with { Homework = new("READY", id, 1, "2026-10-12", state.Version) };
            surface.Apply(state, null);
            surface.ApplyHomework(id, snapshot with { Version = state.Version!, Date = "2026-10-12" });
        }
        bool Waiting() => grid.Children.OfType<TextBlock>().Any(t => t.Text.Contains("排版"));
        try
        {
            var clock = Stopwatch.StartNew();
            Receive(large, 'a');
            Assert(!open.IsEnabled && Waiting(), "initial heavy overview blocked until pagination was complete");
            state = state with { Homework = state.Homework! with { State = "DISABLED", SnapshotId = null } };
            surface.Apply(state, null);
            Assert(!Waiting() && !open.IsEnabled, "revoking a pending first snapshot retained the old loading state");
            var normal = small.Items[0] with { Content = string.Concat(Enumerable.Repeat("阅读材料并标记每段的主旨。\r\n", 900)) };
            Receive(small with { Items = [normal] }, 'b');
            Assert(!open.IsEnabled && Waiting(), "heavy replacement did not leave the dispatcher free");
            PumpUntil(() => open.IsEnabled && !Waiting(), "a valid background overview never became readable", 12);
            var first = grid.Children.OfType<StackPanel>().First().Children.OfType<TextBlock>().First().Text;
            Assert(first.StartsWith(normal.Subject, StringComparison.Ordinal), "obsolete overview replaced the new subject");
            Receive(large, 'c');
            open.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var body = (HomeworkReaderView)surface.FindName("ReaderBody");
            surface.Suspend();
            Assert(body.Position is null && body.Children.Count == 0, "suspending a pending update retained its reader");
            Receive(small, 'd');
            ((Button)surface.FindName("Next")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert(grid.Children.OfType<StackPanel>().Count() == small.Items.Count, "short snapshot could not replace a cancelled long update");
            Checks.Add($"overview accepts, revokes, supersedes and suspends in-flight WPF pagination ({clock.ElapsedMilliseconds} ms including successful medium reflow)");
        }
        finally { surface.Stop(); }
    }
}
