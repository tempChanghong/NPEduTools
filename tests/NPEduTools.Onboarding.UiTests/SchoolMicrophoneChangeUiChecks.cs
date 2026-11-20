using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Host;

internal static partial class Program
{
    private static void RunSchoolMicrophoneChangeUiChecks()
    {
        SchoolMicrophoneChangeWithCameraFault(false);
        SchoolMicrophoneChangeWithCameraFault(true);
        foreach (var scenario in new[] { "allowed", "wrong-secret", "token-changed", "missing-receipt", "malformed",
                     "wrong-target", "different-host", "denied", "busy", "empty", "single", "refresh-failed", "unplugged-after",
                     "legacy-locked", "policy-unavailable", "selection-unconfirmed", "start-capture", "unplug-before", "store-unavailable",
                     "command-unsupported", "camera-fault-mismatch", "camera-fault-missing-token", "camera-fault-empty-token",
                     "camera-fault-unlocked", "camera-fault-incomplete", "camera-fault-bad-entry", "camera-fault-bad-saved-id",
                     "camera-fault-legacy", "camera-fault-other-rejection" })
        {
            var pipe = "NPEduTools.Test.school-mic." + Guid.NewGuid().ToString("N");
            var directory = Path.Combine(_output, "school-mic-" + scenario);
            using var stop = new CancellationTokenSource();
            var noise = new NoiseService(_ => throw new Exception("Capture forbidden"), () => [], directory: directory);
            var cameras = new StudyDeviceService(() => [], directory);
            Guid grant = Guid.NewGuid();
            int changes = 0, legacyReads = 0;
            string choice = "mic-a";
            bool cameraFault = scenario.StartsWith("camera-fault-", StringComparison.Ordinal);
            bool legacy = scenario is "legacy-locked" or "camera-fault-legacy";
            NoiseDevice[] catalog = scenario switch
            {
                "empty" => [],
                "single" => [new("mic-a", "讲台麦克风")],
                _ => [new("mic-a", "讲台麦克风"), new("mic-b", "阵列麦克风")],
            };
            NoiseState CurrentNoise() => noise.Snapshot() with { SelectedDeviceId = choice, State = scenario == "busy" ? "Monitoring" : "Idle" };
            var server = Task.Run(() => WorkspaceProxy(pipe, request =>
            {
                if (request.Capability == "noise.management.command")
                {
                    Interlocked.Increment(ref changes);
                    Assert(request.NoiseManagement is { Action: "replace-microphone", DeviceId: "mic-b", Secret: "fixture-secret" } command &&
                        command.PolicyToken == grant, "wrong maintenance target, secret, or policy scope");
                    string? error = scenario switch
                    {
                        "wrong-secret" => "MANAGEMENT_SECRET_INVALID",
                        "token-changed" => "EQUIPMENT_POLICY_CHANGED",
                        "start-capture" => "EQUIPMENT_PENDING",
                        "unplug-before" => "EQUIPMENT_UNAVAILABLE",
                        "store-unavailable" => "EQUIPMENT_STORE_UNAVAILABLE",
                        "selection-unconfirmed" => "EQUIPMENT_CHANGE_UNCONFIRMED",
                        "command-unsupported" => "UnknownCapability",
                        _ => null
                    };
                    if (error is not null) return Task.FromResult(new HostResponse(1, request.RequestId, "Rejected", error, "fixture"));
                    choice = "mic-b";
                    grant = Guid.NewGuid();
                    var receipt = scenario switch
                    {
                        "missing-receipt" => null,
                        "wrong-target" => CurrentNoise() with { SelectedDeviceId = "mic-a" },
                        "different-host" => CurrentNoise() with { InstanceId = Guid.NewGuid() },
                        _ => CurrentNoise()
                    };
                    return Task.FromResult(new HostResponse(scenario == "malformed" ? 2 : 1, request.RequestId, "Succeeded", null, "fixture", Noise: receipt));
                }
                if (scenario == "refresh-failed" && Volatile.Read(ref changes) != 0)
                    return Task.FromResult(new HostResponse(2, request.RequestId, "Succeeded", null, "fixture"));
                if (request.Capability.StartsWith("noise.devices", StringComparison.Ordinal))
                    return Task.FromResult(new HostResponse(1, request.RequestId, "Succeeded", null, "fixture", Noise: CurrentNoise(),
                        NoiseDevices: scenario == "unplugged-after" && Volatile.Read(ref changes) > 0 ? [new("mic-a", "讲台麦克风")] : catalog));
                if (request.Capability == "study.devices.refresh")
                {
                    Interlocked.Increment(ref legacyReads);
                    if (cameraFault) return Task.FromResult(new HostResponse(1, request.RequestId, "Rejected", "CameraDevicesUnavailable", "old host",
                        StudyDevices: cameras.Snapshot() with { Error = "CameraDevicesUnavailable", Locked = true }));
                    return Task.FromResult(new HostResponse(1, request.RequestId, "Succeeded", null, "old host", StudyDevices: cameras.Snapshot() with { Locked = true }));
                }
                Assert(request.Capability == "study.devices.refresh-microphone-policy", "unexpected mutation in picker");
                if (legacy || scenario == "policy-unavailable")
                    return Task.FromResult(new HostResponse(1, request.RequestId, "Rejected",
                        legacy ? "UnknownCapability" : "EQUIPMENT_STORE_UNAVAILABLE", "fixture"));
                if (cameraFault)
                {
                    string error = scenario == "camera-fault-other-rejection" ? "EQUIPMENT_LOCKED" : "CameraDevicesUnavailable";
                    var state = cameras.Snapshot() with { Locked = scenario != "camera-fault-unlocked",
                        Error = scenario == "camera-fault-mismatch" ? "CameraConfigurationUnavailable" : error,
                        CameraId = scenario == "camera-fault-bad-saved-id" ? " " : null,
                        Cameras = scenario == "camera-fault-incomplete" ? [null!] : scenario == "camera-fault-bad-entry" ? [new("cam", null!)] : [],
                        MicrophoneChangeToken = scenario == "camera-fault-missing-token" ? null : scenario == "camera-fault-empty-token" ? Guid.Empty : grant };
                    return Task.FromResult(new HostResponse(1, request.RequestId, "Rejected", error, "fixture", StudyDevices: state));
                }
                return Task.FromResult(new HostResponse(1, request.RequestId, "Succeeded", null, "fixture",
                    StudyDevices: cameras.Snapshot() with { Locked = true, MicrophoneChangeToken = scenario == "denied" ? null : grant }));
            }, stop.Token, supportsSchoolMicrophonePolicy: true));
            var picker = new StudyDevicePicker(); picker.Bind(pipe);
            int saved = 0; picker.SelectionSaved += (_, _) => saved++;
            var mic = (ComboBox)picker.FindName("StudyMicrophone");
            var camera = (ComboBox)picker.FindName("StudyCamera");
            var confirm = (Button)picker.FindName("ConfirmSchoolMicrophone");
            var secret = (PasswordBox)picker.FindName("EquipmentSecret");
            var message = (TextBlock)picker.FindName("DeviceMessage");
            var sample = scenario == "allowed" ? new Window { Width = 580, Height = 560, ShowActivated = false,
                Content = new Border { Padding = new Thickness(20), Child = picker } } : null;
            try
            {
                sample?.Show();
                var initial = picker.RefreshAsync(); PumpUntil(() => initial.IsCompleted, "initial read stalled"); initial.GetAwaiter().GetResult();
                Assert(!camera.IsEnabled && !((Button)picker.FindName("SaveCamera")).IsEnabled && !confirm.IsEnabled, "school lock or unchanged input was editable");
                if (cameraFault || scenario is "denied" or "busy" or "empty" or "single" or "legacy-locked" or "policy-unavailable")
                {
                    Assert(!mic.IsEnabled && !secret.IsEnabled && changes == 0, "ineligible classroom offered a protected change");
                }
                else
                {
                    mic.SelectedValue = "mic-b";
                    Assert(confirm.IsEnabled && secret.IsEnabled, "authorized candidate inaccessible");
                    if (sample is not null)
                    {
                        secret.Password = "old-fixture-input";
                        mic.SelectedValue = "mic-a";
                        Assert(secret.Password.Length == 0 && !confirm.IsEnabled && !confirm.IsVisible, "secret or permission survived a different target");
                        mic.SelectedValue = "mic-b";
                        sample.UpdateLayout();
                        var confirmTopLeft = confirm.TranslatePoint(new Point(), sample);
                        Assert(confirm.IsVisible && secret.IsVisible && secret.ActualWidth >= 160 && confirmTopLeft.X + confirm.ActualWidth <= sample.ActualWidth &&
                            confirmTopLeft.Y + confirm.ActualHeight <= sample.ActualHeight, "single-change confirmation clipped in compact picker");
                        Snapshot(sample, "school-single-microphone.png");
                    }
                    secret.Password = "fixture-secret";
                    confirm.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    PumpUntil(() => Volatile.Read(ref changes) == 1 && message.Text.Length > 0 &&
                        !(bool)typeof(StudyDevicePicker).GetField("_busy", WorkspaceFlags)!.GetValue(picker)!, "protected change never finished");
                    Assert(secret.Password.Length == 0 && changes == 1 && !camera.IsEnabled, "secret retained, duplicate change, or camera unlocked");
                    Assert(scenario switch
                    {
                        "allowed" => saved == 1 && !confirm.IsEnabled && mic.SelectedValue as string == "mic-b" && message.Text.Contains("锁定仍生效"),
                        "wrong-secret" => saved == 0 && confirm.IsEnabled && message.Text.Contains("口令不正确"),
                        "token-changed" => saved == 0 && !confirm.IsEnabled && message.Text.Contains("策略已变化"),
                        "start-capture" => saved == 0 && !confirm.IsEnabled && message.Text.Contains("正在采集"),
                        "unplug-before" => saved == 0 && !confirm.IsEnabled && message.Text.Contains("已断开"),
                        "store-unavailable" => saved == 0 && !confirm.IsEnabled && message.Text.Contains("无法保存"),
                        "command-unsupported" => saved == 0 && !confirm.IsEnabled && message.Text.Contains("不支持受管麦克风更换"),
                        "selection-unconfirmed" => saved == 0 && !confirm.IsEnabled && message.Text.Contains("麦克风仍待确认"),
                        "refresh-failed" or "unplugged-after" => saved == 0 && !confirm.IsEnabled && message.Text.Contains("待核对"),
                        _ => saved == 0 && !confirm.IsEnabled && message.Text.Contains("未确认"),
                    }, "incorrect result: " + scenario + " " + message.Text);
                }
                Assert(legacyReads == (legacy ? 1 : 0), "school policy read was downgraded without UnknownCapability");
                Checks.Add("School single-microphone maintenance: " + scenario + "; camera remains locked; no real sensors.");
            }
            finally
            {
                sample?.Close(); picker.CancelPending(); stop.Cancel();
                try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
                noise.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }
    private static void SchoolMicrophoneChangeWithCameraFault(bool corruptCameraConfiguration)
    {
        string dir = Path.Combine(_output, "school-mic-camera-fault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string cameraFile = Path.Combine(dir, "study-camera.json");
        if (corruptCameraConfiguration) File.WriteAllText(cameraFile, "invalid-fixture-json");
        var cameras = new StudyDeviceService(() => corruptCameraConfiguration ? [] : throw new IOException("Camera catalog fixture"), dir);
        var noise = new NoiseService(_ => throw new InvalidOperationException("Capture forbidden"),
            () => [new("mic-a", "讲台"), new("mic-b", "阵列")], directory: dir, management: new NoiseManagementStore(dir));
        var school = new SchoolManagementService(noise, cameras, dir);
        var identity = new JsonObject { ["deviceId"] = Guid.NewGuid().ToString() };
        school.BindSchool(identity.ToJsonString());
        const string secret = "test-school-password";
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(secret, salt, 210000, HashAlgorithmName.SHA256, 32);
        school.ApplySchool(new() { ["identity"] = identity, ["credential"] = new JsonObject { ["revision"] = 1L,
            ["verifier"] = new JsonObject { ["iterations"] = 210000, ["salt"] = Convert.ToBase64String(salt), ["hash"] = Convert.ToBase64String(hash) } },
            ["equipment"] = new JsonObject { ["revision"] = 1L, ["locked"] = true, ["microphoneId"] = "mic-a", ["cameraId"] = null, ["allowLocalMicrophoneChange"] = true } });
        string pipe = "NPEduTools.Test.school-mic-camera-fault." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var server = Task.Run(() => new PipeServer(pipe, new DeviceReader(), _ => { }, noise: noise, studyDevices: cameras).RunAsync(stop.Token));
        var picker = new StudyDevicePicker(); picker.Bind(pipe);
        int saved = 0;
        picker.SelectionSaved += (_, _) => saved++;
        try
        {
            var read = picker.RefreshAsync(); PumpUntil(() => read.IsCompleted, "camera fault read stalled"); read.GetAwaiter().GetResult();
            var mic = (ComboBox)picker.FindName("StudyMicrophone");
            var camera = (ComboBox)picker.FindName("StudyCamera");
            var cameraStatus = (TextBlock)picker.FindName("CameraStatus");
            string faultText = corruptCameraConfiguration ? "无法读取摄像头配置" : "无法读取摄像头列表";
            Assert(mic.IsEnabled && mic.SelectedValue as string == "mic-a" && !camera.IsEnabled &&
                cameraStatus.Text.Contains(faultText) && picker.Summary.Contains(faultText), "camera fault hid microphone authorization or camera diagnosis");
            mic.SelectedValue = "mic-b";
            var confirm = (Button)picker.FindName("ConfirmSchoolMicrophone");
            Assert(confirm.IsEnabled, "camera failure blocked authorized microphone confirmation");
            ((PasswordBox)picker.FindName("EquipmentSecret")).Password = secret;
            confirm.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => saved != 0, "private Host did not confirm microphone change with camera failure");
            Assert(saved == 1 && mic.SelectedValue as string == "mic-b" && noise.Snapshot().SelectedDeviceId == "mic-b" &&
                !camera.IsEnabled && !((Button)picker.FindName("SaveCamera")).IsEnabled && cameraStatus.Text.Contains(faultText) &&
                (corruptCameraConfiguration ? File.ReadAllText(cameraFile) == "invalid-fixture-json" : !File.Exists(cameraFile)),
                "changing microphone lost the camera error/lock or wrote camera configuration");
            Checks.Add("School microphone change and private Host with " + (corruptCameraConfiguration ? "corrupt camera configuration" : "failed camera enumeration") +
                ": microphone changed, camera remained locked with visible diagnosis, no camera write or physical capture.");
        }
        finally
        {
            picker.CancelPending(); stop.Cancel();
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            noise.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
