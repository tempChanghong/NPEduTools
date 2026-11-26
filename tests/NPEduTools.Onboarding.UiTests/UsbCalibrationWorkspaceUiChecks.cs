using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
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
    private sealed class TwoMeterBackend : ISoundMeterHidBackend
    {
        public const string First = "synthetic-2202-first", Second = "synthetic-2202-second";
        public volatile bool FirstPresent = true;
        public int Enumerations;
        public readonly ConcurrentQueue<string> Opens = new();
        public IReadOnlyList<SoundMeterHidDevice> Enumerate()
        {
            Interlocked.Increment(ref Enumerations);
            SoundMeterHidDevice Other(string path) => new(path, "隔离模拟 2202", Dly2202Protocol.VendorId, Dly2202Protocol.ProductId);
            return FirstPresent ? [Other(First), Other(Second)] : [Other(Second)];
        }
        public ISoundMeterHidConnection Open(SoundMeterHidDevice device)
        {
            if (device.Path == First && !FirstPresent)
                throw new SoundMeterException(SoundMeterError.Disconnected, "fixture removed the original meter");
            Opens.Enqueue(device.Path);
            return new TwoMeterConnection(device);
        }
    }

    private sealed class TwoMeterConnection(SoundMeterHidDevice device) : ISoundMeterHidConnection
    {
        public SoundMeterHidDevice Device => device;
        public int InputReportLength => 9;
        public int OutputReportLength => 9;
        public void DiscardQueuedInput() { }
        public void Dispose() { }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert(report.Span[1] == 0xB3, "USB workflow sent a non-read command");
            return ValueTask.CompletedTask;
        }
        public ValueTask<int> ReadAsync(Memory<byte> report, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            report.Span.Clear();
            BinaryPrimitives.WriteInt16BigEndian(report.Span[1..], device.Path == TwoMeterBackend.First ? (short)600 : (short)770);
            return ValueTask.FromResult(9);
        }
    }

    private static void RunUsbCalibrationWorkspaceChecks()
    {
        const string secret = "fixture-usb-only";
        string folder = Path.Combine(_output, "private-host");
        var store = new NoiseManagementStore(folder);
        Assert(store.Configure(null, secret, false) is null, "isolated credential setup failed");
        var backend = new TwoMeterBackend();
        var meter = new SoundMeterService(() => backend, value => value == secret ? null : "MANAGEMENT_SECRET_INVALID");
        int formalCapture = 0, unplugged = 0;
        Guid? firstSample = null;
        var noise = new NoiseService(_ =>
        {
            Interlocked.Increment(ref formalCapture);
            throw new InvalidOperationException("No formal capture");
        }, () => [new("fixture-mic", "隔离模拟麦克风")], directory: folder, management: store,
            referenceCreate: _ => new UsbUiCapture(-30), referenceMeterOpen: meter.OpenReferenceAsync);
        var initial = noise.Snapshot();
        Assert(noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("select", initial.InstanceId, initial.Revision, "fixture-mic")))
            .Outcome == "Succeeded", "could not select fixture microphone");
        var commands = new ConcurrentQueue<NoiseReferenceCommand>();
        string pipe = "NPEduTools.Test.usb-calibration-workspace." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var server = Task.Run(() => WorkspaceProxy(pipe, async request =>
        {
            if (request.Capability == "noise.meter.command") return await meter.HandleAsync(request, stop.Token);
            if (request.Capability == "noise.management.status") return noise.ManagementHandle(request);
            if (request.Capability is "noise.reference.status" or "noise.reference.command")
            {
                if (request.NoiseReference is { } command) commands.Enqueue(command);
                var reply = noise.ReferenceHandle(request);
                if (reply.NoiseReference?.Draft?.Samples is [ { Role: "Primary", Quality: "Good", SameWindowConfirmed: true } first ] &&
                    Interlocked.CompareExchange(ref unplugged, 1, 0) == 0)
                {
                    firstSample = first.Id;
                    backend.FirstPresent = false;
                }
                return reply;
            }
            return new(1, request.RequestId, "Succeeded", null, "", Study: StudyFixture("Outside"));
        }, stop.Token));
        var main = PrivateWorkspace(pipe);
        try
        {
            Exercise(main, _ =>
            {
                Click(main, "StudyNav");
                Click(main, "SchoolWorkspaceTab");
                var school = WorkspaceField<StudyMonitoringWindow>(main, "_studyMonitoringWindow");
                var entry = Button(school, "OpenMeterCalibration");
                var hwnd = new WindowInteropHelper(main).Handle;
                Click(school, "OpenMeterCalibration");
                var picker = Picker();
                PumpUntil(() => ((TextBlock)picker.FindName("ManagementLabel")).Text.Contains("本机"), "management source did not load");
                Assert(backend.Enumerations == 0 && backend.Opens.IsEmpty, "entering the workflow opened or enumerated USB");
                foreach (string id in new[] { "MinimizeWindow", "MaximizeRestoreWindow", "CloseWindow" })
                    Assert(Button(main, id, automation: true).IsVisible, "missing main caption control " + id);
                Capture(840, 600, "connection-narrow", picker, Button(picker, "ListDevices"));
                Capture(1040, 760, "connection-wide", picker, Button(picker, "ListDevices"));

                VerifyAndList(picker, 2);
                var firstOption = Select(picker, TwoMeterBackend.First);
                var secondOption = Devices(picker).Items.Cast<SoundMeterDevice>().Single(device => device.Path == TwoMeterBackend.Second);
                Assert(firstOption.Name != secondOption.Name && Context().Contains(firstOption.Name) && backend.Opens.IsEmpty,
                    "same-name devices cannot be distinguished or selecting one opened hardware");
                Capture(840, 600, "selected-narrow", picker, Devices(picker), Button(picker, "OpenReference"));
                var diagnostics = (Expander)picker.FindName("DiagnosticDetails");
                var diagnosticPane = (FrameworkElement)picker.FindName("MeterDiagnosticPane");
                Assert(!diagnostics.IsExpanded, "single-read diagnostics crowd the initial flow");
                diagnostics.IsExpanded = true;
                Capture(1040, 760, "diagnostics-wide", picker, Devices(picker), Button(picker, "ReadMeter"));
                Assert(Grid.GetColumn(diagnosticPane) == 1 && backend.Opens.IsEmpty, "wide diagnostics did not use the side lane or read automatically");
                Capture(840, 600, "diagnostics-narrow", picker, Devices(picker), Button(picker, "ReadMeter"));
                Assert(Grid.GetColumn(diagnosticPane) == 0, "narrow diagnostics did not stack below the connection form");
                diagnostics.IsExpanded = false;
                Click(picker, "OpenReference");
                var reference = Reference();
                PumpUntil(() => Button(reference, "BeginDraft").IsEnabled, "reference preparation did not load");
                Assert(new WindowInteropHelper(picker).Handle == nint.Zero && new WindowInteropHelper(reference).Handle == nint.Zero &&
                    new WindowInteropHelper(main).Handle == hwnd && Context().Contains(firstOption.Name),
                    "the handoff changed the native window or lost the original instrument");
                Capture(840, 600, "prepare-narrow", reference, Button(reference, "BeginDraft"));
                Click(reference, "BeginDraft");
                PumpUntil(() => Button(reference, "StartSample").IsVisible && Button(reference, "StartSample").IsEnabled, "draft did not reach sampling");
                var original = Status().Draft!;
                var action = Button(reference, "StartSample");
                Assert(original.MeterDevicePath == TwoMeterBackend.First && !((ComboBox)reference.FindName("RoleChoice")).IsVisible &&
                    ((TextBlock)reference.FindName("SamplingStatus")).Text.Contains("第 1 段待采"), "field flow asks for a manual level or lost the selected meter");
                Capture(840, 600, "sample-narrow", reference, (TextBlock)reference.FindName("SamplingStatus"), action);
                Capture(1040, 760, "sample-wide", reference, action);
                Checks.Add("The school entry uses one main HWND and its three controls. Same-name USB items are distinct, and both layouts retain the meter and current action through preparation and sampling.");

                Click(reference, "StartSample");
                PumpUntil(() => Button(reference, "CancelSample").IsEnabled, "the owned first reference sample did not start");
                Assert(!Button(main, "WorkspaceUsbConnection").IsEnabled, "the instrument could change during owned sampling");
                PumpUntil(() => !Button(reference, "StartSample").IsEnabled && Context().Contains("重新查找") &&
                    Status().Draft?.Samples.Any(sample => sample.Role == "Check" && sample.Quality != "Good") == true,
                    "unplugging after a valid first sample did not suspend recovery", 50);
                var interrupted = Status().Draft!;
                Assert(interrupted.Id == original.Id && interrupted.Samples.Any(sample => sample.Id == firstSample && sample.Quality == "Good") &&
                    commands.Count(command => command.Action == "start") == 2 && backend.Opens.SequenceEqual([TwoMeterBackend.First]),
                    "failure discarded the valid segment, silently changed device or retried");
                Capture(840, 600, "interrupted-narrow", reference, (TextBlock)reference.FindName("SamplingStatus"));

                Click(main, "WorkspaceUsbConnection");
                picker = Picker();
                VerifyAndList(picker, 1);
                Assert(Devices(picker).SelectedItem is null && !Button(picker, "OpenReference").IsEnabled,
                    "a same-name replacement was chosen while the original USB path was absent");
                Select(picker, TwoMeterBackend.Second);
                Click(picker, "OpenReference");
                reference = Reference();
                PumpUntil(() => Button(reference, "BeginDraft").IsEnabled && ((TextBlock)reference.FindName("DraftStatus")).Text.Contains("其他仪表"),
                    "other meter did not show the original-draft warning");
                var priorRef = reference;
                int warnings = 0;
                var dismissed = (Task)typeof(NoiseReferenceWindow).GetMethod("BeginDraftAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(priorRef, [(Func<bool>)(() => { warnings++; return false; })])!;
                dismissed.GetAwaiter().GetResult();
                Assert(warnings == 1 && Status().Draft?.Id == original.Id && commands.Count(command => command.Action == "begin") == 1 &&
                    ((ListBox)reference.FindName("SampleList")).Items.Count == 0,
                    "another USB path inherited, replaced, or displayed the previous instrument evidence");

                backend.FirstPresent = true;
                Click(main, "WorkspaceUsbConnection");
                picker = Picker();
                VerifyAndList(picker, 2);
                Select(picker, TwoMeterBackend.First);
                Click(picker, "OpenReference");
                reference = Reference();
                PumpUntil(() => Button(reference, "StartSample").IsEnabled &&
                    Equals(Button(reference, "StartSample").Content, "继续第 2 段"), "original draft did not resume at the second segment");
                Assert(Status().Draft?.Id == original.Id && commands.Count(command => command.Action == "start") == 2,
                    "reconnecting started another recording or created a new draft");
                Capture(840, 600, "resumed-narrow", reference, (TextBlock)reference.FindName("SamplingStatus"), Button(reference, "StartSample"));
                Checks.Add("Removing the original after segment one retains that exact segment. An identically named different path stays unselected and cannot read or replace the recovered draft without consent.");

                Click(reference, "StartSample");
                PumpUntil(() => Button(reference, "ConfirmProfile").IsVisible && Button(reference, "ConfirmProfile").IsEnabled,
                    "second segment did not reach explicit review", 50);
                var complete = Status();
                Assert(complete.Draft?.Id == original.Id &&
                    complete.Draft.Samples.Any(sample => sample.Id == firstSample) && complete.Profile is null &&
                    commands.Where(command => command.Action == "start").Select(command => command.Role).SequenceEqual(["Primary", "Check", "Check"]) &&
                    backend.Opens.SequenceEqual([TwoMeterBackend.First, TwoMeterBackend.First]),
                    "resuming replaced the first segment, wrote a profile, or opened the wrong USB path");
                Assert(!((Border)reference.FindName("UsbReviewWarningSurface")).IsVisible,
                    "an ordinary successful comparison shows an empty warning surface");
                Capture(840, 600, "review-narrow", reference, (TextBlock)reference.FindName("EvaluationStatus"), Button(reference, "ConfirmProfile"));
                Capture(1040, 760, "review-wide", reference, Button(reference, "ConfirmProfile"));
                Click(reference, "ConfirmProfile");
                PumpUntil(() => Status().Profile?.MeterDevicePath == TwoMeterBackend.First, "the explicit profile save was not recorded");
                Assert(Status().Profile?.Samples.Any(sample => sample.Id == firstSample) == true && formalCapture == 0,
                    "the saved reference omitted the original evidence or started ordinary study monitoring");
                Click(main, "WorkspaceUsbBack");
                PumpUntil(() => entry.IsVisible && Keyboard.FocusedElement == entry, "exit did not restore original school action and focus");
                Checks.Add("Only the explicitly resumed second segment uses the reconnected original USB path. Review and manual save fit both sizes; return restores the original school entry without starting formal monitoring.");
                main.Close();

                Window Controller() => WorkspaceField<Window>(main, "_workspaceCalibrationController");
                SoundMeterWindow Picker() => (SoundMeterWindow)Controller();
                NoiseReferenceWindow Reference() => (NoiseReferenceWindow)Controller();
                ComboBox Devices(Window page) => (ComboBox)page.FindName("Devices");
                string Context() => ((TextBlock)main.FindName("WorkspaceUsbContext")).Text;
                void VerifyAndList(Window page, int count)
                {
                    ((PasswordBox)page.FindName("ManagementSecret")).Password = secret;
                    Click(page, "ListDevices");
                    PumpUntil(() => Devices(page).Items.Count == count && Button(page, "ListDevices").IsEnabled,
                        $"the intended USB catalog did not settle (expected {count})");
                }
                SoundMeterDevice Select(Window page, string path)
                {
                    var device = Devices(page).Items.Cast<SoundMeterDevice>().Single(item => item.Path == path);
                    Devices(page).SelectedItem = device;
                    return device;
                }
                void Capture(int width, int height, string name, Window page, params FrameworkElement[] elements)
                {
                    main.Width = width;
                    main.Height = height;
                    main.UpdateLayout();
                    PumpUntil(() => Math.Abs(main.ActualWidth - width) < 2 && Math.Abs(main.ActualHeight - height) < 2, "window did not fit viewport");
                    Snapshot(main, name + ".png");
                    var scroll = page.FindName("ReferencePageScroll") as ScrollViewer ?? page.FindName("MeterScroll") as ScrollViewer;
                    foreach (var item in elements)
                    {
                        var viewport = scroll?.IsAncestorOf(item) == true ? (FrameworkElement)scroll : main;
                        var point = item.TranslatePoint(new Point(), viewport);
                        var bottom = item.TranslatePoint(new Point(item.ActualWidth, item.ActualHeight), viewport);
                        var visibleHeight = viewport is ScrollViewer scrollViewport ? scrollViewport.ViewportHeight : main.ActualHeight;
                        Assert(item.IsVisible && item.ActualHeight > 0 && point.Y >= -1 && bottom.Y <= visibleHeight + 1 &&
                            point.X >= -1 && bottom.X <= viewport.ActualWidth + 1,
                            $"{name}: {item.Name} occupies [{point.X:F0},{point.Y:F0}–{bottom.X:F0},{bottom.Y:F0}] in viewport height {visibleHeight:F0}");
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
        NoiseReferenceState Status() => noise.ReferenceHandle(new(1, Guid.NewGuid(), "noise.reference.status")).NoiseReference!;
    }
}
