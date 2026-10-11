using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Core;

internal static partial class Program
{
    private sealed record StudyResourceSample(double Seconds, long WorkingSetBytes, long PrivateBytes,
        long ManagedHeapBytes, int Handles, double CpuSeconds, int HomeworkCards, int TrendShapes,
        int Pages, int DisciplineKeys, int AudioRequests);

    private static object SoakField(object owner, string name) => owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static void RunStudySoakChecks(bool accelerated)
    {
        var samples = new List<StudyResourceSample>();
        var payloads = new List<WeakReference>();
        var wall = Stopwatch.StartNew();
        var released = RunStudySoakWindow(samples, payloads, accelerated);
        double wallSeconds = wall.Elapsed.TotalSeconds;
        // Only the release diagnostic uses forced collection. The five-minute series does not.
        var drain = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => { if (drain.Elapsed.TotalSeconds >= 2) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        int retainedPayloads = payloads.Count(reference => reference.IsAlive);
        bool surfaceReleased = !released.Surface.IsAlive, windowReleased = !released.Window.IsAlive;
        File.WriteAllText(Path.Combine(_output, "soak-no-screenshots.json"), JsonSerializer.Serialize(new
        {
            kind = "isolated-wpf-study-soak", actualDurationSeconds = wallSeconds, logicalDurationSeconds = samples[^1].Seconds, accelerated,
            renderTier = RenderCapability.Tier >> 16, logicalWidth = 1440, logicalHeight = 810,
            fullApp = false, host = false, realDevices = false, production = false, screenshots = false,
            mutedAudio = true, sensorInput = "synthetic", forcedGcDuringSeries = false,
            homeworkUpdateSeconds = 8, homeworkUpdatesStopAtSeconds = 60, rotationSeconds = 60, syntheticDisciplineSeconds = 120,
            hideCycleSeconds = 90, inputIntervalMilliseconds = 500, trendPoints = 120,
            retention = new { surfaceReleased, windowReleased, trackedPayloads = payloads.Count, retainedPayloads },
            samples
        }, new JsonSerializerOptions { WriteIndented = true }));
        using var coverage = JsonDocument.Parse(File.ReadAllText(Path.Combine(_output, "coverage.json")));
        Assert(coverage.RootElement.GetProperty("maxPage").GetInt32() > 0 &&
            coverage.RootElement.GetProperty("committedSnapshots").GetInt32() > 1 &&
            coverage.RootElement.GetProperty("hides").GetInt32() >= 3,
            "soak did not commit pending homework, rotate pages and hide/resume");
        Assert(surfaceReleased && windowReleased, "stopped study surface/window survived dispatcher drain and collection");
        Assert(retainedPayloads == 0, "retired synthetic homework/noise payloads survived closing the study display");
        Checks.Add($"300 {(accelerated ? "synthetic" : "real")} seconds: synthetic 120-point trends, 24-item mixed-length homework refreshed every 8s for the first 60s, default 60s reading rotation, hide/resume, medium/high edges and muted native audio; no forced collection during series");
        Checks.Add("Stopped surface/window and all tracked retired payloads released after dispatcher drain and diagnostic collection; no absolute MiB pass budget or whole-evening leak claim");
        RunStudyCacheCapacityChecks();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Surface, WeakReference Window) RunStudySoakWindow(
        List<StudyResourceSample> samples, List<WeakReference> payloads, bool accelerated)
    {
        var clock = accelerated ? new StudyUiClock() : null;
        var surface = new StudySurface(() => Task.CompletedTask, () => { }, true, clock,
            reminderPermission: (_, _) => Task.FromResult(true));
        var window = new Window { Content = surface, Width = 1440, Height = 810, WindowStyle = WindowStyle.None };
        var instance = Guid.NewGuid(); var session = Guid.NewGuid();
        var discipline = Guid.NewGuid();
        var elapsed = Stopwatch.StartNew(); var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(accelerated ? 1 : 500) };
        int update = -1, bandCycle = -1, lastSample = -1, hiddenCycle = -1, maxPage = 0, hides = 0;
        var committedSnapshots = new HashSet<string>();
        bool hidden = false; Exception? failure = null;
        timer.Tick += (_, _) =>
        {
            try
            {
                if (clock is not null) clock.Ms += 500;
                double seconds = clock is null ? elapsed.Elapsed.TotalSeconds : clock.Ms / 1000d;
                int hideCycle = (int)(seconds / 90);
                if (hideCycle > 0 && hideCycle != hiddenCycle)
                {
                    hiddenCycle = hideCycle; hidden = true; hides++; surface.Suspend(); window.Hide(); return;
                }
                if (hidden) { hidden = false; window.Show(); }
                string phase = seconds < 2 ? "Preparing" : "Active";
                var state = StudyFixture(phase, phase == "Preparing" ? -120 + seconds : seconds - 2);
                int cycle = (int)(seconds / 120);
                if (cycle != bandCycle) { bandCycle = cycle; discipline = Guid.NewGuid(); }
                string band = seconds % 30 < 10 ? "None" : seconds % 30 < 20 ? "Medium" : "High";
                int revision = (int)(Math.Min(60, seconds) / 8);
                string snapshotId = revision.ToString("x64");
                state = state with
                {
                    Display = new(instance, true, true, 0), Execution = state.Execution! with { SessionId = session },
                    Threshold = new(session, band, band == "High" && seconds % 120 < 30 ? discipline : null),
                    Homework = new("READY", snapshotId, 1, "2026-10-12", snapshotId)
                };
                var noise = new NoiseState(instance, 1, "Active", "synthetic", "fixture", "合成麦克风", null,
                    band == "High" ? -35 : band == "Medium" ? -45 : -57, "Good",
                    new(600 + seconds, 600 + seconds, 1, -60, -40, 0, 100),
                    Enumerable.Range(0, 120).Select(i => new NoisePoint(540 + seconds + i * .5,
                        -57 + 10 * Math.Sin(i / 4d), "Good")).ToArray(), SessionId: session);
                surface.Apply(state, noise);
                if (revision != update)
                {
                    update = revision;
                    var homework = new StudyHomeworkSnapshot(state.Version!, "2026-10-12",
                        Enumerable.Range(0, 24).Select(i => new StudyHomeworkItem("work" + i, revision + 1,
                            "物理", "", i % 8 == 0
                                ? string.Concat(Enumerable.Repeat($"更新 {revision}：完成实验题，分析现象并列出条件。\n", 16))
                                : $"更新 {revision}：完成第 {i + 1} 题，写清推理过程。",
                            [new("one", "行政一班"), new("walk", "物理走班 A")])).ToArray());
                    StudyHomeworkContract.Validate(homework);
                    surface.ApplyHomework(snapshotId, homework); payloads.Add(new(homework)); payloads.Add(new(noise));
                }
                var cards = (Grid)surface.FindName("HomeworkGrid");
                var trend = (Canvas)surface.FindName("TrendCanvas");
                var pages = (StudyHomeworkPage[])SoakField(surface, "_pages");
                maxPage = Math.Max(maxPage, ((StudyHomeworkPager)SoakField(surface, "_pager")).Page);
                if (SoakField(surface, "_loadedHomework") is string loaded) committedSnapshots.Add(loaded);
                Assert(cards.Children.Count <= 6 && pages.All(page => page.Items.Count <= (page.Wide ? 2 : 6)), "homework visual children grew beyond page capacity");
                Assert(trend.Children.Count <= 120, "trend retained shapes outside the bounded input");
                int sample = (int)(seconds / 5);
                if (sample != lastSample || seconds >= 300)
                {
                    lastSample = sample;
                    using var process = Process.GetCurrentProcess(); process.Refresh();
                    var audio = (StudyReminderAudio)SoakField(surface, "_audio");
                    samples.Add(new(seconds, process.WorkingSet64, process.PrivateMemorySize64, GC.GetTotalMemory(false),
                        process.HandleCount, process.TotalProcessorTime.TotalSeconds, cards.Children.Count,
                        trend.Children.Count, pages.Length, ((ICollection)SoakField(surface, "_disciplineOrder")).Count, audio.Requests));
                    File.WriteAllText(Path.Combine(_output, "progress.json"), JsonSerializer.Serialize(samples[^1]));
                }
                if (seconds >= 300) frame.Continue = false;
            }
            catch (Exception error) { failure = error; frame.Continue = false; }
        };
        try
        {
            window.Show(); timer.Start(); Dispatcher.PushFrame(frame);
            if (failure is not null) throw new InvalidOperationException("Study soak failed", failure);
            Assert(samples.Count >= 50 && samples[^1].AudioRequests >= 5, "soak did not exercise a full series and muted discipline playback");
            File.WriteAllText(Path.Combine(_output, "coverage.json"), JsonSerializer.Serialize(new { maxPage, committedSnapshots = committedSnapshots.Count, hides, updates = update + 1,
                remaining = ((StudyHomeworkPager)SoakField(surface, "_pager")).RemainingSeconds }));
            return (new(surface), new(window));
        }
        finally { timer.Stop(); surface.Stop(); window.Content = null; window.Close(); }
    }

