using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Core;
using NPEduTools.Host;

internal static partial class Program
{
    private sealed class MeterUiBackend : ISoundMeterHidBackend
    {
        public int Enumerations, Opens, Disposals;
        public byte Flags;
        public double Value = 60.5;
        public IReadOnlyList<SoundMeterHidDevice> Enumerate()
        { Enumerations++; return [new("synthetic-meter", "隔离模拟 2202", Dly2202Protocol.VendorId, Dly2202Protocol.ProductId)]; }
        public ISoundMeterHidConnection Open(SoundMeterHidDevice d) { Opens++; return new MeterUiConnection(this, d); }
    }
    private sealed class MeterUiConnection(MeterUiBackend backend, SoundMeterHidDevice d) : ISoundMeterHidConnection
    {
        public SoundMeterHidDevice Device => d;
        public int InputReportLength => 9;
        public int OutputReportLength => 9;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken token)
        { Assert(report.Span[1] == 0xB3, "beta sent a non-read command"); token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
        public ValueTask<int> ReadAsync(Memory<byte> report, CancellationToken token)
        { report.Span.Clear(); System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(report.Span[1..], (short)(backend.Value * 10)); report.Span[3] = backend.Flags; return ValueTask.FromResult(9); }
        public void Dispose() => backend.Disposals++;
        public void DiscardQueuedInput() { }
    }
    private static void RunSoundMeterChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        var backend = new MeterUiBackend();
        string folder = Path.Combine(_output, "meter-school-credential-fixture");
        var store = new NoiseManagementStore(folder);
        var noise = new NoiseService(_ => throw new Exception("No real microphone"), () => [], directory: folder, management: store);
        var school = new SchoolManagementService(noise, new StudyDeviceService(() => [], folder), folder);
        var identity = new System.Text.Json.Nodes.JsonObject { ["deviceId"] = "fixture" };
        school.BindSchool(identity.ToJsonString());
        byte[] salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        byte[] hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2("fixture-admin", salt, 210000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        school.ApplySchool(new() { ["identity"] = identity, ["credential"] = new System.Text.Json.Nodes.JsonObject {
            ["revision"] = 7L, ["verifier"] = new System.Text.Json.Nodes.JsonObject { ["iterations"] = 210000,
                ["salt"] = Convert.ToBase64String(salt), ["hash"] = Convert.ToBase64String(hash) } },
            ["equipment"] = new System.Text.Json.Nodes.JsonObject { ["revision"] = 0, ["locked"] = false, ["microphoneId"] = null, ["cameraId"] = null } });
        var service = new SoundMeterService(() => backend, s => noise.ManagementHandle(new(1, Guid.NewGuid(), "noise.management.command", NoiseManagement: new("verify", s))).ErrorCode);
        string pipe = "NPEduTools.Test.meter-beta." + Guid.NewGuid().ToString("N");
        using var lifetime = new CancellationTokenSource();
        var serving = Task.Run(() => new PipeServer(pipe, new DeviceReader(), _ => { }, noise: noise, soundMeter: service).RunAsync(lifetime.Token));
        var window = new SoundMeterWindow(pipe);
        try
        {
            Exercise(window, owner =>
            {
                Assert(owner.Title.Contains("联机校准 (Beta)"), "Beta label missing");
                Assert(backend.Enumerations == 0 && backend.Opens == 0, "window load touched hardware");
                PumpUntil(() => ((TextBlock)owner.FindName("ManagementLabel")).Text.Contains("学校"), "school credential source was not displayed");
                Assert(((TextBlock)owner.FindName("ManagementHint")).Text.Contains("7"), "school credential revision missing");
                Assert(!Button(owner, "ConfigureManagement").IsEnabled, "school credential could be overwritten locally");
                var secret = (PasswordBox)owner.FindName("ManagementSecret"); secret.Password = "wrong-password";
                Click(owner, "ListDevices");
                PumpUntil(() => ((TextBlock)owner.FindName("Message")).Text.Contains("验证未通过"), "management failure missing");
                Assert(backend.Enumerations == 0, "unauthorized list enumerated devices");
                Checks.Add("WPF/private Host pipe: Beta label, no startup enumeration, denied management never touches backend");
                secret.Password = "fixture-admin"; Click(owner, "ListDevices");
                var list = (ComboBox)owner.FindName("Devices"); PumpUntil(() => list.Items.Count == 1, "fake device list missing");
                Assert(((TextBlock)owner.FindName("Message")).Text.Contains("学校管理口令验证通过"), "successful school verification had no explicit feedback");
                Assert(backend.Opens == 0, "enumeration opened a device"); list.SelectedIndex = 0;
                Assert(!((Expander)owner.FindName("DiagnosticDetails")).IsExpanded && Button(owner, "OpenReference").IsVisible, "diagnostics crowd the default connection workflow");
                ((Expander)owner.FindName("DiagnosticDetails")).IsExpanded = true;
                Click(owner, "ReadMeter");
                PumpUntil(() => ((TextBlock)owner.FindName("Reading")).Text.Contains("60.5"), "fake reading missing");
                Assert(backend.Opens == 1 && backend.Disposals == 1, "one-shot handle was not released");
                Assert(((TextBlock)owner.FindName("Mode")).Text.Contains("9/9"), "HID capabilities missing from diagnostic UI");
                Assert(((TextBlock)owner.FindName("Message")).Text.Contains("未自动填入"), "reading claimed calibration complete");
                Snapshot(owner, "sound-meter-beta-reading.png");
                Checks.Add("WPF/private Host pipe: explicit exact-device read, A/Slow mode, one-shot release; reading never claims calibration success");
                PumpUntil(() => ((TextBlock)owner.FindName("Message")).Text.Contains("已过期"), "cached reading never expired");
                Assert(((TextBlock)owner.FindName("Reading")).Text == "—", "stale reading remains visible");
                backend.Flags = 0xF0; Click(owner, "ReadMeter");
                PumpUntil(() => ((TextBlock)owner.FindName("Reading")).Text.Contains("dB(C)"), "C reading silently relabelled");
                Assert(((TextBlock)owner.FindName("Mode")).Text.Contains("MAX 开") && ((TextBlock)owner.FindName("Mode")).Text.Contains("低电量 是"), "mode flags hidden");
                Assert(((TextBlock)owner.FindName("Message")).Text.Contains("不能用于"), "ineligible reading accepted as reference");
                Checks.Add("WPF/private Host pipe: cached reading expires; C/Fast/MAX/low-battery remain explicit and cannot become A reference");
                backend.Value = 140; Click(owner, "ReadMeter");
                PumpUntil(() => ((TextBlock)owner.FindName("Message")).Text.Contains("报文无效"), "invalid report error missing");
                Assert(((TextBlock)owner.FindName("Reading")).Text == "—", "failed read reused old value");
                Assert(backend.Opens == backend.Disposals, "fake handles leaked");
                owner.Close(); Assert(secret.Password.Length == 0, "password retained after close");
                Checks.Add("WPF/private Host pipe: invalid report clears old value, releases handle; closing clears password; no real device or audio");
            });
        }
        finally
        {
            window.Close(); lifetime.Cancel(); try { serving.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            noise.DisposeAsync().AsTask().GetAwaiter().GetResult();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        RunSoundMeterAccessChecks();
    }
}
