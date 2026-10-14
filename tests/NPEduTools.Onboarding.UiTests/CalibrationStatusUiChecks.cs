using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunCalibrationStatusVisualChecks()
    {
        var clock = new StudyUiClock();
        var surface = new StudySurface(() => Task.CompletedTask, () => { }, true, clock);
        var window = new Window { Content = surface, Width = 1280, Height = 720, WindowStyle = WindowStyle.None };
        var state = StudyFixture("Active", 4800);
        var noise = new NoiseState(Guid.NewGuid(), 7, "Active", "fixture", "mic", "synthetic", null, -40,
            "Good", new(4800, 4800, 1, -40, -30, 0, 48000), [], SessionId: state.Execution!.SessionId, SelectedDeviceId: "mic");
        var saved = new NoiseCalibrationStatus(noise.InstanceId, noise.Revision, "Saved", "Quick");
        var label = (TextBlock)surface.FindName("CalibrationText");
        var badge = (Border)surface.FindName("CalibrationBadge");
        try
        {
            Exercise(window, w =>
            {
                surface.Apply(state, noise, calibration: saved); w.UpdateLayout();
                Assert(label.Text == "已快速校准" && badge.ToolTip!.ToString()!.Contains("dBFS"), "quick label missing or misrepresents SPL");
                Assert(badge.ActualWidth < 270 && Canvas.GetTop(badge) + badge.ActualHeight < 975, "calibration badge overlaps signal/trend area");
                Assert(((TextBlock)surface.FindName("NoiseStatus")).Text == "正在监测", "calibration label changed capture status");
                Snapshot(w, "study-calibration-quick.png");
                var acoustic = state with { Settings = state.Settings! with { Noise = state.Settings.Noise with { Estimated = new(65, 50) } } };
                var estimatedNoise = noise with { Estimate = new(new string('c', 64), "a-weighted-test", "UsbReferenceBeta", "Slow", 90,
                    "2026-10-14T00:00:00.000Z", "Active", null, 60, new(4800, 1, 58, 67)) };
                surface.Apply(acoustic, estimatedNoise, calibration: saved with { State = "Ready", Method = "UsbReferenceBeta" }); w.UpdateLayout();
                Assert(label.Text == "已联机校准 (Beta)" && badge.ToolTip!.ToString()!.Contains("估算 dB(A)"), "USB method or unit missing");
                Assert(badge.ActualWidth < 300, "USB beta badge grew into neighboring controls");
                Snapshot(w, "study-calibration-usb.png");
                surface.Apply(acoustic, estimatedNoise with { Estimate = estimatedNoise.Estimate! with { State = "Invalidated", Reason = "INPUT_SETTINGS_CHANGED", CurrentDbA = null } },
                    calibration: saved with { State = "Invalidated", Method = "UsbReferenceBeta", Reason = "INPUT_SETTINGS_CHANGED" });
                Assert(label.Text == "校准已失效" && badge.ToolTip!.ToString()!.Contains("重新校准"), "invalid reference left a calibrated badge");
                Snapshot(w, "study-calibration-invalid.png");
                surface.Apply(acoustic with { Phase = "Preparing" }, noise, calibration: saved with { Method = "UsbReferenceBeta" });
                Assert(((TextBlock)surface.FindName("NoiseDetail")).Text == "估算 dB(A) · 等待输入核对", "saved reference claimed missing reference during preparation");
                surface.Apply(state with { Phase = "Preparing" }, noise, calibration: saved);
                Assert(label.Text == "已快速校准", "preparation lost saved calibration state");
                surface.Apply(state with { Manual = new(Guid.NewGuid(), 1, state.Execution.SessionId, "Active", null, null, null, true, 600) }, noise,
                    calibration: saved with { State = "Uncalibrated", Method = "None" });
                Assert(label.Text == "未校准", "manual study did not show calibration state");
                Snapshot(w, "study-calibration-manual.png");
                surface.Apply(state, noise, calibration: saved with { NoiseRevision = 8 });
                Assert(label.Text == "校准状态待确认", "mismatched revision remained calibrated");
                surface.Apply(state, noise, calibration: saved); clock.Ms += 1600;
                PumpUntil(() => label.Text == "校准状态待确认", "stale calibration remained valid");
                surface.Apply(state, noise); Assert(label.Text == "校准状态待确认", "old Host became uncalibrated instead of unknown");
                Checks.Add("Calibration badge: real WPF quick/USB Beta/invalid/uncalibrated/old-Host/stale/revision/manual/preparation states; 1280x720 logical layout; muted and no physical capture");
                w.Close();
            });
        }
        finally { surface.Stop(); window.Close(); }
        var settings = new StudyMonitoringWindow("NPEduTools.Test.CalibrationLayout." + Guid.NewGuid(), () => { });
        try
        {
            Exercise(settings, w =>
            {
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
                void Calibration(NoiseCalibrationStatus? value, NoiseState? signal) =>
                    typeof(StudyMonitoringWindow).GetMethod("RenderCalibration", flags)!.Invoke(settings, [value, signal]);
                w.Width = 640; w.Height = 460;
                typeof(StudyMonitoringWindow).GetMethod("Render", flags)!.Invoke(settings, [state]);
                Calibration(saved, noise); w.UpdateLayout();
                Assert(((TextBlock)settings.FindName("CalibrationStatus")).Text == "已快速校准", "school setting badge missing");
                Assert(((TextBlock)settings.FindName("CalibrationDetail")).Text.Contains("下次采集"), "saved setting profile falsely claims current input is checked");
                Assert(((ScrollViewer)settings.FindName("PageScroll")).HorizontalOffset == 0, "settings need horizontal scrolling at compact logical size");
                ((ScrollViewer)settings.FindName("PageScroll")).ScrollToVerticalOffset(300); w.UpdateLayout();
                Snapshot(w, "school-calibration-settings.png");
                Calibration(null, null);
                Assert(((TextBlock)settings.FindName("CalibrationStatus")).Text == "校准状态待确认", "settings retain old calibration after disconnect");
                Checks.Add("Calibration settings: real WPF saved-vs-applied copy, unknown on disconnect and compact scrollable school settings");
                w.Close();
            });
        }
        finally { settings.Close(); }
    }
}
