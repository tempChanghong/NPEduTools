using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

internal static partial class Program
{
    private sealed class ManualReferenceFixtureCapture : IReferenceNoiseCapture
    {
        public string DeviceName => "隔离模拟麦克风";
        public string Signature => ReferenceNoiseMeter.AlgorithmVersion + "|48000|1|32|gain=.7|mute=False";
        public volatile bool Streaming;
        public event Action<NoiseFrame>? Frame { add { } remove { } }
        public event Action<ReferenceNoiseFrame>? ReferenceFrame;
        public event Action<string>? Failed { add { } remove { } }
        public void Start() => Streaming = true;
        public void Dispose() => Streaming = false;
        internal void Send(double dbfs, bool settled)
        {
            double energy = Math.Pow(10, dbfs / 10);
            ReferenceFrame?.Invoke(new(.1, energy, energy, energy, .1, 0, false, settled));
        }
    }

    private static void RunManualReferenceWorkspaceChecks()
    {
        const string secret = "synthetic-only";
        const string mic = "fixture-mic";
        string folder = Path.Combine(_output, "manual-reference-host");
        string pipe = "NPEduTools.Test.manual-reference-workspace." + Guid.NewGuid().ToString("N");
        var clock = new CalibrationUiClock();
        var management = new NoiseManagementStore(folder, clock);
        Assert(management.Configure(null, secret, false) is null, "fixture management setup failed");
        ManualReferenceFixtureCapture? capture = null;
        int formalCaptures = 0, reports = 0, dropRead = 0, dropReadingReply = 0, dropStartReply = 0;
        var commands = new ConcurrentQueue<NoiseReferenceCommand>();
        var noise = new NoiseService(_ =>
        {
            Interlocked.Increment(ref formalCaptures);
            throw new InvalidOperationException("this fixture never opens formal input");
        }, () => [new(mic, "隔离模拟麦克风")], clock, folder, management,
            referenceCreate: _ => capture = new());
        noise.Completed += _ => Interlocked.Increment(ref reports);
        var initial = noise.Snapshot();
        Assert(noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("select", initial.InstanceId, initial.Revision, mic)))
            .Outcome == "Succeeded", "fixture microphone selection failed");
        using var stop = new CancellationTokenSource();
        var server = Task.Run(() => WorkspaceProxy(pipe, request =>
        {
            if (request.Capability is "noise.management.status" or "noise.management.verify")
                return Task.FromResult(noise.ManagementHandle(request));
            if (request.Capability == "noise.reference.status" && Volatile.Read(ref dropRead) != 0)
                throw new IOException("fixture dropped the status connection");
            if (request.Capability is "noise.reference.status" or "noise.reference.command")
            {
                if (request.NoiseReference is { } command) commands.Enqueue(command);
                var reply = noise.ReferenceHandle(request);
                if (request.NoiseReference?.Action == "start" && Interlocked.Exchange(ref dropStartReply, 0) == 1)
                    throw new IOException("fixture dropped the reply after sampling started");
                if (request.NoiseReference?.Action == "reference" && Interlocked.Exchange(ref dropReadingReply, 0) == 1)
                    throw new IOException("fixture dropped the reply after the reading was saved");
                return Task.FromResult(reply);
            }
            return Task.FromResult(new HostResponse(1, request.RequestId, "Succeeded", null, "",
                Study: StudyFixture("Outside")));
        }, stop.Token));

        NoiseReferenceState Status() => noise.ReferenceHandle(new(1, Guid.NewGuid(), "noise.reference.status")).NoiseReference!;
        var main = PrivateWorkspace(pipe);
        try
        {
            Exercise(main, owner =>
            {
                var hwnd = new WindowInteropHelper(owner).Handle;
                Click(main, "StudyNav");
                Click(main, "SchoolWorkspaceTab");
                var school = WorkspaceField<StudyMonitoringWindow>(main, "_studyMonitoringWindow");
                var original = Button(school, "ManualReferenceButton");
                PumpUntil(() => original.IsVisible && original.IsEnabled, "manual reference entry was missing");
                Click(school, "ManualReferenceButton");
                var page = WorkspaceField<NoiseReferenceWindow>(main, "_workspaceManualReferenceController");
                var credential = Field<PasswordBox>("ManagementSecret");
                var stepOne = Button(page, "BeginDraft");
                var role = Field<ComboBox>("RoleChoice");
                var reading = Field<TextBox>("ReferenceValue");
                var sameWindow = Field<CheckBox>("SameWindow");
                var saveReading = Button(page, "AttachReference");
                var saveProfile = Button(page, "ConfirmProfile");
                var scopedTarget = Field<TextBlock>("ManualSegmentTarget");
                var health = Field<TextBlock>("ReferenceHealth");
                var diagnostics = Field<Expander>("SampleDetails");
                var optional = Field<Expander>("OptionalRangeInputs");
                var minimum = Field<TextBox>("ReferenceMinimum");
                var maximum = Field<TextBox>("ReferenceMaximum");
                var scroll = Field<ScrollViewer>("ReferencePageScroll");
                PumpUntil(() => stepOne.IsEnabled && stepOne.IsVisible, "embedded manual reference did not read Host status");
                Assert(!page.IsVisible && new WindowInteropHelper(page).Handle == nint.Zero && new WindowInteropHelper(owner).Handle == hwnd,
                    "the school entry opened a second native window");
                foreach (string id in new[] { "MinimizeWindow", "MaximizeRestoreWindow", "CloseWindow" })
                    Assert(Button(main, id, automation: true).IsVisible, "main window caption button missing: " + id);
                Assert(!Button(page, "StartSample").IsVisible && !saveReading.IsVisible && !saveProfile.IsVisible,
                    "preparation shows actions for later stages");
                CaptureSize(1040, 760, "prepare-wide", credential, Field<ComboBox>("TimeMode"), Field<TextBox>("Stability"), Field<TextBox>("CheckTolerance"), stepOne);
                CaptureSize(840, 600, "prepare-narrow", credential, Field<ComboBox>("TimeMode"), Field<TextBox>("Stability"), Field<TextBox>("CheckTolerance"), stepOne);
                Field<TextBox>("Stability").Text = Field<TextBox>("CheckTolerance").Text = "1";
                credential.Password = secret;
                Click(page, "BeginDraft");
                PumpUntil(() => Button(page, "StartSample").IsVisible && Button(page, "StartSample").IsEnabled, "created draft did not open sampling");
                var draftId = Status().Draft!.Id;
                Assert(commands.Single().Action == "begin", "unexpected extra write");
                Checks.Add("Original school action reuses the main HWND and three caption controls; both viewport sizes include the mandatory setup controls and current action");

                Click(page, "StartSample");
                PumpUntil(() => capture is { Streaming: true } && Button(page, "CancelSample").IsVisible, "first reference segment did not start");
                CaptureSize(840, 600, "sampling-narrow", role, Field<TextBlock>("SamplingStatus"), Button(page, "CancelSample"));
                Return();
                Assert(credential.Password.Length == 0 && capture is { Streaming: true } && Status().Draft!.Id == draftId,
                    "return retained a password or interrupted the Host's bounded manual segment");
                FinishSegment(-40);
                var first = Status().Draft!.Samples.Single();
                Assert(first.Role == "Primary" && first.Quality == "Good", "first real Host segment outcome was not valid");
                Click(school, "ManualReferenceButton");
                PumpUntil(() => scopedTarget.IsVisible && scopedTarget.Text.Contains("待填读数") && reading.IsEnabled, "reopened page did not recover the actual first segment");
                Assert(commands.Select(c => c.Action).SequenceEqual(new[] { "begin", "start" }) && !saveReading.IsEnabled,
                    "reopening wrote to the Host or reused a credential");
                Click(page, "SetupStep");
                credential.Password = secret;
                Assert(Button(page, "ContinueDraft").IsVisible && Button(page, "ContinueDraft").IsEnabled, "existing draft did not offer continue");
                Click(page, "ContinueDraft");
                PumpUntil(() => reading.IsVisible && reading.IsEnabled, "continued draft did not restore the reading field");
                Checks.Add("Returning during real Host sampling restores original focus; reopening uses the persisted draft and sample ID with zero replay and requires credential reentry");

                reading.Text = "50"; sameWindow.IsChecked = true;
                optional.IsExpanded = true;
                PumpUntil(() => minimum.IsVisible && maximum.IsVisible, "optional range could not be opened");
                minimum.Text = 49.5.ToString(CultureInfo.CurrentCulture);
                maximum.Text = 50.5.ToString(CultureInfo.CurrentCulture);
                optional.IsExpanded = false;
                Volatile.Write(ref dropRead, 1);
                PumpUntil(() => health.Text.Contains("待确认") && !saveReading.IsEnabled, "connection loss left the former reading writable");
                Assert(reading.Text == "50" && sameWindow.IsChecked == true, "read-only outage discarded pending input");
                Volatile.Write(ref dropRead, 0);
                PumpUntil(() => saveReading.IsEnabled, "reconnect did not verify the same segment again");
                Assert(reading.Text == "50" && sameWindow.IsChecked == true && Status().Draft!.Samples.Single().Id == first.Id,
                    "reconnect changed the first segment or discarded its input");
                Volatile.Write(ref dropReadingReply, 1);
                Click(page, "AttachReference");
                PumpUntil(() => scopedTarget.Text.Contains("已记录") && reading.IsEnabled && !saveReading.IsEnabled,
                    "lost save reply did not recover the saved value without offering an identical write");
                Assert(Status().Draft!.Samples.Single() is
                    { ReferenceDbA: 50, ReferenceMinimumDbA: 49.5, ReferenceMaximumDbA: 50.5, SameWindowConfirmed: true } &&
                    commands.Count(c => c.Action == "reference") == 1, "reconciliation replayed a reading or saved to the wrong sample");
                Checks.Add("Disconnected reads suspend writes, unsaved input stays with the exact sample, and a dropped save reply reconciles from the Host without resending");

                role.SelectedValue = "Check";
                Assert(reading.Text.Length == 0 && minimum.Text.Length == 0 && maximum.Text.Length == 0 &&
                    !saveReading.IsVisible && Button(page, "StartSample").IsEnabled,
                    "first segment input leaked into the second segment");
                Volatile.Write(ref dropStartReply, 1);
                Click(page, "StartSample");
                PumpUntil(() => capture is { Streaming: true } && Button(page, "CancelSample").IsVisible, "reconnect did not recover the accepted second segment");
                Assert(commands.Count(c => c.Action == "start") == 2, "the accepted start was retransmitted");
                FinishSegment(-30);
                var second = Status().Draft!.Samples.Single(s => s.Role == "Check");
                PumpUntil(() => scopedTarget.Text.Contains("复核电平") && reading.IsVisible && reading.IsEnabled, "second segment did not become the reading target");
                reading.Text = "60"; sameWindow.IsChecked = true;
                role.SelectedValue = "Primary";
                Assert(reading.Text == "50" && scopedTarget.Text.Contains("第一电平"), "role switch did not recover the original saved reading");
                role.SelectedValue = "Check";
                Assert(reading.Text == "60" && sameWindow.IsChecked == true && !diagnostics.IsExpanded,
                    "role switch discarded the unsaved reading or required opening diagnostics");
                CaptureSize(1040, 760, "reading-wide", role, scopedTarget, reading, sameWindow, saveReading);
                Assert(Grid.GetColumn(Field<Border>("ReferenceSampleReading")) == 1, "wide reading does not use the second column");
                CaptureSize(840, 600, "reading-narrow", role, scopedTarget, reading, sameWindow, Field<Expander>("OptionalRangeInputs"), saveReading);
                Assert(Grid.GetRow(Field<Border>("ReferenceSampleReading")) == 1, "narrow reading does not follow the segment");
                Click(page, "AttachReference");
                PumpUntil(() => saveProfile.IsVisible && saveProfile.IsEnabled, "both saved readings did not advance to review");
                CaptureSize(840, 600, "review-narrow", Field<TextBlock>("EvaluationStatus"), saveProfile);
                CaptureSize(1040, 760, "review-wide", Field<TextBlock>("EvaluationStatus"), saveProfile);
                var pairs = commands.Where(c => c.Action == "reference").ToArray();
                Assert(pairs.Length == 2 && pairs[0].DraftId == draftId && pairs[0].SampleId == first.Id &&
                    pairs[0].ReferenceDbA == 50 && pairs[1].DraftId == draftId && pairs[1].SampleId == second.Id &&
                    pairs[1].ReferenceDbA == 60 && pairs.All(c => c.SameWindowConfirmed),
                    "readings did not reach the original ordered sample IDs");
                Click(page, "ConfirmProfile");
                PumpUntil(() => Status().Profile is { Method: "ManualReference" }, "real Host did not confirm an estimate");
                Assert(formalCaptures == 0 && reports == 0 && commands.All(c => c.Action != "cancel"),
                    "manual navigation opened, reported or sent a stop for formal monitoring");
                Checks.Add("At 840×600 the current inputs and actions are in view for prepare, sampling, reading and review; two exact sample IDs reach Host, saved as a manual estimate only");

                Return();
                var previous = Status();
                var replacement = new NoiseReferenceCommand("begin", previous.InstanceId, previous.Revision, secret,
                    Instrument: "replacement", TimeWeighting: "Slow", StabilityDb: 1, CheckToleranceDb: 1, Workflow: "Stable");
                Assert(noise.ReferenceHandle(new(1, Guid.NewGuid(), "noise.reference.command",
                    NoiseReference: replacement)).Outcome == "Succeeded", "fixture could not replace draft");
                Click(school, "ManualReferenceButton");
                PumpUntil(() => stepOne.IsVisible && Field<TextBlock>("DraftStatus").Text.Contains("replacement"), "a newer draft did not return the page to preparation");
                Assert(reading.Text.Length == 0 && !saveProfile.IsVisible && Status().Draft is { Samples.Count: 0 } newer && newer.Id != draftId,
                    "different draft reused old readings or a confirmation button");
                Checks.Add("Replacing the Host draft while away never reattaches old readings or confirmation to the new draft");
                main.Close();

                T Field<T>(string name) where T : FrameworkElement => (T)page.FindName(name);
                void Return()
                {
                    Click(main, "WorkspaceManualReferenceBack");
                    PumpUntil(() => original.IsVisible && Keyboard.FocusedElement == original, "return failed to focus the original entry");
                }
                void FinishSegment(double dbfs)
                {
                    var current = capture!;
                    for (int i = 0; i < 150; i++) { clock.Ms += 100; current.Send(dbfs, i >= 50); }
                    PumpUntil(() => !current.Streaming && Status().State == "Ready", "Host did not finish the bounded sample");
                }
                void CaptureSize(int width, int height, string file, params FrameworkElement[] required)
                {
                    main.Width = width;
                    main.Height = height;
                    scroll.ScrollToTop();
                    main.UpdateLayout();
                    PumpUntil(() => Math.Abs(main.ActualWidth - width) < 2 && Math.Abs(main.ActualHeight - height) < 2, "wrong measured window size");
                    Snapshot(main, file + ".png");
                    foreach (var element in required)
                    {
                        var viewport = scroll.IsAncestorOf(element) ? (FrameworkElement)scroll : main;
                        var top = element.TranslatePoint(new Point(), viewport);
                        var bottom = element.TranslatePoint(new Point(element.ActualWidth, element.ActualHeight), viewport);
                        double visibleHeight = viewport == scroll ? scroll.ViewportHeight : main.ActualHeight;
                        double visibleWidth = viewport == scroll ? scroll.ViewportWidth : main.ActualWidth;
                        Assert(element.IsVisible && element.ActualHeight > 0 && top.Y >= -1 && bottom.Y <= visibleHeight + 1 &&
                            top.X >= -1 && bottom.X <= visibleWidth + 1,
                            $"{file}: {element.Name} [{top.X:F0},{top.Y:F0}–{bottom.X:F0},{bottom.Y:F0}], viewport {visibleWidth:F0}×{visibleHeight:F0}");
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
    }
}
