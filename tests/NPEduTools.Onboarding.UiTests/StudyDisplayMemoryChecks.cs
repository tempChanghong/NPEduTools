using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunStudyMemoryChecks()
    {
        // Separate process, no RenderTargetBitmap, no MainWindow/App startup or hardware.
        var surface = new StudySurface(() => Task.CompletedTask, () => { }, true, reminderPermission: (_, _) => Task.FromResult(true));
        var window = new Window { Content = surface, Width = 1440, Height = 810, WindowStyle = WindowStyle.None };
        var samples = new List<object>();
        var instance = Guid.NewGuid(); var session = Guid.NewGuid();
        var discipline = Guid.NewGuid();
        var frame = new DispatcherFrame(); var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            double seconds = elapsed.Elapsed.TotalSeconds;
            string phase = seconds < 4 ? "Preparing" : "Active";
            var state = StudyFixture(phase, phase == "Preparing" ? -120 + seconds : seconds < 8 ? seconds - 4 : 600 + seconds);
            state = state with { Display = new(instance, true, true, 0), Execution = state.Execution! with { SessionId = session } };
            string band = seconds < 8 || seconds >= 20 ? "None" : seconds < 12 ? "Medium" : "High";
            state = state with { Threshold = new(session, band, seconds is >= 12 and < 17 ? discipline : null) };
            var noise = new NoiseState(instance, 1, "Active", "synthetic", "fixture", "合成麦克风", null, band == "High" ? -35 : band == "Medium" ? -45 : -57, "Good", new(600, 600, 1, -60, -40, 0, 100),
                Enumerable.Range(0, 120).Select(i => new NoisePoint(540 + i * .5, -57 + 10 * Math.Sin(i / 4d), "Good")).ToArray(), SessionId: session);
            surface.Apply(state, noise);
            using var process = Process.GetCurrentProcess(); process.Refresh();
            samples.Add(new { seconds, phase, band, workingSetBytes = process.WorkingSet64, privateBytes = process.PrivateMemorySize64, managedHeapBytes = GC.GetTotalMemory(false) });
            if (seconds >= 24) frame.Continue = false;
        };
        try
        {
            window.Show(); timer.Start(); Dispatcher.PushFrame(frame);
            File.WriteAllText(Path.Combine(_output, "memory-no-screenshots.json"), JsonSerializer.Serialize(new
            {
                kind = "isolated-wpf-study-control", renderTier = System.Windows.Media.RenderCapability.Tier >> 16,
                fullApp = false, host = false, realDevices = false, production = false, screenshots = false,
                logicalWidth = window.ActualWidth, logicalHeight = window.ActualHeight, samples
            }, new JsonSerializerOptions { WriteIndented = true }));
            Checks.Add("Separate WPF process sampled for 24s at 1440x810 logical pixels through preparation/start/medium/high/discipline/quiet, without screenshots, real Host or devices; not target-screen acceptance");
        }
        finally { timer.Stop(); surface.Stop(); window.Close(); }
    }
}
