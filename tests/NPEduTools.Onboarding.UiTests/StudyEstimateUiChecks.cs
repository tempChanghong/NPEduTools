using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunStudyEstimateVisualChecks()
    {
        var state = StudyFixture(); var clock = new StudyUiClock();
        var surface = new StudySurface(() => Task.CompletedTask, () => { }, true, clock);
        var owner = new Window { Content = surface, Width = 1200, Height = 800, ShowInTaskbar = false };
        try
        {
            Exercise(owner, _ =>
            {
                var e = new NoiseEstimate(new string('a',64), "a-sos-band8500-v1|sample-energy-fs-v1", "ManualReference", "Slow",
                    90, "2026-10-11T00:00:00.000Z", "Active", null, 60, new(8,.8,60,65));
                var noise = new NoiseState(Guid.NewGuid(),1,"Active","synthetic", "fixture","fake", DateTimeOffset.UtcNow,-30,"Good",
                    new(10,10,1,-30,-20,0,100),[new(8,-30,"Good"),new(9,-30,"Good")],SessionId:state.Execution!.SessionId,
                    Estimate:e,EstimatedTrend:[new(8,60,"Good"),new(9,60,"Good")]);
                var settings = state.Settings! with { Presentation = new("StatusDbfs",180), Noise = state.Settings!.Noise with { Estimated = new(65,55) } };
                var calibrated = state with { Settings = settings, Threshold = new(noise.SessionId!.Value,"Medium") };
                surface.Apply(calibrated,noise); owner.UpdateLayout();
                string Text(string name) => ((TextBlock)surface.FindName(name)).Text;
                Assert(Text("Dbfs").Contains("60.0") && Text("Dbfs").Contains("估算 dB(A)"), "estimated display reuses a raw digital label");
                Assert(Text("NoiseDetail").Contains("Slow"), "estimate omits time weighting");
                var reading = (TextBlock)surface.FindName("Dbfs");
                var textWidth = new System.Windows.Media.FormattedText(reading.Text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new System.Windows.Media.Typeface(reading.FontFamily, reading.FontStyle, reading.FontWeight, reading.FontStretch), reading.FontSize, reading.Foreground,
                    System.Windows.Media.VisualTreeHelper.GetDpi(reading).PixelsPerDip).WidthIncludingTrailingWhitespace;
                Assert(reading.ActualWidth + 1 >= textWidth, "estimated number or unit is clipped in the footer");
                Snapshot(owner,"study-estimated-level.png");
                surface.Apply(calibrated,noise with { Estimate = e with { State="Invalidated",Reason="INPUT_SETTINGS_CHANGED",CurrentDbA=null } });
                Assert(Text("Dbfs").Contains("—") && !Text("Dbfs").Contains("-30"), "invalid estimate falls back to raw with acoustic units");
                Assert(Text("NoiseDetail").Contains("失效"), "invalid estimate lacks an explanation");
                Assert((string)typeof(StudySurface).GetField("_edgeBand",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(surface)! == "None", "invalid estimate retained threshold edges");
                surface.Apply(calibrated with { Settings = settings with { Noise = settings.Noise with { Estimated = null } } },noise);
                Assert(Text("Dbfs").Contains("-30.0 dBFS") && !Text("Dbfs").Contains("dB(A)"), "digital mode reuses an estimated value");
                Checks.Add("Estimated/digital school units: 60 estimated dB(A) and -30 dBFS stay separate; invalid reference clears acoustic values and threshold edges without raw fallback");
                surface.Stop(); owner.Close();
            });
        }
        finally { surface.Stop(); owner.Close(); }
    }
}
