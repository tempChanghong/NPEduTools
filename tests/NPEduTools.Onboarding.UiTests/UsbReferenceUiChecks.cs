using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

internal static partial class Program
{
    private sealed class UsbUiCapture(double digital) : IReferenceNoiseCapture
    {
        public string DeviceName => "隔离模拟参考麦克风";
        public string Signature => ReferenceNoiseMeter.AlgorithmVersion + "|48000|1|32|gain=.7|mute=False";
        public event Action<NoiseFrame>? Frame { add { } remove { } }
        public event Action<ReferenceNoiseFrame>? ReferenceFrame;
        public event Action<string>? Failed { add { } remove { } }
        private System.Threading.Timer? _timer;
        private int _frames, _disposed;
        public bool Disposed => Volatile.Read(ref _disposed) != 0;
        public void Start() => _timer = new(_ =>
        {
            if (Disposed) return;
            int n = Interlocked.Increment(ref _frames); double e = Math.Pow(10, digital / 10);
            ReferenceFrame?.Invoke(new(.1, e, e, e, .1, 0, false, n > 50));
        }, null, 100, 100);
        public void Dispose() { Interlocked.Exchange(ref _disposed, 1); _timer?.Dispose(); }
    }
    private static void RunUsbReferenceChecks()
    {
        const string secret = "fixture-reference-admin";
        string folder = Path.Combine(Path.GetTempPath(), "npep-usb-reference-ui-" + Guid.NewGuid());
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        var backend = new MeterUiBackend { Value = 50 };
        var management = new NoiseManagementStore(folder); Assert(management.Configure(null, secret, false) is null, "management setup failed");
        var meter = new SoundMeterService(() => backend, value => value == secret ? null : "MANAGEMENT_SECRET_INVALID");
        UsbUiCapture? capture = null;
        var noise = new NoiseService(_ => throw new Exception("No real/formal microphone"), () => [new("synthetic-mic", "Synthetic")],
            directory: folder, management: management, referenceCreate: _ => capture = new(backend.Value - 90), referenceMeterOpen: meter.OpenReferenceAsync);
        var s = noise.Snapshot(); noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("select", s.InstanceId, s.Revision, "synthetic-mic")));
        string pipe = "NPEduTools.Test.usb-reference." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var serving = Task.Run(() => new PipeServer(pipe, new DeviceReader(), _ => { }, noise: noise, soundMeter: meter).RunAsync(stop.Token));
        var window = new NoiseReferenceWindow(pipe, "synthetic-meter");
        try
        {
            Exercise(window, owner =>
            {
                PumpUntil(() => Button(owner, "BeginDraft").IsEnabled, "USB reference Host not connected");
                Assert(owner.Title.Contains("(Beta)"), "USB reference lost Beta title");
                Assert(backend.Opens == 0 && backend.Enumerations == 0 && capture is null, "load touched devices");
                Assert(((WrapPanel)owner.FindName("ManualReferenceInputs")).Visibility == Visibility.Collapsed, "USB page allows manual override");
                ((PasswordBox)owner.FindName("ManagementSecret")).Password = "wrong-password";
                Click(owner, "RefreshStatus");
                PumpUntil(() => ((TextBlock)owner.FindName("OperationMessage")).Text.Contains("验证未通过"), "refresh did not reject wrong password");
                Assert(backend.Opens == 0 && capture is null, "refresh verification touched sensors");
                ((PasswordBox)owner.FindName("ManagementSecret")).Password = secret;
                Click(owner, "RefreshStatus");
                PumpUntil(() => ((TextBlock)owner.FindName("OperationMessage")).Text.Contains("管理口令验证通过"), "refresh did not report valid password");
                Assert(backend.Opens == 0 && capture is null, "valid refresh verification touched sensors");
                ((TextBox)owner.FindName("Stability")).Text = "1"; ((TextBox)owner.FindName("CheckTolerance")).Text = "1";
                Click(owner, "BeginDraft"); PumpUntil(() => Button(owner, "StartSample").IsEnabled, "USB draft missing");
                Click(owner, "StartSample");
                PumpUntil(() => ((TextBlock)owner.FindName("MeterStatus")).Text.Contains("50.0 dB(A)"), "continuous USB reading missing");
                Assert(!Button(owner, "ConfirmProfile").IsEnabled, "accepted request claimed calibration ready");
                var observer = new NoiseReferenceWindow(pipe, "synthetic-meter") { Owner = owner };
                observer.Show(); PumpUntil(() => Button(observer, "CancelSample").IsEnabled, "observer did not see current capture");
                ((PasswordBox)observer.FindName("ManagementSecret")).Password = secret; observer.Close();
                var observing = System.Diagnostics.Stopwatch.StartNew(); PumpUntil(() => observing.ElapsedMilliseconds > 300, "observer close wait failed");
                Assert(capture is { Disposed: false } && backend.Disposals == 0, "closing observer cancelled another window's capture");
                Snapshot(owner, "usb-reference-sampling.png");
                var samples = (ListBox)owner.FindName("SampleList");
                PumpUntil(() => samples.Items.Count == 1 && Button(owner, "StartSample").IsEnabled, "first automatic reference missing", 22);
                Assert(((TextBlock)owner.FindName("MeterStatus")).Text.Contains("仅在明确"), "terminal USB value remained live");
                Assert(backend.Opens == backend.Disposals && capture!.Disposed, "first window did not release devices");
                backend.Value = 60; Click(owner, "StartSample");
                PumpUntil(() => samples.Items.Count == 2 && Button(owner, "ConfirmProfile").IsEnabled, "second reference not ready", 22);
                Click(owner, "ConfirmProfile"); PumpUntil(() => ((TextBlock)owner.FindName("ProfileStatus")).Text.Contains("USB (Beta)"), "USB profile not saved");
                var saved = noise.ReferenceHandle(new(1, Guid.NewGuid(), "noise.reference.status")).NoiseReference!;
                Assert(saved.Profile is { Method: "UsbReferenceBeta" } && saved.Profile.Samples.All(p => p.MeterEvidence is not null), "USB evidence missing");
                Snapshot(owner, "usb-reference-confirmed.png");
                Checks.Add("Real WPF/private Host pipe + synthetic HID/microphone: explicit two-level USB capture, automatic offset/check, Beta profile; no physical devices");
                // A backend handle can open before the Accepted reply reaches this window.
                // Test owner cancellation only after the UI has confirmed its sampling ownership.
                Click(owner, "StartSample"); PumpUntil(() => backend.Opens == 3 && Button(owner, "CancelSample").IsEnabled, "third capture not confirmed");
                owner.Close(); PumpUntil(() => !owner.IsVisible && backend.Disposals == 3 && capture!.Disposed, "close did not cancel/release both devices");
                Assert(((PasswordBox)owner.FindName("ManagementSecret")).Password.Length == 0, "password retained after close");
                Checks.Add("WPF: no startup acquisition, manual inputs hidden, acceptance not success, terminal reading removed; observer close preserves capture, owner close cancels and clears password");
            });
        }
        finally
        {
            window.Close(); stop.Cancel(); try { serving.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            // Dispose may await a final worker; never block the dispatcher it would resume on.
            Task.Run(async () => await noise.DisposeAsync()).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Directory.Delete(folder, true);
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
