using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NPEduTools.UiPrototype;

internal static class PrototypeChecks
{
    internal static void Run(WorkspaceWindow w, string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks.Add(message); }
        void Click(Button button) { Check(button.IsEnabled, button.Name + " action available"); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(w); }
        void Shot(string name)
        {
            Pump(w);
            var client = (FrameworkElement)w.Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(client.ActualWidth), (int)Math.Ceiling(client.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(client); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name)); encoder.Save(file);
        }
        void Reachable(FrameworkElement control)
        {
            var point = control.TranslatePoint(new Point(), w);
            Check(control.IsVisible && control.ActualWidth > 0 && point.X >= 0 && point.Y >= 0 &&
                point.X + control.ActualWidth <= w.ActualWidth + 1 && point.Y + control.ActualHeight < w.ActualHeight - 5, control.Name + " fits the viewport");
        }
        w.Width = 1100; w.Height = 740; Pump(w); Shot("01-home.png");
        Check(!w.HomeTask.Text.Contains("正在采集"), "idle home does not imply live capture");
        Click(w.HomePrimary);
        Check(w.PageTitle.Text == "自习" && w.ManualPane.IsVisible, "home action opens unified study/manual page");
        Check(w.StartFields.Visibility == Visibility.Collapsed && w.EndFields.Visibility == Visibility.Collapsed, "optional dates start collapsed");
        Check(w.StudyPrimary.Content as string == "开始自习", "immediate action has one clear verb");
        Shot("02-manual-idle.png");
        Click(w.StudyPrimary); Check(w.State.ManualState == "Active", "immediate start enters manual session");
        Check(w.StudyPrimary.Content as string == "结束自习" && !w.ManualForm.IsVisible, "active session replaces configuration with session controls");
        Click(w.StudyPrimary); Check(w.State.ManualState == "Idle" && w.State.HasReport, "manual ending preserves report");
        w.Scheduled.IsChecked = true; w.StartTime.Text = "25:00"; Click(w.StudyPrimary);
        Check(w.State.ManualState == "Idle" && w.FeedbackText.Text.Contains("有效时间"), "invalid scheduled time has actionable feedback without starting");
        w.StartTime.Text = "19:00"; w.EndChoice.SelectedIndex = 1; w.EndTime.Text = "23:00"; Click(w.StudyPrimary);
        Check(w.State.ManualState == "Idle" && w.FeedbackText.Text.Contains("三小时"), "overlong schedule rejected");
        w.EndTime.Text = "21:30"; Click(w.StudyPrimary);
        Check(w.State.ManualState == "Scheduled" && w.StudyPrimary.Content as string == "取消预约", "valid future schedule has explicit cancel action and no capture");
        Shot("03-manual-scheduled.png"); Click(w.StudyPrimary);
        Click(w.SchoolTab); Check(w.PageTitle.Text == "自习" && w.SchoolPane.IsVisible, "school schedule uses same study entry");
        Shot("04-school-schedule.png");
        w.Scene.SelectedIndex = 1; Click(w.ManualTab);
        Check(!w.StudyPrimary.IsEnabled, "school active session disables new manual start");
        Check(!w.SchoolCapture.Text.Contains("待确认"), "school-active fixture supplies fresh execution separately");
        Click(w.SchoolTab); Shot("05-school-active.png");
        w.Scene.SelectedIndex = 2; Click(w.HomeNav);
        Check(!w.HomeNext.Text.Contains("19:15"), "clock loss withdraws next-time prediction");
        Click(w.HomePrimary); Check(w.DevicesPage.IsVisible && !w.State.ClockReady, "clock error action leads directly to connection checks");
        Shot("06-connection-recovery.png"); Click(w.ClockRetry); Check(w.State.ClockReady, "explicit connection retry recovers time");
        Click(w.StudyNav); Click(w.ManualTab); w.Immediate.IsChecked = true; w.EndChoice.SelectedIndex = 0;
        Click(w.StudyPrimary); Click(w.StudySecondary); Click(w.ClockRetry);
        Check(w.State.ManualState == "Active", "checking school time does not end manual study");
        Click(w.StudyNav); Click(w.StudyPrimary);
        Click(w.DevicesNav);
        // Use routed events on the actual WPF calibration button, not a mirrored UI model.
        var openCalibration = Descendants(w.DevicesPage).OfType<Button>().Single(b => b.Content as string == "联机校准 (Beta)  →");
        Click(openCalibration); w.Password.Password = "wrong"; Click(w.VerifyMeter);
        Check(!w.CalibrationPrimary.IsEnabled && w.MeterStatus.Text.Contains("不正确"), "wrong demo password cannot start calibration");
        w.Password.Password = "123456"; Click(w.VerifyMeter);
        Check(w.CalibrationPrimary.IsEnabled && w.MeterStatus.Text.Contains("验证通过"), "valid demo password has visible confirmation");
        Check(!w.CredentialsPanel.IsVisible && w.MeterPreferences.IsVisible, "verified credentials collapse to expose instrument and calibration mode");
        Shot("07-calibration-connected.png"); Click(w.CalibrationPrimary); w.AdvanceSampling(); Shot("12-calibration-sampling.png");
        var disconnect = Descendants(w.CalibrationSampling).OfType<Button>().Single(); Click(disconnect);
        Check(w.CalibrationSetup.IsVisible && !w.CalibrationPrimary.IsEnabled && !w.State.ReferenceSaved, "USB interruption returns to verification and cannot save stale sample");
        w.AdvanceSampling(); Check(w.CalibrationSetup.IsVisible, "late sample tick cannot revive disconnected operation");
        Shot("08-calibration-disconnected.png");
        w.Password.Password = "123456"; Click(w.VerifyMeter); Click(w.CalibrationPrimary);
        for (int i = 0; i < 6; i++) w.AdvanceSampling();
        Check(w.CalibrationResult.IsVisible && !w.CalibrationPrimary.IsEnabled, "completed sampling requires explicit result confirmation");
        Shot("09-calibration-result.png"); w.ConfirmReference.IsChecked = true; Click(w.CalibrationPrimary);
        Check(w.State.ReferenceSaved && w.DevicesPage.IsVisible, "confirmed reference returns to shared devices");
        w.Microphone.SelectedIndex = 1; Check(!w.State.ReferenceSaved && w.FeedbackText.Text.Contains("失效"), "input change invalidates shared reference");
        Check(w.HomeReadiness.Text.Contains("备用麦克风") && w.StudyFooter.Text.Contains("备用麦克风"), "shared microphone summary follows explicit device selection");
        Click(openCalibration); w.Password.Password = "123456"; Click(w.VerifyMeter); Click(w.CalibrationPrimary); Click(w.HomeNav);
        for (int i = 0; i < 6; i++) w.AdvanceSampling();
        Check(!w.State.ReferenceSaved && w.HomePage.IsVisible && w.FeedbackText.Text.Contains("已取消"), "leaving sampling cannot silently finish or save");
        foreach (var size in new[] {new Size(1280,680),new Size(1100,740),new Size(1024,540),new Size(900,640),new Size(840,580),new Size(760,520)})
        {
            w.Width = size.Width; w.Height = size.Height; Pump(w);
            Click(w.StudyNav); w.StudyScroll.ScrollToTop(); Pump(w);
            Reachable(w.StudyPrimary); Reachable(w.StudySecondary); Reachable(w.ManualTab); Reachable(w.SchoolTab);
            Click(w.DevicesNav); Click(openCalibration); w.Password.Password = "123456"; Click(w.VerifyMeter);
            Reachable(w.CalibrationPrimary); Reachable(w.CalibrationBack);
            if (size.Height >= 580) Reachable(w.CalibrationMode);
            if (size.Width == 840) Shot("10-compact-calibration.png");
            Click(w.HomeNav); Reachable(w.HomePrimary);
            if (size.Width == 840) Shot("11-compact-home.png");
        }
        var model = new WorkspaceState(); Check(model.Arrange(true,null,"",false,null,"") is null, "fixture manual session can start without dates");
        model.StartSchool(); Check(model.HasReport && model.ManualState == "Idle" && model.SchoolActive, "fixture scheduled priority retains manual report without simultaneous sessions");
        File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new {status="PASSED",checks,environment="isolated-wpf-prototype",logicalWindowSizes=new[]{"1280x680","1100x740","1024x540","900x640","840x580","760x520"},realDevices=false,schoolConnections=false,businessSourceModified=false},new JsonSerializerOptions{WriteIndented=true}));
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout(); var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));
        Dispatcher.PushFrame(frame); window.UpdateLayout();
    }
    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) { var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested; }
    }
}
