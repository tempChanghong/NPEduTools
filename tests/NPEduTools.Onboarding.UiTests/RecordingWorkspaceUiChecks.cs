using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunRecordingWorkspaceChecks()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        string pipe = "NPEduTools.Test.recording-workspace." + Guid.NewGuid().ToString("N");
        string preferences = StartupPreferencesStore.PathFor(pipe);
        using var stop = new CancellationTokenSource();
        var requests = new ConcurrentQueue<HostRequest>();
        RecordingState state = new("Idle", "隔离后台准备录制");
        AutomaticRecordingState automatic = new(false, Guid.Empty, "隔离后台：自动录课未开启", null, []);
        Guid bridge = Guid.NewGuid(), connection = Guid.NewGuid(), profile = Guid.NewGuid(), firstSubject = Guid.NewGuid(), secondSubject = Guid.NewGuid();
        var date = new DateOnly(2026, 10, 13);
        var start = new DateTimeOffset(date.ToDateTime(new TimeOnly(7, 59)), TimeSpan.Zero);
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
                    requests.Enqueue(request);
                    Assert(Protocol.Validate(request) is null, "workspace sent invalid protocol parameters");
                    if (offline) continue;
                    SchoolClockFrame? frame = null;
                    if (request.Capability == "classisland.school-clock")
                    {
                        var now = start.Add(elapsed.Elapsed);
                        var schedule = new DaySchedule(profile, profile, profile, "隔离课表", date, "fixture-1", now, true, true, "模拟学校时间",
                            [new(1, firstSubject, "周测", start.AddMinutes(-29), start.AddMinutes(1), true),
                             new(2, secondSubject, "语文", start.AddMinutes(1), start.AddMinutes(46), true)]);
                        frame = new(connection, bridge, ++sequence, 0, now, 0, "Advancing", "隔离学校时钟", schedule);
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
                        if (autoCommand.Action == "skip-upcoming") automatic = automatic with { Message = "隔离后台已跳过下一节" };
                    }
                    await Protocol.WriteAsync(channel, new HostResponse(Protocol.Version, request.RequestId, "Succeeded", null,
                        "隔离回执", Recording: state, Automatic: automatic, SchoolClock: frame), stop.Token);
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
                Click(main, "RecordingManualTab");
                var manual = Field<RecordingWindow>("_recordingWindow");
                PumpUntil(() => Button(manual, "RecordStart").IsEnabled, "embedded device probe did not populate options");
                Assert(probes == 1 && !manual.IsVisible && ((FrameworkElement)main.FindName("RecordingWorkspace")).IsVisible,
                    "recording opened another window or did not load embedded controller");
                Snapshot(main, "recording-workspace-immediate.png");
                Click(manual, "RecordStart");
                PumpUntil(() => state.Active && Button(manual, "RecordStop").IsEnabled, "synthetic recording did not start");
                Assert(((FrameworkElement)manual.FindName("RecordingSettings")).Visibility == Visibility.Collapsed,
                    "live recording still displays setup form");
                int mutations = requests.Count(r => r.Recording is not null || r.Automatic?.Action is not (null or "lease"));
                Click(main, "RecordingPlanTab");
                var plan = Field<AutoRecordingWindow>("_autoRecordingWindow");
                PumpUntil(() => ((DataGrid)plan.FindName("Plans")).Items.Count == 2, "embedded automatic page did not load school schedule");
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
                Click(manual, "RecordPause"); PumpUntil(() => state.Phase == "Recording", "resume did not continue session");
                Snapshot(main, "recording-workspace-live.png");
                Click(main, "RecordingPlanTab");
                Click(plan, "RealSkip");
                PumpUntil(() => requests.Any(r => r.Automatic?.Action == "skip-upcoming"), "skip-next action used the old stop-current command");
                Assert(state.Active && !requests.Any(r => r.Recording?.Action == "stop"), "skip upcoming ended active recording");
                var grid = (DataGrid)plan.FindName("Plans"); grid.SelectedItem = grid.Items[1];
                ((Button)Find(planContent(plan), "AutoExclude")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert(!automatic.Enabled && File.Exists(preferences.Replace(".startup.json", ".recording-plans.json", StringComparison.Ordinal)),
                    "saving daily arrangement enabled recording or was not persisted");
                Checks.Add("Embedded actual controllers/private pipe: synthetic probe, start/pause/resume, page navigation keeps session and shared client; day save does not enable, skip upcoming does not stop current");
                Snapshot(main, "recording-workspace-today.png");
                offline = true;
                PumpUntil(() => !Available(), "connection loss did not invalidate controls");
                Assert(!Button(plan, "StopCurrent").IsEnabled && ((TextBlock)plan.FindName("LatestRecording")).Text.Contains("未确认") && ((TextBlock)manual.FindName("RecordingHeading")).Text.Contains("未确认") && ((TextBlock)plan.FindName("RealStatus")).Text.Contains("未知"),
                    "disconnected workspace retains a confirmed-live claim or active stop command");
                offline = false;
                PumpUntil(Available, "fresh polling did not recover controls", 8);
                Click(plan, "StopCurrent");
                PumpUntil(() => state.Phase == "Saved" && !Button(manual, "RecordStop").IsVisible, "bound stop did not reconcile both views");
                Click(main, "RecordingRecordsTab");
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
                    Reachable(main, Button(plan, "RealSkip")); Reachable(main, Button(plan, "ToggleReal"));
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
            foreach (string suffix in new[] { ".recording.json", ".recording-preview.json", ".recording-plans.json" })
            {
                string path = preferences.Replace(".startup.json", suffix, StringComparison.Ordinal);
                if (File.Exists(path)) File.Delete(path);
            }
            SynchronizationContext.SetSynchronizationContext(previous);
        }
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

