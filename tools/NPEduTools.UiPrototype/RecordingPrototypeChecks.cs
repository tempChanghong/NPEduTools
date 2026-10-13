using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NPEduTools.UiPrototype;

internal static class RecordingPrototypeChecks
{
    internal static void Run(RecordingWorkspaceWindow w, string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException(description); checks.Add(description); }
        void Click(Button button) { Check(button.IsEnabled,button.Name + " is available"); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(w); }
        void Shot(string name)
        {
            Pump(w); var root = (FrameworkElement)w.Content;
            var image = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,PixelFormats.Pbgra32);
            image.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(output,name)); encoder.Save(file);
        }
        void Fits(FrameworkElement control)
        {
            var root = (FrameworkElement)w.Content; var point = control.TranslatePoint(new Point(),root);
            Check(control.IsVisible && control.ActualWidth > 0 && point.X >= -1 && point.Y >= -1 && point.X + control.ActualWidth <= root.ActualWidth + 1 && point.Y + control.ActualHeight <= root.ActualHeight + 1,
                control.Name + " remains reachable at " + w.Width + "x" + w.Height);
        }
        Button Course(int number) => Descendants(w.Lessons).OfType<Button>().Single(b => b.Tag is int n && n == number);
        w.Width = 1120; w.Height = 780; Pump(w); Shot("01-home.png");
        Click(w.HomePrimary); Check(w.ManualPane.IsVisible,"home opens immediate recording in one navigation action");
        Check(w.StartManual.Content as string == "开始录制","one clear primary action before recording"); Shot("02-manual-ready.png");
        Click(w.StartManual); Check(w.State.Active && w.LiveSummary.IsVisible && !w.IdleSummary.IsVisible,"session controls replace idle settings");
        Click(w.PlanTab); Check(w.State.Active,"switching to schedule leaves recording active");
        Click(w.RecordsTab); Check(w.State.Active,"switching to records leaves recording active");
        Click(w.ManualTab); Click(w.AudioGap); Check(w.AudioStatus.Text.Contains("等待恢复"),"audio wait appears without ending capture"); Shot("03-recording-warning.png");
        Click(w.AudioRecovered); Check(w.State.Active && w.AudioStatus.Text.Contains("静音"),"audio recovery continues and states loss explicitly");
        Click(w.Pause); Check(w.State.Phase == "Paused","pause is distinct from stop"); Click(w.Pause); Click(w.StopManual);
        Check(w.State.Phase == "Saved" && w.State.Records[0].Status == "已保存","stop produces only an explicitly simulated record");
        Click(w.PlanTab); Check(w.State.Plans().Single(p => p.Lesson.Number == 1).Selected == false,"weekly test excluded by production pure planner");
        var first = w.State.Plans().Single(p => p.Lesson.Number == 2);
        Check(!first.Conflict && first.Start.Hour == 8 && first.Start.Minute == 0,"first lesson starts at 08:00 despite excluded adjoining morning slot");
        Shot("04-today-plan.png"); Click(Course(3)); Check(w.State.Dirty && !w.State.Plans().Single(p => p.Lesson.Number == 3).Selected,"course change becomes an unsaved draft");
        Check(w.State.Plans(true).Single(p => p.Lesson.Number == 3).Selected,"unsaved change does not alter executable saved plan");
        Shot("05-unsaved-day.png"); Click(w.SavePlan); Check(!w.State.Dirty && !w.State.AutomaticEnabled,"save does not silently enable scheduling");
        Click(w.ToggleAutomatic); Click(w.Advance); Check(w.State.Active && w.State.Owner == "Automatic" && w.State.Started.Hour == 8,"saved first lesson starts only after explicit demo trigger");
        Click(w.ManualTab); Check(w.State.Active && w.ActiveSource.Text.Contains("语文"),"automatic session can be controlled through unified live page"); Shot("06-automatic-live.png");
        Click(w.PlanTab); Click(w.Advance); Check(w.State.Phase == "Saved","demo hard end saves automatic session");
        Check(w.State.Handled(w.State.Plans(true).Single(p => p.Lesson.Number == 2)),"completed task stays marked handled");
        Check(w.Lessons.ItemsSource.Cast<DemoLessonRow>().Single(r => r.Number == 2).Status == "已处理","completed row does not falsely promise another recording");
        Click(w.ManualTab); Click(w.StartManual); Click(w.PlanTab); Click(w.SkipNext);
        Check(w.State.Active && w.State.Owner == "Manual","skip next never ends active manual recording");
        var skipped = w.State.Records[0].Subject;
        Check(w.State.NextPlan()?.Lesson.Subject != skipped,"skip next removes that task from next summary");
        w.State.ClockReady = false; Click(w.ManualTab); Click(w.PlanTab);
        Check(w.State.Active,"loss of school clock does not end manual recording");
        w.State.AutomaticEnabled = false; Click(w.ManualTab); Click(w.PlanTab);
        Check(!w.ToggleAutomatic.IsEnabled,"automatic enable waits for confirmed school clock");
        w.State.ClockReady = true; Click(w.ManualTab); Click(w.PlanTab);
        Click(w.ManualTab); Click(w.FailCapture); Check(w.State.Phase == "Failed","failure has explicit preserved-fragment state");
        Click(w.HomeNav); Click(w.HomeRecords); Check(w.RecordFilter.SelectedIndex == 1,"home attention action filters unfinished records");
        var detail = Descendants(w.RecordItems).OfType<Button>().First(); Click(detail);
        Check(w.RecordDetail.IsVisible && w.RecordPath.Text.Contains("sessions"),"failed fragment path is available without opening or changing files"); Shot("07-unfinished-record.png");
        Click(w.PlanTab); Click(w.WeeklyEditor); w.Before.Text = "9"; Click(w.ApplyWeekly);
        Check(w.WeeklyPage.IsVisible && w.FeedbackText.Text.Contains("0–5"),"invalid margins remain in editor with actionable feedback");
        w.Before.Text = "2"; Click(w.ApplyWeekly); Check(w.PlanPane.IsVisible,"weekly settings return to day view without extra nested tabs");
        Click(w.ManualTab); Click(w.OpenSettings); w.Folder.Text = ""; Click(w.SaveSettings);
        Check(w.SettingsPage.IsVisible && w.FeedbackText.Text.Contains("填写"),"blank output location rejected without actual filesystem use");
        w.Folder.Text = @"D:\课堂录像"; w.Quality.SelectedIndex = 1; Click(w.SaveSettings);
        Check(w.InputSummary.Text.Contains("720p") && w.ManualPane.IsVisible,"saved memory-only settings update immediate summary");
        foreach (var size in new[] {new Size(1280,700),new Size(1024,700),new Size(900,620),new Size(760,540),new Size(640,480)})
        {
            w.Width = size.Width; w.Height = size.Height; Pump(w);
            Click(w.PlanTab); w.LessonScroll.ScrollToTop(); Pump(w); Fits(w.SavePlan); Fits(w.DiscardPlan); Fits(w.ToggleAutomatic); Fits(w.Advance); Fits(w.PlanTab);
            Check(w.LessonScroll.ViewportWidth > 0 && w.LessonScroll.ExtentWidth <= w.LessonScroll.ViewportWidth + 1,"day list needs no horizontal scroll");
            Check(w.LessonScroll.ViewportHeight >= 120,"schedule retains a usable vertical viewport");
            var courseButton = Course(2); courseButton.BringIntoView(); Pump(w); Fits(courseButton);
            w.LessonScroll.ScrollToTop(); Pump(w);
            if (size.Width == 1024) Shot("08-compact-today-plan.png");
            Click(w.ManualTab); Fits(w.StartManual); Click(w.StartManual); Fits(w.Pause); Fits(w.StopManual); Click(w.StopManual);
            Click(w.OpenSettings); Fits(w.SaveSettings); Fits(w.BackSettings); Click(w.BackSettings);
            if (size.Width == 640) { Click(w.PlanTab); Course(2).BringIntoView(); Pump(w); Shot("09-small-plan.png"); }
        }
        File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new { status="PASSED", checks, realDevices=false, schoolServices=false, productionSourceModified=false,
            logicalWindowSizes=new[]{"1280x700","1024x700","900x620","760x540","640x480"}, note="logical viewport checks, not physical 200% school touch acceptance" },new JsonSerializerOptions {WriteIndented=true}));
    }
    private static void Pump(Window window)
    {
        window.UpdateLayout(); var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false)); Dispatcher.PushFrame(frame); window.UpdateLayout();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) { var child=VisualTreeHelper.GetChild(root,i); yield return child; foreach(var nested in Descendants(child)) yield return nested; }
    }
}
