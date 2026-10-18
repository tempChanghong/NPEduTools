using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

internal static partial class Program
{
    private sealed class CalibrationUiClock : TimeProvider
    {
        public long Ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ms;
    }
    private sealed class CalibrationUiCapture : INoiseCapture, INoiseCaptureMetadata
    {
        public string DeviceName => "隔离模拟麦克风";
        public string Signature => "fixture-pcm-48000-mono-fixed-gain";
        public bool Started;
        public bool Disposed;
        public event Action<NoiseFrame>? Frame;
        public event Action<string>? Failed { add { } remove { } }
        public void Start() => Started = true;
        public void Emit(double dbfs) => Frame?.Invoke(new(.1, Math.Pow(10, dbfs / 10), .1, 0, false));
        public void Dispose() => Disposed = true;
    }
    private static void RunNoiseCalibrationChecks()
    {
        RunCalibrationTransportTimeoutChecks();
        RunReferenceSnapshotChecks();
        RunQuickCalibrationSnapshotChecks();
        RunCalibrationOrderingChecks(false);
        const string secret = "fixture-calibration-admin";
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        string folder = Path.Combine(_output, "calibration-fixture"); Directory.CreateDirectory(folder);
        var clock = new CalibrationUiClock();
        CalibrationUiCapture? capture = null;
        int paused = 0;
        var management = new NoiseManagementStore(folder, clock);
        Assert(management.Configure(null, secret, false) is null, "fixture management setup failed");
        var noise = new NoiseService(_ => capture = new(), () => [new("fixture-mic", "模拟麦克风")], clock, folder, management,
            referencePaused: () => Volatile.Read(ref paused) != 0);
        var initial = noise.Snapshot();
        noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("select", initial.InstanceId, initial.Revision, "fixture-mic")));
        string cases = Path.Combine(AppContext.BaseDirectory, "Fixtures/study-device-cases.json");
        var config = JsonNode.Parse(File.ReadAllText(cases))![0]!["value"]!.DeepClone().AsObject();
        var connection = Guid.NewGuid(); var bridge = Guid.NewGuid(); long sequence = 0;
        var school = new StudySettingsService(() => new(connection, bridge, ++sequence, 0,
            new DateTimeOffset(2026, 10, 12, 18, 30, 0, TimeSpan.Zero).AddMilliseconds(clock.Ms), 0, "Advancing", "fixture"),
            () => false, clock, false);
        string scope = config["identity"]!.ToJsonString();
        school.BindStudy(scope); school.ApplyStudy(config);
        string pipe = "NPEduTools.Test.calibration-ui." + Guid.NewGuid().ToString("N");
        using var lifetime = new CancellationTokenSource();
        var serving = Task.Run(() => new PipeServer(pipe, new DeviceReader(), _ => { }, noise: noise, study: school).RunAsync(lifetime.Token));
        var window = new NoiseCalibrationWindow(pipe);
        try
        {
            Exercise(window, owner =>
            {
                PumpUntil(() => Button(owner, "BeginDraft").IsEnabled, "quick calibration status did not load through Host pipe");
                Assert(((ComboBox)owner.FindName("SceneChoice")).Items.Count == 5, "natural scene options missing");
                Assert(((ComboBox)owner.FindName("LabelChoice")).Items.Count == 3, "judgement options missing");
                ((PasswordBox)owner.FindName("ManagementSecret")).Password = secret;
                Click(owner, "BeginDraft"); PumpUntil(() => Button(owner, "StartSample").IsEnabled, "draft not created");
                void Sample(string scene, string label, double level, int expected)
                {
                    ((ComboBox)owner.FindName("SceneChoice")).SelectedValue = scene;
                    ((ComboBox)owner.FindName("LabelChoice")).SelectedValue = label;
                    Click(owner, "StartSample");
                    PumpUntil(() => capture is { Started: true, Disposed: false } && Button(owner, "CancelSample").IsEnabled, "UI sampling did not start");
                    for (int i = 0; i < 150; i++)
                    { clock.Ms += 100; school.ApplyStudy(config); capture!.Emit(level); }
                    PumpUntil(() => ((ListBox)owner.FindName("SampleList")).Items.Count == expected && Button(owner, "StartSample").IsEnabled,
                        "sampling statistics did not return to WPF");
                }
                Sample("NormalStudy", "Acceptable", -55, 1);
                Sample("SustainedConversation", "Discipline", -20, 2);
                PumpUntil(() => Button(owner, "UseSuggestion").IsEnabled, "two natural scenes did not suggest thresholds");
                Click(owner, "UseSuggestion");
                Assert(((TextBox)owner.FindName("MediumThreshold")).Text.Length > 0, "suggestion not filled");
                Click(owner, "ConfirmThresholds");
                PumpUntil(() => ((TextBlock)owner.FindName("ProfileStatus")).Text.Contains("适用"), "confirmed local thresholds not applied");
                var policy = new StudyNoise(true, -10, -20, 600, 5);
                var effective = noise.ResolveCalibration(scope, config["version"]!.GetValue<string>(), policy);
                Assert(effective.RedLineDbfs != policy.RedLineDbfs && effective.DisciplineCooldownSeconds == 600 &&
                    effective.HighContinuousSeconds == 5, "calibration changed school timing or failed to replace only local lines");
                Snapshot(owner, "quick-calibration-confirmed.png");
                ((ScrollViewer)owner.Content).ScrollToEnd(); owner.UpdateLayout();
                Snapshot(owner, "quick-calibration-thresholds.png");
                Checks.Add("Actual WPF/private Host pipe: five optional scenes, separate judgements, two simulated 15-second samples, suggestion fill, management verification and local dual-threshold confirmation");
                var savedCapture = capture;
                Volatile.Write(ref paused, 1);
                PumpUntil(() => ((TextBlock)owner.FindName("SamplingStatus")).Text.Contains("运行保护") &&
                    !Button(owner, "BeginDraft").IsEnabled, "runtime pause did not disable calibration through Host pipe");
                Assert(!Button(owner, "StartSample").IsEnabled && !Button(owner, "ConfirmThresholds").IsEnabled &&
                    !Button(owner, "ClearThresholds").IsEnabled && !Button(owner, "UseSuggestion").IsEnabled,
                    "runtime-protected calibration allows writes");
                Assert(ReferenceEquals(savedCapture, capture), "runtime pause unexpectedly opened another capture");
                Snapshot(owner, "quick-calibration-runtime-paused.png");
                Volatile.Write(ref paused, 0);
                PumpUntil(() => Button(owner, "BeginDraft").IsEnabled && ((TextBlock)owner.FindName("ProfileStatus")).Text.Contains("适用"),
                    "runtime resume did not preserve confirmed thresholds");
                Checks.Add("Actual WPF/private Host pipe: runtime pause disables sampling and configuration writes; resuming preserves confirmed thresholds without opening a device");
                var rows = (ListBox)owner.FindName("SampleList"); rows.SelectedIndex = 0;
                ((ComboBox)owner.FindName("LabelChoice")).SelectedValue = "Visual";
                Click(owner, "RelabelSample");
                PumpUntil(() => ((TextBlock)owner.FindName("SuggestionStatus")).Text.Contains("至少补充"), "relabel failed");
                Click(owner, "ClearThresholds");
                PumpUntil(() => ((TextBlock)owner.FindName("ProfileStatus")).Text.Contains("尚未确认"), "school threshold restore failed");
                Checks.Add("Actual WPF/private Host pipe: relabel recalculates suggestion, restore school thresholds; no real clock, microphone, camera or cloud");
                owner.Close();
            });
        }
        finally
        {
            window.Close(); lifetime.Cancel(); try { serving.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            school.DisposeAsync().AsTask().GetAwaiter().GetResult(); noise.DisposeAsync().AsTask().GetAwaiter().GetResult();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