    private static void RunStudyCacheCapacityChecks()
    {
        var clock = new StudyUiClock();
        var surface = new StudySurface(() => Task.CompletedTask, () => { }, true, clock,
            (_, _) => Task.FromResult(false));
        var instance = Guid.NewGuid(); var session = Guid.NewGuid();
        try
        {
            for (int i = 0; i < 300; i++)
            {
                clock.Ms += 6000;
                var state = StudyFixture(); var start = state.Window!.Start.AddSeconds(i);
                state = state with { Display = new(instance, true, true, 0), Window = new(start, start.AddMinutes(150)),
                    SchoolNow = start.AddMinutes(10), Execution = state.Execution! with { SessionId = session },
                    Threshold = new(session, "High", Guid.NewGuid()) };
                var noise = new NoiseState(instance, 1, "Active", "synthetic", "fixture", "合成麦克风", null,
                    -35, "Good", new(600, 600, 1, -60, -40, 0, 100), [], SessionId: session);
                surface.Apply(state, noise);
                Assert(((ICollection)SoakField(surface, "_disciplineOrder")).Count <= 128, "discipline FIFO exceeded capacity");
                Assert(((HashSet<Guid>)SoakField(surface, "_disciplineAttempts")).Count <= 128, "discipline set exceeded capacity");
                var timeline = SoakField(surface, "_timeline");
                Assert(((ICollection)SoakField(timeline, "_order")).Count <= 128, "stage FIFO exceeded capacity");
                Assert(((HashSet<string>)SoakField(timeline, "_seen")).Count <= 128, "stage set exceeded capacity");
            }
            Assert(((ICollection)SoakField(surface, "_disciplineOrder")).Count == 128, "discipline stress did not reach capacity");
            Assert(((ICollection)SoakField(SoakField(surface, "_timeline"), "_order")).Count == 128, "stage stress did not reach capacity");
            Checks.Add("Separate accelerated 300-window/300-offer capacity probe: stage and discipline FIFOs never exceed 128; synthetic time, no audio grants and no elapsed school-session claim");
        }
        finally { surface.Stop(); }
    }
}
