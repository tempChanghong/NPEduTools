using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Host;

internal static partial class Program
{
    private static void RunQuickCalibrationWorkspaceChecks()
    {
        const string secret = "isolated-calibration";
        const string mic = "fixture-mic";
        const string nextMic = "fixture-spare";
        const string scope = "isolated-school-calibration";
        string pipe = "NPEduTools.Test.quick-calibration-workspace." + Guid.NewGuid().ToString("N");
        string directory = Path.Combine(_output, "quick-calibration-host");
        var clock = new CalibrationUiClock();
        var management = new NoiseManagementStore(directory, clock);
        Assert(management.Configure(null, secret, false) is null, "could not configure isolated credential");
        CalibrationUiCapture? capture = null;
        var study = StudyFixture("Outside");
        var noise = new NoiseService(_ => capture = new(), () => [new(mic, "隔离模拟麦克风"), new(nextMic, "备用模拟麦克风")],
            clock, directory, management);
        SelectMicrophone(mic);
        var commands = new ConcurrentQueue<NoiseCalibrationCommand>();
        int reads = 0, dropReads = 0, dropReply = 0;
        var verificationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishOldVerification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verificationFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var server = Task.Run(() => WorkspaceProxy(pipe, async request =>
        {
            if (request.Capability == "noise.management.status") return noise.ManagementHandle(request);
            if (request.NoiseManagement?.Action == "verify")
            {
                verificationStarted.TrySetResult();
                await finishOldVerification.Task.WaitAsync(stop.Token);
                verificationFinished.TrySetResult();
                throw new IOException("fixture dropped the superseded verification reply");
            }
            if (request.Capability == "noise.calibration.status")
            {
                Interlocked.Increment(ref reads);
                if (Volatile.Read(ref dropReads) != 0) throw new IOException("fixture dropped the status connection");
                return noise.CalibrationHandle(request, scope, study);
            }
            if (request.NoiseCalibration is { } command)
            {
                commands.Enqueue(command);
                var response = noise.CalibrationHandle(request, scope, study);
                if (command.Action == "start" && Interlocked.Exchange(ref dropReply, 0) == 1)
                    throw new IOException("fixture dropped the response after Host accepted the sample");
                return response;
            }
            return new HostResponse(1, request.RequestId, "Succeeded", null, "", Study: study);
        }, stop.Token));
        var main = PrivateWorkspace(pipe);
        try
        {
            Exercise(main, _ =>
            {
                Click(main, "StudyNav");
                Click(main, "SchoolWorkspaceTab");
                var school = WorkspaceField<StudyMonitoringWindow>(main, "_studyMonitoringWindow");
                var entry = Button(school, "QuickCalibrationButton");
                var hwnd = new WindowInteropHelper(main).Handle;
                Click(school, "QuickCalibrationButton");
                var page = WorkspaceField<NoiseCalibrationWindow>(main, "_workspaceQuickCalibrationController");
                var scroll = Field<ScrollViewer>("CalibrationScroll");
                var credentials = Field<Expander>("CredentialsExpander");
                var password = Field<PasswordBox>("ManagementSecret");
                var scene = Field<ComboBox>("SceneChoice");
                var label = Field<ComboBox>("LabelChoice");
                var start = Button(page, "StartSample");
                var cancel = Button(page, "CancelSample");
                var draft = Button(page, "BeginDraft");
                var medium = Field<TextBox>("MediumThreshold");
                var high = Field<TextBox>("HighThreshold");
                var confirm = Button(page, "ConfirmThresholds");
                var suggested = Field<TextBlock>("SuggestedValues");
                var applied = Field<TextBlock>("AppliedValues");
                var sampleList = Field<ListBox>("SampleList");
                var sampleDetails = Field<Expander>("SampleDetailsExpander");
                PumpUntil(() => draft.IsVisible && draft.IsEnabled, "quick calibration did not load through the original school entry");
                Assert(!page.IsVisible && new WindowInteropHelper(page).Handle == nint.Zero && new WindowInteropHelper(main).Handle == hwnd,
                    "quick calibration opened a second native window");
                foreach (string id in new[] { "MinimizeWindow", "MaximizeRestoreWindow", "CloseWindow" })
                    Assert(Button(main, id, automation: true).IsVisible, "missing main caption control: " + id);
                CaptureSize(840, 600, "initial-narrow", draft);
                Assert(!credentials.IsExpanded && !sampleDetails.IsExpanded && !confirm.IsVisible, "the narrow page should start in sampling with less-used areas closed");
                Click(page, "BeginDraft");
                Assert(credentials.IsExpanded && password.IsKeyboardFocused && commands.IsEmpty, "attempt without a credential did not reveal the inline input or started a write");
                CaptureSize(840, 600, "credential-narrow", password, draft);
                password.Password = secret;
                Click(page, "BeginDraft");
                PumpUntil(() => start.IsEnabled, "Host did not open the draft");
                Guid draftId = Status().DraftId!.Value;
                credentials.IsExpanded = false;
                Checks.Add("Original school entry uses the same main HWND and caption buttons. The narrow first screen has a draft action and reveals the inline credential on demand.");

                scene.SelectedValue = "NormalStudy";
                label.SelectedValue = "Acceptable";
                Click(page, "StartSample");
                PumpUntil(() => capture is { Started: true, Disposed: false } && cancel.IsEnabled, "the first bounded Host sample did not start");
                CaptureSize(840, 600, "sampling-active-narrow", scene, label, cancel, Field<ProgressBar>("SamplingProgress"), Field<TextBlock>("SamplingStatus"));
                Return();
                Assert(password.Password.Length == 0 && Status() is { State: "Sampling" } current && current.DraftId == draftId,
                    "return kept the credential, stopped the sample, or changed the draft");
                Finish(-55);
                var first = Status().Samples.Single();
                Assert(first is { Label: "Acceptable", Quality: "Good" }, "the first Host segment was invalid");
                Open();
                PumpUntil(() => start.IsEnabled && sampleList.Items.Count == 1, "return did not find the recorded sample");
                Assert(Status().DraftId == draftId && Status().Samples.Single().Id == first.Id &&
                    !Field<TextBlock>("OperationMessage").Text.Contains("正在采样"), "reopened the wrong draft, sample or old progress");
                Checks.Add("Leaving while sampling returns focus to the original entry and clears the password. The bounded Host capture finishes and reopening reads the same draft and sample IDs.");

                password.Password = secret;
                scene.SelectedValue = "SustainedConversation";
                label.SelectedValue = "Discipline";
                Volatile.Write(ref dropReply, 1);
                Click(page, "StartSample");
                PumpUntil(() => capture is { Started: true, Disposed: false } && commands.Count(c => c.Action == "start") == 2,
                    "Host did not accept the second sample");
                Volatile.Write(ref dropReads, 1);
                PumpUntil(() => Field<TextBlock>("OperationMessage").Text.Contains("未确认") && !cancel.IsEnabled,
                    "lost reply did not mark the outcome uncertain and disable writes");
                Finish(-20);
                var originalSamples = Status().Samples.Select(s => s.Id).ToArray();
                Volatile.Write(ref dropReads, 0);
                PumpUntil(() => start.IsEnabled && sampleList.Items.Count == 2, "the original sample was lost after reconnection");
                Assert(Status().DraftId == draftId && originalSamples.SequenceEqual(Status().Samples.Select(s => s.Id)) &&
                    commands.Count(c => c.Action == "start") == 2 && originalSamples[0] == first.Id,
                    "recovery changed the draft or samples, or automatically sent start again");
                CaptureSize(840, 600, "sampling-ready-narrow", scene, label, start, Field<TextBlock>("SampleCoverage"), sampleDetails);
                Click(page, "ThresholdTab");
                PumpUntil(() => confirm.IsVisible && suggested.Text.Contains("dBFS"), "the narrow review did not open with actual suggested values");
                Assert(!start.IsVisible && !applied.Text.Contains("dBFS"), "narrow view mixes stages or suggests the threshold is already applied");
                Click(page, "UseSuggestion");
                string priorMedium = medium.Text, priorHigh = high.Text;
                Assert(priorMedium.Length > 0 && priorHigh.Length > 0 && commands.All(c => c.Action != "confirm"),
                    "filling the suggestion either did not fill both inputs or submitted a write");
                CaptureSize(840, 600, "thresholds-narrow", applied, suggested, medium, high, confirm);
                CaptureSize(1040, 760, "thresholds-and-sampling-wide", start, Field<TextBlock>("SampleCoverage"), applied, suggested, medium, high, confirm);
                Assert(Grid.GetColumn(Field<Border>("ThresholdPane")) == 1 && !Field<UniformGrid>("CompactTabs").IsVisible,
                    "the wide page did not use two columns");
                CaptureSize(840, 600, "review-narrow-after-resize", medium, high, confirm);
                Assert(medium.Text == priorMedium && high.Text == priorHigh, "resizing lost pending inputs");
                Checks.Add("At 150% scaling, each 840×600 task fits its real viewport; at 1040×760 both panes stay visible. Filling a suggestion changes only the editor.");

                int readStart = Volatile.Read(ref reads);
                Volatile.Write(ref dropReads, 1);
                PumpUntil(() => Volatile.Read(ref reads) > readStart && !confirm.IsEnabled, "offline status did not suspend saving");
                Return();
                Open();
                Assert(medium.Text == priorMedium && high.Text == priorHigh && !confirm.IsEnabled, "reopening offline discarded input or allowed a stale write");
                Volatile.Write(ref dropReads, 0);
                PumpUntil(() => confirm.IsEnabled && medium.Text == priorMedium, "a fresh read did not restore the pending fields");
                Assert(high.Text == priorHigh && Status().DraftId == draftId &&
                    originalSamples.SequenceEqual(Status().Samples.Select(s => s.Id)), "recovery selected a different draft or samples");
                Click(page, "ConfirmThresholds");
                Assert(credentials.IsExpanded && password.IsKeyboardFocused && commands.Count == 3,
                    "saving after return did not ask for the credential inline before submitting");
                CaptureSize(840, 600, "thresholds-credential-narrow", password, medium, high, confirm);
                password.Password = secret;
                credentials.IsExpanded = false;
                Click(page, "ConfirmThresholds");
                PumpUntil(() => applied.Text.Contains("dBFS") && Status().Applied && medium.Text.Length == 0 && high.Text.Length == 0,
                    "confirmation did not apply the profile and clear the pending state");
                Assert(commands.Count(c => c.Action == "confirm" && c.DraftId == draftId) == 1, "confirmation did not target the original draft exactly once");
                CaptureSize(840, 600, "saved-narrow", applied, suggested, medium, high, confirm);
                Checks.Add("Dropping the accepted sample reply and later status reads preserves results and unsaved thresholds. Recovery is read-only until explicit confirmation on the original draft.");

                string settled = Field<TextBlock>("OperationMessage").Text;
                Click(page, "RefreshStatus");
                PumpUntil(() => verificationStarted.Task.IsCompleted && !confirm.IsEnabled, "explicit verification never started");
                Return();
                Open();
                PumpUntil(() => confirm.IsEnabled && !finishOldVerification.Task.IsCompleted,
                    "a verification response from the previous visit blocked the fresh read", 2);
                finishOldVerification.TrySetResult();
                PumpUntil(() => verificationFinished.Task.IsCompleted, "old fixture verification never finished");
                Assert(Field<TextBlock>("OperationMessage").Text == settled && password.Password.Length == 0,
                    "old verification replaced the current message or retained the credential");
                Checks.Add("Returning while credential verification is still outstanding cancels that read. Its late failure cannot replace the current result or delay reopening.");

                Return();
                SelectMicrophone(nextMic);
                Open();
                PumpUntil(() => medium.Text.Length == 0 && !confirm.IsEnabled && sampleList.Items.Count == 0,
                    "selecting another microphone exposed stale inputs or samples on a different target");
                Assert(commands.All(c => c.Action != "cancel") && commands.Count == 4, "page navigation wrote or replayed a calibration action");
                Checks.Add("Switching microphones clears old inputs and prevents the prior draft or sample evidence from being used for the new target.");
                main.Close();

                T Field<T>(string name) where T : FrameworkElement => (T)page.FindName(name);
                void Open() => Click(school, "QuickCalibrationButton");
                void Return()
                {
                    Click(main, "WorkspaceQuickCalibrationBack");
                    PumpUntil(() => entry.IsVisible && Keyboard.FocusedElement == entry, "back did not refocus the original quick calibration action");
                }
                void CaptureSize(int width, int height, string filename, params FrameworkElement[] controls)
                {
                    main.Width = width;
                    main.Height = height;
                    scroll.ScrollToTop();
                    main.UpdateLayout();
                    PumpUntil(() => Math.Abs(main.ActualWidth - width) < 2 && Math.Abs(main.ActualHeight - height) < 2,
                        "window did not adopt the requested viewport size");
                    Snapshot(main, filename + ".png");
                    foreach (var control in controls)
                    {
                        var top = control.TranslatePoint(new Point(), scroll);
                        var bottom = control.TranslatePoint(new Point(control.ActualWidth, control.ActualHeight), scroll);
                        Assert(control.IsVisible && control.ActualHeight > 0 && top.X >= -1 && top.Y >= -1 &&
                            bottom.X <= scroll.ViewportWidth + 1 && bottom.Y <= scroll.ViewportHeight + 1,
                            $"{filename}: {control.Name} [{top.X:F0},{top.Y:F0}–{bottom.X:F0},{bottom.Y:F0}] vs {scroll.ViewportWidth:F0}×{scroll.ViewportHeight:F0} at {VisualTreeHelper.GetDpi(main).DpiScaleX:P0}");
                    }
                }
            });
        }
        finally
        {
            stop.Cancel();
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            Task.Run(async () => await noise.DisposeAsync()).GetAwaiter().GetResult();
        }

        NoiseCalibrationState Status() => noise.CalibrationHandle(new(1, Guid.NewGuid(), "noise.calibration.status"), scope, study).NoiseCalibration!;
        void SelectMicrophone(string id)
        {
            var state = noise.Snapshot();
            var reply = noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("select", state.InstanceId, state.Revision, id)));
            Assert(reply.Outcome == "Succeeded", "could not select fixture microphone");
        }
        void Finish(double dbfs)
        {
            for (int i = 0; i < 150; i++) { clock.Ms += 100; capture!.Emit(dbfs); }
            PumpUntil(() => capture is { Disposed: true } && Status().State == "Ready", "Host did not finish the bounded sample");
        }
    }
}
