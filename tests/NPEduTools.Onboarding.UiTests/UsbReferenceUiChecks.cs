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
        RunCalibrationOrderingChecks(true);
        RunFieldReferenceChecks();
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
                Assert(((FrameworkElement)owner.FindName("PreparePanel")).IsVisible && !((FrameworkElement)owner.FindName("SamplePanel")).IsVisible,
                    "USB wizard did not begin at setup");
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
                ((ComboBox)owner.FindName("WorkflowChoice")).SelectedValue="Stable";
                ((TextBox)owner.FindName("Stability")).Text = "1"; ((TextBox)owner.FindName("CheckTolerance")).Text = "1";
                Click(owner, "BeginDraft"); PumpUntil(() => Button(owner, "StartSample").IsEnabled, "USB draft missing");
                Assert(((FrameworkElement)owner.FindName("SamplePanel")).IsVisible && !((FrameworkElement)owner.FindName("PreparePanel")).IsVisible, "draft preparation did not advance to sampling");
                Click(owner, "StartSample");
                PumpUntil(() => ((TextBlock)owner.FindName("MeterStatus")).Text.Contains("50.0 dB(A)"), "continuous USB reading missing");
                Assert(!Button(owner, "ConfirmProfile").IsEnabled, "accepted request claimed calibration ready");
                var uiType = typeof(NoiseReferenceWindow);
                uiType.GetField("_connected", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(owner, false);
                uiType.GetMethod("UpdateButtons", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(owner, null);
                Assert(Button(owner,"RefreshStatus").IsVisible && Button(owner,"SetupStep").IsEnabled && !Button(owner,"ConfirmProfile").IsEnabled,
                    "lost connection traps sampling view or permits stale save");
                Click(owner,"SetupStep");
                Assert(((PasswordBox)owner.FindName("ManagementSecret")).IsVisible,"lost connection cannot reach credentials");
                Click(owner,"RefreshStatus");
                PumpUntil(()=>((FrameworkElement)owner.FindName("SamplePanel")).IsVisible && Button(owner,"CancelSample").IsEnabled,"refresh could not return to the active sampling step");
                Assert(backend.Opens==1,"workflow recovery started an extra USB capture");
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
                Click(owner, "ConfirmProfile"); PumpUntil(() => ((TextBlock)owner.FindName("ProfileStatus")).Text.Contains("已联机校准 (Beta)"), "USB profile not saved");
                var saved = noise.ReferenceHandle(new(1, Guid.NewGuid(), "noise.reference.status")).NoiseReference!;
                Assert(saved.Profile is { Method: "UsbReferenceBeta" } && saved.Profile.Samples.All(p => p.MeterEvidence is not null), "USB evidence missing");
                Snapshot(owner, "usb-reference-confirmed.png");
                Checks.Add("Real WPF/private Host pipe + synthetic HID/microphone: explicit two-level USB capture, automatic offset/check, Beta profile; no physical devices");
                // A backend handle can open before the Accepted reply reaches this window.
                // Test owner cancellation only after the UI has confirmed its sampling ownership.
                Click(owner, "ReviewSamples");
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
    private static void RunFieldReferenceChecks()
    {
        const string secret="fixture-field-reference-admin";
        string folder=Path.Combine(Path.GetTempPath(),"npep-field-reference-ui-"+Guid.NewGuid());
        var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        var backend=new MeterUiBackend {Value=50};var management=new NoiseManagementStore(folder);
        Assert(management.Configure(null,secret,false) is null,"field fixture management setup failed");
        var meter=new SoundMeterService(()=>backend,value=>value==secret?null:"MANAGEMENT_SECRET_INVALID");
        int creates=0;UsbUiCapture? capture=null;
        var noise=new NoiseService(_=>throw new Exception("No real/formal microphone"),()=>[new("synthetic-mic","Synthetic")],directory:folder,management:management,
            referenceCreate:_=>capture=new(backend.Value-90+(++creates==2?4:0)),referenceMeterOpen:meter.OpenReferenceAsync);
        var initial=noise.Snapshot();noise.Handle(new(1,Guid.NewGuid(),"noise.command",Noise:new("select",initial.InstanceId,initial.Revision,"synthetic-mic")));
        string pipe="NPEduTools.Test.field-reference."+Guid.NewGuid().ToString("N");using var stop=new CancellationTokenSource();
        var serving=Task.Run(()=>new PipeServer(pipe,new DeviceReader(),_=>{},noise:noise,soundMeter:meter).RunAsync(stop.Token));
        var window=new NoiseReferenceWindow(pipe,"synthetic-meter");
        try {
            Exercise(window,owner=> {
                PumpUntil(()=>Button(owner,"BeginDraft").IsEnabled,"field Host not connected");
                Assert(((ComboBox)owner.FindName("WorkflowChoice")).SelectedValue as string=="Field","USB did not default to field mode");
                Assert(((TextBox)owner.FindName("Stability")).Text=="20" && ((TextBox)owner.FindName("CheckTolerance")).Text=="3","field engineering defaults missing");
                Assert(!((Expander)owner.FindName("AdvancedSettings")).IsExpanded,"field wizard exposed engineering parameters by default");
                Assert(backend.Opens==0 && capture is null,"field page opened sensors on load");
                ((PasswordBox)owner.FindName("ManagementSecret")).Password=secret;Click(owner,"BeginDraft");
                PumpUntil(()=>Button(owner,"StartSample").IsEnabled,"field draft unavailable");Click(owner,"StartSample");
                PumpUntil(()=>creates==2,"field first stage did not automatically start its owned check",48);
                PumpUntil(()=>noise.ReferenceHandle(new(1,Guid.NewGuid(),"noise.reference.status")).NoiseReference?.Evaluation.Assessment?.WarningRequired==true,"field check warning missing",48);
                PumpUntil(()=>((CheckBox)owner.FindName("AcceptReferenceWarning")).Visibility==Visibility.Visible,"warning confirmation checkbox missing");
                Assert(((FrameworkElement)owner.FindName("ResultPanel")).IsVisible && Button(owner,"ConfirmProfile").IsVisible,"field completion did not advance to visible results");
                Assert(!Button(owner,"ConfirmProfile").IsEnabled,"warning bypassed without deliberate acknowledgement");
                Assert(backend.Opens==2 && backend.Disposals==2 && capture!.Disposed,"automatic pair retained sensor handles");
                Assert(((TextBlock)owner.FindName("EvaluationStatus")).Text.Contains("未验证全量程"),"same-level field result claimed a broad validation range");
                var observed=noise.ReferenceHandle(new(1,Guid.NewGuid(),"noise.reference.status")).NoiseReference!;
                var first=observed.Draft!.Samples.Single(x=>x.Role=="Primary");
                var continuation=observed with {Revision=101,Draft=observed.Draft with {Samples=[first]}};
                var continuationCheck=typeof(NoiseReferenceWindow).GetMethod("OwnsFieldContinuation",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
                bool Owns(NoiseReferenceState candidate)=>(bool)continuationCheck.Invoke(null,[candidate,continuation.Draft.Id,100L,first.Id])!;
                Assert(Owns(continuation),"owned first result lost continuation eligibility");
                Assert(!Owns(continuation with {Revision=103}),"another window's resample retained old automatic continuation");
                Assert(!Owns(continuation with {Draft=continuation.Draft with {Samples=[first with {Id=Guid.NewGuid()}]}}),"replacement first sample retained automatic continuation");
                ((ScrollViewer)owner.FindName("ReferencePageScroll")).ScrollToBottom();owner.UpdateLayout();
                Snapshot(owner,"field-reference-warning.png");
                ((CheckBox)owner.FindName("AcceptReferenceWarning")).IsChecked=true;Assert(Button(owner,"ConfirmProfile").IsEnabled,"acknowledgement did not enable save");
                Click(owner,"ConfirmProfile");PumpUntil(()=>noise.ReferenceHandle(new(1,Guid.NewGuid(),"noise.reference.status")).NoiseReference?.Profile is {Workflow:"Field",WarningAcknowledged:true},"field acknowledgement/profile not saved");
                PumpUntil(()=>Button(owner,"StartSample").IsEnabled && ((TextBlock)owner.FindName("ProfileStatus")).Text.Contains("已明确接受"),"field save response did not settle in WPF");
                Snapshot(owner,"field-reference-confirmed.png");
                Click(owner,"ReviewSamples");
                Assert(Button(owner,"StartSample").IsVisible,"return-to-sampling did not expose retry action");
                Click(owner,"StartSample");PumpUntil(()=>creates==3 && Button(owner,"CancelSample").IsEnabled,"field retry did not start");
                owner.Close();PumpUntil(()=>!owner.IsVisible && backend.Disposals==3 && capture!.Disposed,"field owner close failed to cancel/release");
                Assert(creates==3,"closed field owner resumed a fourth capture");
                Checks.Add("Field WPF/private Host pipe + synthetic HID/microphone: USB defaults 20/3, one-click two 30s stages, same-level validation scope, 4dB warning requires checkbox, save records acknowledgement, close cancels; no real sensors");
            });
        } finally {
            window.Close();stop.Cancel();try{serving.GetAwaiter().GetResult();}catch(OperationCanceledException){}
            Task.Run(async()=>await noise.DisposeAsync()).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Directory.Delete(folder,true);SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
