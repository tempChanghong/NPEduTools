using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;
using Outline = System.Windows.Shapes.Path;

internal static partial class Program
{
    private static void RunStudyProgressChecks()
    {
        var clock = new StudyUiClock();
        var surface = new StudySurface(() => Task.CompletedTask, () => { }, true, clock);
        var window = new Window { Content = surface, Width = 1440, Height = 810, WindowStyle = WindowStyle.None };
        T Element<T>(string name) where T : FrameworkElement => (T)surface.FindName(name);
        try
        {
            Exercise(window, w =>
            {
                foreach (var (phase, seconds, expected) in new[] {
                    ("Preparing", -120d, 0), ("Active", 0d, 0), ("Active", 2250d, 25),
                    ("Active", 4500d, 50), ("Active", 6750d, 75), ("Active", 8999d, 99),
                    ("Ended", 9000d, 100), ("Ended", 9100d, 100) })
                {
                    surface.Apply(StudyFixture(phase, seconds), null);
                    Assert(Element<TextBlock>("ProgressPercent").Text == $"{expected}%", $"wrong {phase} progress at {seconds}s");
                    var fill = Element<Outline>("ProgressFill");
                    Assert(fill.Visibility == (expected == 0 ? Visibility.Collapsed : Visibility.Visible), "zero/full progress outline visibility wrong");
                    if (expected is > 0 and < 100)
                    {
                        var bounds = fill.Data.Bounds;
                        double perimeter = 2 * (bounds.Width + bounds.Height - 72) + 36 * Math.PI;
                        Assert(Math.Abs(fill.StrokeDashArray[0] * fill.StrokeThickness / perimeter - seconds / 9000) < .00011, "rounded outline length disagrees with elapsed time");
                        Assert(fill.StrokeDashArray[1] * fill.StrokeThickness >= perimeter - .001, "outline has a second illuminated segment");
                    }
                    if (expected == 100) Assert(fill.StrokeDashArray is null || fill.StrokeDashArray.Count == 0, "full outline retained an end gap");
                }
                var state = StudyFixture("Active", 4500);
                surface.Apply(state, null); var half = Element<Outline>("ProgressFill").StrokeDashArray;
                clock.Ms += 1600;
                PumpUntil(() => Element<TextBlock>("ProgressCaption").Text == "进度暂停", "stale school progress did not pause");
                Assert(Element<TextBlock>("ProgressPercent").Text == "50%" && ReferenceEquals(half, Element<Outline>("ProgressFill").StrokeDashArray), "stale progress advanced or recreated the outline");
                var next = StudyFixture("Preparing", -120) with { Window = new(state.Window!.Start.AddDays(1), state.Window.End.AddDays(1)), SchoolNow = state.Window.Start.AddDays(1).AddMinutes(-2) };
                surface.Apply(next, null, TimeSpan.FromSeconds(2));
                Assert(Element<TextBlock>("ProgressPercent").Text == "—" && Element<Outline>("ProgressFill").Visibility == Visibility.Collapsed, "new stale window inherited old progress");
                surface.Apply(next, null);
                Assert(Element<TextBlock>("ProgressPercent").Text == "0%", "next preparation did not reset");
                var manual = state with { Manual = new(Guid.NewGuid(), 1, state.Execution!.SessionId, "Active", null, null, null, true, 600) };
                surface.Apply(manual, null); Assert(Element<TextBlock>("ProgressPercent").Text == "50%", "planned manual progress lost");
                surface.Apply(manual with { Manual = manual.Manual! with { HasPlannedEnd = false } }, null);
                Assert(Element<FrameworkElement>("ProgressInfo").Visibility == Visibility.Collapsed && Element<Outline>("ProgressTrack").Visibility == Visibility.Collapsed && Element<Outline>("ProgressFill").Visibility == Visibility.Collapsed, "manual ceiling displayed as planned progress");
                surface.Apply(manual, null); Assert(Element<TextBlock>("ProgressPercent").Text == "50%", "planned manual progress did not return");
                surface.Apply(state with { Window = new(state.Window!.Start, state.Window.Start) }, null);
                Assert(Element<FrameworkElement>("ProgressInfo").Visibility == Visibility.Collapsed, "invalid interval produced a numeric progress");
                Checks.Add("Progress outline: preparation/start/25/50/75/99/end, single clockwise segment, full closure, stale freeze, new-window reset, optional manual end and invalid interval; synthetic WPF only");
                w.Close();
            });
        }
        finally { surface.Stop(); window.Close(); }
    }
}
