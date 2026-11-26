using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Core;

internal static partial class Program
{
    private static void RunRecordingWorkspaceChecks()
    {
        RunRecordingQuickNavigationChecks();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        string pipe = "NPEduTools.Test.recording-workspace." + Guid.NewGuid().ToString("N");
        string preferences = StartupPreferencesStore.PathFor(pipe);
        string plansPath = preferences.Replace(".startup.json", ".recording-plans.json", StringComparison.Ordinal);
        using var stop = new CancellationTokenSource();
        var requests = new ConcurrentQueue<HostRequest>();
        RecordingState state = new("Idle", "隔离后台准备录制");
        AutomaticRecordingState automatic = new(false, Guid.Empty, "隔离后台：自动录课未开启", null, []);
        Guid bridge = Guid.NewGuid(), connection = Guid.NewGuid(), profile = Guid.NewGuid(), firstSubject = Guid.NewGuid(), secondSubject = Guid.NewGuid();
        var date = new DateOnly(2026, 10, 13);
        var start = new DateTimeOffset(date.ToDateTime(new TimeOnly(7, 59)), TimeSpan.Zero);
        var upcoming = new UpcomingRecording("语文", start.AddMinutes(1), start.AddMinutes(46));
        var elapsed = Stopwatch.StartNew();
        int sequence = 0, probes = 0;
        bool offline = false;
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var channel = new NamedPipeServerStream(pipe, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await channel.WaitForConnectionAsync(stop.Token);
                    var request = await Protocol.ReadAsync<HostRequest>(channel, stop.Token);
                    if (request.Capability == ClassroomActionAccess.ManualRecordingStatusCapability)
                    {
                        await Protocol.WriteAsync(channel, new HostResponse(1, request.RequestId, "Rejected", "UnknownCapability", "Older fixture Host"), stop.Token);
                        continue;
                    }
                    requests.Enqueue(request);
                    Assert(Protocol.Validate(request) is null, "workspace sent invalid protocol parameters");
                    if (offline) continue;
                    SchoolClockFrame? frame = null;
                    DayForecast? forecast = null;
                    if (request.Capability is "classisland.school-clock" or "classisland.day-plan")
                    {
                        var now = start.Add(elapsed.Elapsed);
                        var schoolDate = request.SchoolDate ?? date;
                        var dayStart = start.AddDays(schoolDate.DayNumber - date.DayNumber);
                        var schedule = new DaySchedule(profile, profile, profile, "隔离课表", schoolDate, "fixture-1", now, true, true, "模拟学校时间",
                            [new(1, firstSubject, "周测", dayStart.AddMinutes(-29), dayStart.AddMinutes(1), true),
                             new(2, secondSubject, "语文", dayStart.AddMinutes(1), dayStart.AddMinutes(46), true),
                             new(3, secondSubject, "阅读", dayStart.AddMinutes(56), dayStart.AddMinutes(101), true),
                             new(4, firstSubject, "自习", dayStart.AddMinutes(111), dayStart.AddMinutes(156), true)]);
                        if (request.SchoolDate is not null) forecast = new(bridge, date, schoolDate, schedule);
                        else frame = new(connection, bridge, ++sequence, 0, now, 0, "Advancing", "隔离学校时钟", schedule);
                    }
                    else if (request.Recording is { } command)
                    {
                        if (command.Action != "start") Assert(state.Control?.Matches(command.Control) == true, "command lost session fence");
                        state = command.Action switch
                        {
                            "start" => new("Recording", "隔离后台正在录制", Seconds: 42, Control: new("Manual", Guid.NewGuid(), "", 0, 0)),
                            "pause" => state with { Phase = "Paused", Message = "隔离后台暂停" },
                            "resume" => state with { Phase = "Recording", Message = "隔离后台继续" },
                            "stop" => state with { Phase = "Saved", Message = "隔离后台已保存", OutputFile = Path.Combine(_output, "synthetic.mp4") },
                            _ => state
                        };
                    }
                    else if (request.Automatic is { } autoCommand && autoCommand.Action is not "lease")
                    {
                        automatic = automatic with { Enabled = autoCommand.Action == "enable" || automatic.Enabled && autoCommand.Action != "disable", ClientId = autoCommand.ClientId };
                        if (autoCommand.Action == "enable") automatic = automatic with { Message = "自动录课已开启", NextState = "Ready", Next = upcoming };
                        if (autoCommand.Action == "skip-upcoming") automatic = automatic with { Message = "隔离后台已跳过下一节", NextState = "None", Next = null };
                    }
                    await Protocol.WriteAsync(channel, new HostResponse(Protocol.Version, request.RequestId, "Succeeded", null,
                        "隔离回执", Recording: state, Automatic: automatic, SchoolClock: frame, Forecast: forecast), stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        var main = (MainWindow)typeof(MainWindow).GetConstructors(flags).Single().Invoke([pipe, null, false]);
        void Invoke(string method, params object[] values) => typeof(MainWindow).GetMethod(method, flags)!.Invoke(main, values);
        T Field<T>(string name) where T : class => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(main)!;
        Func<Task<RecordingEnvironment>> probe = () =>
        {
            probes++;
            return Task.FromResult(new RecordingEnvironment(true, null, [new("fixture", "隔离大屏", 0, 0, 2560, 1440, true)],
                [new("fixture-mic", "隔离麦克风")], [new("fixture-speaker", "隔离扬声器")]));
        };
        typeof(MainWindow).GetField("_recordingProbe", flags)!.SetValue(main, probe);
        Invoke("InitializeRecording");
        var client = Field<object>("_recording");
        bool Available() => (bool)client.GetType().GetProperty("StateAvailable")!.GetValue(client)!;
        try
        {
            Exercise(main, owner =>
            {
                PumpUntil(Available, "recording status was not loaded");
                Click(main, "RecordingNav");
                Assert(((FrameworkElement)main.FindName("RecordingPlanHost")).IsVisible && probes == 0, "idle recording navigation did not default to today's schedule");
                var plan = Field<AutoRecordingWindow>("_autoRecordingWindow");
                var grid = (DataGrid)plan.FindName("Plans");
                PumpUntil(() => grid.Items.Count == 4 && Button(plan, "TomorrowButton").IsEnabled, "embedded automatic page did not load its schedule and school clock");
                CaptureRecordingPlanFirstScreen(main, plan, 840, 600, "automatic-first-840-600.png");
                Assert(!Button(plan, "RealSkip").IsVisible && Button(plan, "ToggleReal").Content.ToString() == "开启自动录课",
                    "disabled automatic recording offered a skip or hid enable");
                Checks.Add("840×600 initial plan: master state and date are visible with three fully readable course rows and the main window's three caption buttons");
                Click(main, "RecordingManualTab");
                var manual = Field<RecordingWindow>("_recordingWindow");
                PumpUntil(() => Button(manual, "RecordStart").IsEnabled, "embedded device probe did not populate options");
                Assert(probes == 1 && !manual.IsVisible && ((FrameworkElement)main.FindName("RecordingWorkspace")).IsVisible,
                    "recording opened another window or did not load embedded controller");
                Snapshot(main, "recording-workspace-immediate.png");
                Click(main, "SettingsNav");
                Click(main, "TeachingSettingsTab"); main.UpdateLayout();
                var teaching = (FrameworkElement)main.FindName("TeachingSettingsPanel");
                var settingsAction = FlowElements<Button>(teaching).Single(button => Equals(button.Content, "录制设置"));
                settingsAction.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); main.UpdateLayout();
                Assert(((FrameworkElement)manual.FindName("RecordingSettings")).IsVisible,
                    "recording settings shortcut did not open the editor");
                Click(manual, "ReturnRecordingSettings");
                Invoke("InitializeOnboarding");
                typeof(MainWindow).GetField("_onboarding", flags)!.SetValue(main,
                    new OnboardingState(Features: OnboardingFeatures.Recording, Step: "recording"));
                Invoke("ShowOnboarding");
                var wizard = Application.Current.Windows.OfType<OnboardingWindow>().Single();
                try
                {
                    Click(wizard, "OobeRecordingSettings", true); main.UpdateLayout();
                    Assert(((FrameworkElement)manual.FindName("RecordingSettings")).IsVisible,
                        "wizard recording settings shortcut did not open the editor");
                }
                finally { wizard.Close(); }
                Click(manual, "ReturnRecordingSettings");
                Checks.Add("Teaching settings and onboarding both open the embedded recording editor");
                string originalDirectory = ((TextBox)manual.FindName("OutputDirectory")).Text;
                ((TextBox)manual.FindName("OutputDirectory")).Text = "   ";
                Click(manual, "OpenFolder");
                Assert(((TextBlock)manual.FindName("RecordingError")).Text.Contains("保存位置"), "blank folder action has no usable feedback");
                Assert(!state.Active && requests.All(r => r.Recording is null), "invalid folder action changed recording state");
                ((TextBox)manual.FindName("OutputDirectory")).Text = originalDirectory;
                Checks.Add("Actual recording folder button: a blank editable directory gives feedback without launching anything or mutating the recording session");
                Click(main, "RecordingPlanTab");
                CheckRecordingSettingsRoundTrip(main, plan, manual, grid, date);
                Click(main, "RecordingManualTab");
                Click(manual, "RecordStart");
                PumpUntil(() => state.Active && Button(manual, "RecordStop").IsEnabled, "synthetic recording did not start");
                Assert(((FrameworkElement)manual.FindName("RecordingSettings")).Visibility == Visibility.Collapsed,
                    "live recording still displays setup form");
                int mutations = requests.Count(r => r.Recording is not null || r.Automatic?.Action is not (null or "lease"));
                Click(main, "RecordingPlanTab");
                CheckRecordingPlanScroll((DataGrid)plan.FindName("Plans"));
                Assert(state.Active && requests.Count(r => r.Recording is not null || r.Automatic?.Action is not (null or "lease")) == mutations,
                    "switching to schedule mutated recording state");
                Click(main, "RecordingRecordsTab");
                Assert(state.Active && ((TabControl)plan.FindName("AutoSections")).SelectedIndex == 4,
                    "records page interrupted recording or showed the wrong section");
                Invoke("HomeNavClicked", main, new RoutedEventArgs());
                Click(main, "RecordingHomeButton");
                Assert(state.Active && ((FrameworkElement)main.FindName("RecordingManualHost")).IsVisible, "homepage active recording action opened setup or ended session");
                Assert(requests.Count(r => r.Recording is not null || r.Automatic?.Action is not (null or "lease")) == mutations, "homepage manager button mutated recording");
                Invoke("SettingsNavClicked", main, new RoutedEventArgs());
                Assert(state.Active && requests.Count(r => r.Recording is not null || r.Automatic?.Action is not (null or "lease")) == mutations,
                    "home/settings navigation stopped or disabled recording");
                Click(main, "RecordingNav");
                Assert(ReferenceEquals(manual, Field<RecordingWindow>("_recordingWindow")) && probes == 1, "navigation duplicated controller or reset devices");
                Click(manual, "RecordPause"); PumpUntil(() => state.Phase == "Paused" && Button(manual, "RecordPause").Content.ToString() == "继续录制", "pause receipt not shown");
                Click(manual, "RecordPause");
                PumpUntil(() => state.Phase == "Recording" && Button(manual, "RecordPause") is { IsEnabled: true, Content: "暂停" } &&
                    Button(manual, "RecordStop").IsEnabled, "resume did not reconcile the live controls");
                Snapshot(main, "recording-workspace-live.png");
                var (fullWidth, fullHeight, minWidth, minHeight) = (main.Width, main.Height, main.MinWidth, main.MinHeight);
                var root = (FrameworkElement)main.Content;
                var fullScale = root.LayoutTransform;
                main.MinWidth = main.MinHeight = 0;
                main.Width = 760; main.Height = 520;
                root.LayoutTransform = new ScaleTransform(.8, .8);
                main.UpdateLayout();
                var body = (ScrollViewer)manual.FindName("RecordingBodyScroll");
                body.ScrollToBottom(); main.UpdateLayout();
                foreach (string name in new[] { "RecordPause", "RecordStop" })
                {
                    var button = Button(manual, name);
                    Reachable(main, button);
                    var target = button.TransformToAncestor(main).TransformBounds(new Rect(button.RenderSize));
                    var at = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), main);
                    Assert(target.Height >= 43.99 && target.Width >= 43.99, $"{name} has a {target.Size} DIP target at compact density");
                    var hit = main.InputHitTest(at);
                    Assert(hit is DependencyObject visual && (ReferenceEquals(visual, button) || button.IsAncestorOf(visual)),
                        $"{name} centre hit {hit?.GetType().Name ?? "nothing"} outside the scrolled recording body");
                }
                Snapshot(main, "recording-workspace-live-760x520.png");
                main.Width = fullWidth; main.Height = fullHeight; main.MinWidth = minWidth; main.MinHeight = minHeight;
                root.LayoutTransform = fullScale;
                Checks.Add("Ready-to-use recording controls stay in the footer after scrolling at 760x520 and 80-percent content density, with 44-DIP hit targets");
                Click(main, "RecordingPlanTab");
                Click(plan, "ToggleReal");
                PumpUntil(() => automatic.Enabled && Button(plan, "RealSkip").IsVisible && Button(plan, "RealSkip").IsEnabled,
                    "confirmed upcoming course did not reveal skip action");
                CaptureRecordingPlanFirstScreen(main, plan, 1280, 700, "automatic-upcoming-1280-700.png");
                CaptureRecordingPlanFirstScreen(main, plan, 840, 600, "automatic-upcoming-840-600.png");
                Click(plan, "RealSkip");
                PumpUntil(() => requests.Any(r => r.Automatic?.Action == "skip-upcoming"), "skip-next action used the old stop-current command");
                Assert(state.Active && !requests.Any(r => r.Recording?.Action == "stop"), "skip upcoming ended active recording");
                PumpUntil(() => Button(plan, "ToggleReal").IsEnabled && !Button(plan, "RealSkip").IsVisible, "skip receipt did not retire stale action");
                Click(plan, "ToggleReal");
                PumpUntil(() => !automatic.Enabled && Equals(Button(plan, "ToggleReal").Content, "开启自动录课"), "disable did not settle before day edit");
                CheckRecordingDayOverride(main, plan, grid, plansPath);
                Assert(!automatic.Enabled && requests.Count(r => r.Automatic?.Action == "enable") == 1,
                    "saving daily arrangement enabled recording");
                Checks.Add("Embedded actual controllers/private pipe: synthetic probe, start/pause/resume, page navigation keeps session and shared client; day save does not enable, skip upcoming does not stop current");
                Snapshot(main, "recording-workspace-today.png");
                offline = true;
                PumpUntil(() => !Available(), "connection loss did not invalidate controls");
                Assert(!Button(plan, "StopCurrent").IsEnabled && ((TextBlock)plan.FindName("LatestRecording")).Text.Contains("未确认") && ((TextBlock)manual.FindName("RecordingHeading")).Text.Contains("未确认") && ((TextBlock)plan.FindName("RealStatus")).Text.Contains("未知"),
                    "disconnected workspace retains a confirmed-live claim or active stop command");
                offline = false;
                PumpUntil(Available, "fresh polling did not recover controls", 8);
                Click(main, "RecordingManualTab");
                Click(manual, "RecordStop");
                PumpUntil(() => state.Phase == "Saved" && !Button(manual, "RecordStop").IsVisible, "bound stop did not reconcile both views");
                Invoke("HomeNavClicked", main, new RoutedEventArgs());
                Click(main, "RecordingHomeButton");
                Assert(((Button)main.FindName("RecordingRecordsTab")).Tag as string == "active", "saved result did not route from the homepage to history");
                Assert(((TextBlock)plan.FindName("LatestRecordingPath")).Text.EndsWith("synthetic.mp4"), "records omitted recent manual result");
                Snapshot(main, "recording-workspace-records.png");
                foreach (var size in new[] { new Size(1280, 700), new Size(1024, 700), new Size(1024, 540), new Size(840, 580), new Size(760, 520) })
                {
                    main.MinWidth = main.MinHeight = 0; main.Width = size.Width; main.Height = size.Height;
                    ((FrameworkElement)main.Content).LayoutTransform = new ScaleTransform(.8, .8);
                    Click(main, "RecordingManualTab"); main.UpdateLayout();
                    Reachable(main, Button(manual, "RecordStart"));
                    Click(manual, "EditSettings"); main.UpdateLayout(); Reachable(main, Button(manual, "SaveSettings"));
                    Click(manual, "SaveSettings");
                    Click(main, "RecordingPlanTab"); main.UpdateLayout();
                    Reachable(main, Button(plan, "ToggleReal"));
                    var scroll = (ScrollViewer)plan.FindName("DayScroll");
                    Assert(scroll.ViewportHeight >= 100, "compact day page has no usable scroll viewport");
                    scroll.ScrollToBottom(); main.UpdateLayout();
                    var include = (Button)Find(planContent(plan), "AutoInclude"); include.BringIntoView(); main.UpdateLayout(); Reachable(main, include);
                    Snapshot(main, $"recording-workspace-{size.Width}-{size.Height}.png");
                }
                Assert(state.Phase == "Saved" && requests.Count(r => r.Recording?.Action == "start") == 1,
                    "compact layout/navigation restarted recording");
                Checks.Add("Logical 1280x700 through 760x520, including 1024x540 Windows-200-percent-sized viewport: start/save/master/skip/daily actions reachable; usable day scroll; no recording restart; physical touch acceptance pending");
                manual.Shutdown();
                ((ContentControl)main.FindName("RecordingManualHost")).Content = null;
                typeof(MainWindow).GetField("_recordingWindow", flags)!.SetValue(main, null);
                state = new("Recording", "隔离后台已有自动录课", Control: new("Automatic", Guid.NewGuid(), "fixture-current", 0, 0));
                PumpUntil(() => ((RecordingState)client.GetType().GetProperty("State")!.GetValue(client)!).Active, "active-entry state was not confirmed");
                int beforeProbe = probes;
                Click(main, "RecordingManualTab"); var activeEntry = Field<RecordingWindow>("_recordingWindow");
                main.UpdateLayout();
                Assert(probes == beforeProbe && Button(activeEntry, "RecordStop").IsVisible, "entering ongoing recording probed devices or lost session control");
                state = new("Saved", "隔离后台已有录课结束");
                PumpUntil(() => Button(activeEntry, "RecordStart").IsEnabled && probes == beforeProbe + 1, "active-entry page did not detect devices after recording ended");
                Checks.Add("Entering a preexisting active recording does not probe/reset inputs; after a fresh saved state, one deferred synthetic probe restores immediate start");
                main.Close();
            });
        }
        finally
        {
            main.Close(); client.GetType().GetMethod("Detach")!.Invoke(client, null);
            stop.Cancel(); server.GetAwaiter().GetResult();
            File.Delete(OnboardingStore.PathFor(pipe));
            foreach (string suffix in new[] { ".recording.json", ".recording-preview.json", ".recording-plans.json" })
            {
                string path = preferences.Replace(".startup.json", suffix, StringComparison.Ordinal);
                if (File.Exists(path)) File.Delete(path);
            }
            File.Delete(plansPath + ".tmp");
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
    private static void CaptureRecordingPlanFirstScreen(MainWindow main, AutoRecordingWindow plan, int width, int height, string file)
    {
        var original = (main.Width, main.Height);
        main.Width = width;
        main.Height = height;
        var day = (ScrollViewer)plan.FindName("DayScroll");
        day.ScrollToTop();
        main.UpdateLayout();
        var grid = (DataGrid)plan.FindName("Plans");
        PumpUntil(() => grid.ItemContainerGenerator.ContainerFromIndex(2) is DataGridRow,
            "three plan rows were not realized in the initial viewport");
        Reachable(main, (FrameworkElement)plan.FindName("PlanDate"));
        Reachable(main, Button(plan, "ToggleReal"));
        if (Button(plan, "RealSkip").IsVisible) Reachable(main, Button(plan, "RealSkip"));
        foreach (string id in new[] { "MinimizeWindow", "MaximizeRestoreWindow", "CloseWindow" })
            Reachable(main, (FrameworkElement)Find(main, id));
        Assert(Math.Abs(main.ActualWidth - width) < 1 && Math.Abs(main.ActualHeight - height) < 1 && day.VerticalOffset == 0,
            "the initial plan proof was not at the requested size and top of the page");
        var viewport = FlowElements<ScrollContentPresenter>(day).First();
        Snapshot(main, file);
        for (int i = 0; i < 3; i++)
        {
            var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(i);
            var bounds = row.TransformToAncestor(viewport).TransformBounds(new Rect(row.RenderSize));
            Assert(bounds.Top >= 0 && bounds.Bottom <= viewport.ActualHeight + 1,
                $"course row {i + 1} was clipped in the initial viewport at {width}x{height}: {bounds.Top:0}–{bounds.Bottom:0} out of {viewport.ActualHeight:0}");
        }
        Assert(!plan.IsVisible, "automatic plan opened a second native window");
        (main.Width, main.Height) = original;
        main.UpdateLayout();
    }

    private static void CheckRecordingSettingsRoundTrip(MainWindow main, AutoRecordingWindow plan, RecordingWindow manual, DataGrid grid, DateOnly date)
    {
        var original = (main.Width, main.Height);
        (main.Width, main.Height) = (840, 600);
        var settings = (Button)Find(planContent(plan), "AutoRecordingSettings");
        PumpUntil(() => Button(plan, "TomorrowButton").IsEnabled, "clock not ready for choosing tomorrow");
        Click(plan, "TomorrowButton");
        var datePicker = (DatePicker)plan.FindName("PlanDate");
        PumpUntil(() => grid.Items.Count == 4 && ((TextBlock)plan.FindName("DateStatus")).Text.Contains("到当天重新核对"), "tomorrow's forecast was not shown");
        var expectedDate = date.AddDays(1).ToDateTime(TimeOnly.MinValue);
        Assert(datePicker.SelectedDate == expectedDate, "test failed to choose tomorrow");
        grid.SelectedIndex = 2;
        var editor = (Expander)Find(planContent(plan), "AutoDatedEditor");
        editor.IsExpanded = true;
        var draft = (TextBox)plan.FindName("DatedName");
        draft.Text = "明天的临时草稿";
        var scroll = (ScrollViewer)plan.FindName("DayScroll");
        scroll.ScrollToBottom(); main.UpdateLayout();
        PumpUntil(() => scroll.VerticalOffset > 1, "test failed to scroll before changing recording settings");
        double offset = scroll.VerticalOffset;
        settings.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        var back = Button(manual, "ReturnRecordingSettings");
        Assert(back.IsVisible && Equals(back.Content, "返回自动安排") && !manual.IsVisible && !Button(manual, "RecordStart").IsVisible,
            "plan settings lost its same-window return or offered manual start");
        Click(manual, "SaveSettings");
        Assert(back.IsVisible && ((TextBlock)manual.FindName("RecordingPreferenceFeedback")).Text.Contains("已保存"),
            "saving recording sources lost its receipt or return to the plan");
        Click(manual, "ReturnRecordingSettings");
        PumpUntil(() => settings.IsKeyboardFocused && Math.Abs(scroll.VerticalOffset - offset) < 2,
            "return did not restore the origin focus and reading position");
        Assert(datePicker.SelectedDate == expectedDate && grid.SelectedIndex == 2 && editor.IsExpanded && draft.Text == "明天的临时草稿",
            "return reset the date, selected course or unsaved time slot");
        Snapshot(main, "automatic-settings-return-840-600.png");
        editor.IsExpanded = false;
        Click(plan, "TodayButton");
        PumpUntil(() => datePicker.SelectedDate == date.ToDateTime(TimeOnly.MinValue), "return test did not reset fixture date");
        (main.Width, main.Height) = original;
        main.UpdateLayout();
        Checks.Add("Actual same-window recording settings save and return preserves tomorrow, selected course, scroll offset, initiating focus and unsaved fixed time slot");
    }

    private static void CheckRecordingDayOverride(MainWindow main, AutoRecordingWindow plan, DataGrid grid, string plansPath)
    {
        grid.SelectedIndex = 1;
        main.UpdateLayout();
        var feedback = (TextBlock)plan.FindName("DayOverrideFeedback");
        var action = (Button)Find(planContent(plan), "AutoExclude");
        var bytes = File.ReadAllBytes(plansPath);
        using (new FileStream(plansPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            action.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert(feedback.Text.Contains("未应用修改") && grid.SelectedIndex == 1 && File.ReadAllBytes(plansPath).SequenceEqual(bytes),
                "locked plan did not show inline failure or disturbed the saved plan and selection");
        }
        action.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        var saved = new RecordingPlanBookStore(plansPath).Read().Overrides;
        Assert(feedback.Text.Contains("语文 · 已保存当天不录") && saved is [{ Number: 2, Mode: "Exclude" }],
            "retry did not save the selected course or show its inline receipt");
        feedback.BringIntoView();
        main.UpdateLayout();
        Reachable(main, feedback);
        Snapshot(main, "automatic-day-override.png");
        grid.SelectedIndex = 2;
        Assert(feedback.Text.Length == 0, "the previous course receipt followed the new selection");
        Checks.Add("Plan file lock fails in place and keeps the original chosen course; retry commits only that course and its feedback disappears when another course is selected");
    }

    private static void CheckRecordingPlanScroll(DataGrid plans)
    {
        var subjectMinimum = plans.Columns[0].MinWidth;
        plans.Columns[0].MinWidth = 1600;
        plans.BringIntoView();
        plans.UpdateLayout();
        var viewport = FlowElements<ScrollViewer>(plans).First();
        PumpUntil(() => viewport.ScrollableWidth >= 600, "the plan columns did not overflow");
        var bar = FlowElements<ScrollBar>(plans).Single(control => control.Orientation == Orientation.Horizontal && control.IsVisible);
        var track = (Track)bar.Template.FindName("PART_Track", bar);
        Assert(track.Orientation == Orientation.Horizontal && bar.ActualWidth > bar.ActualHeight,
            "widening a recording plan column produced a vertical track in the horizontal scrollbar");
        ((RoutedCommand)track.IncreaseRepeatButton.Command).Execute(null, track.IncreaseRepeatButton);
        PumpUntil(() => viewport.HorizontalOffset > 0, "the right-hand track did not reveal the remaining columns");
        ((RoutedCommand)track.DecreaseRepeatButton.Command).Execute(null, track.DecreaseRepeatButton);
        PumpUntil(() => viewport.HorizontalOffset == 0, "the left-hand track did not return to the first column");
        plans.Columns[0].MinWidth = subjectMinimum;
        plans.UpdateLayout();
        Checks.Add("Wide recording plan columns can be paged horizontally in both directions");
    }
    private static DependencyObject planContent(Window controller) => (DependencyObject)controller.GetType().Assembly
        .GetType("NPEduTools.App.WorkspacePanel")!.GetMethod("View", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [controller])!;
    private static void Reachable(Window main, FrameworkElement element)
    {
        var point = element.TranslatePoint(new Point(), main);
        var end = element.TranslatePoint(new Point(element.ActualWidth, element.ActualHeight), main);
        Assert(element.IsVisible && element.ActualHeight > 0 && point.X >= 0 && point.Y >= 0 && end.X <= main.ActualWidth + 1 && end.Y <= main.ActualHeight + 1,
            element.Name + " clipped at " + main.Width + "x" + main.Height);
    }
}

