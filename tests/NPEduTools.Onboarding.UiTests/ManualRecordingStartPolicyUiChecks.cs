using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunManualRecordingStartPolicyChecks()
    {
        var dispatcher = Application.Current.Dispatcher;
        string pipe = "NPEduTools.Test.recording-start-policy." + Guid.NewGuid().ToString("N");
        const string displayId = "display-front", speakerA = "speaker-a", speakerB = "speaker-b", micId = "mic-teacher";
        var display = new RecordingDisplay(displayId, "前排教学大屏", 0, 0, 1920, 1080, true);
        int displayLeft = 0; bool useSecondSpeaker = false;
        RecordingEnvironment Inventory() => new(true, null, [display with { Left = displayLeft }], [new(micId, "讲台麦克风", true)],
            [new(speakerA, "讲台扬声器", !useSecondSpeaker), new(speakerB, "教室扩声器", useSecondSpeaker)]);
        var mode = "unknown";
        bool rejectAuthorizedStart = false;
        Guid host = Guid.NewGuid(), ticket = Guid.NewGuid();
        string version = new('e', 64);
        int reads = 0, statusReads = 0;
        var writes = new ConcurrentQueue<HostRequest>();
        RecordingState state = new("Idle", "准备录制");
        var automatic = new AutomaticRecordingState(false, Guid.Empty, "自动录课未启用", null, []);
        using var lifetime = new CancellationTokenSource();
        var server = Task.Run(() => WorkspaceProxy(pipe, request =>
        {
            var rule = Volatile.Read(ref mode);
            var protection = new NoiseProtectionState(false, true, host, null, ManagementSource: "School",
                ClassroomPolicy: new(version, "deny", "deny", StartManualRecording: rule));
            var reply = new HostResponse(1, request.RequestId, "Succeeded", null, "", Recording: Volatile.Read(ref state), Automatic: automatic);
            if (request.Capability == ClassroomActionAccess.ManualRecordingStatusCapability)
            {
                Interlocked.Increment(ref reads);
                reply = rule switch
                {
                    "unknown" => reply with { Outcome = "Rejected", ErrorCode = "PolicyUnavailable", Message = "无法读取策略" },
                    "old" => reply with { Outcome = "Rejected", ErrorCode = "UnknownCapability", Message = "Older fixture" },
                    _ => reply with { NoiseProtection = protection }
                };
            }
            else if (request.Capability == "noise.management.command")
            {
                writes.Enqueue(request);
                reply = reply with { NoiseProtection = protection with { Ticket = ticket } };
            }
            else if (request.Recording is { } command)
            {
                writes.Enqueue(request);
                if (command.Action == "start" && Volatile.Read(ref rejectAuthorizedStart))
                    return Task.FromResult(reply with { Outcome = "Rejected", ErrorCode = "MANAGEMENT_REQUIRED",
                        Message = "请重新核对本班权限和本次录课设置。" });
                var next = command.Action switch
                {
                    "start" => new RecordingState("Recording", "正在录制所选屏幕", 5, 40, 42000, Control: new("Manual", Guid.NewGuid(), "", 0, 0)),
                    "pause" => state with { Phase = "Paused", Message = "已暂停录制" },
                    "resume" => state with { Phase = "Recording", Message = "已继续录制" },
                    "stop" => new RecordingState("Saved", "已保存", OutputFile: Path.Combine(_output, "synthetic.mp4")),
                    _ => throw new InvalidOperationException("Unexpected recording action")
                };
                Volatile.Write(ref state, next); reply = reply with { Recording = next };
            }
            else if (request.Capability == "recording.status") Interlocked.Increment(ref statusReads);
            else if (request.Automatic?.Action != "lease") writes.Enqueue(request);
            return Task.FromResult(reply);
        }, lifetime.Token));
        var client = dispatcher.Invoke(() => new RecordingClient(dispatcher, pipe), DispatcherPriority.Normal);
        var window = new RecordingWindow(client, pipe, () => Task.FromResult(Inventory()));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        try
        {
            Exercise(window, owner =>
            {
                string Label(string id) => ((TextBlock)window.FindName(id)).Text;
                void Start() => Click(window, "RecordStart");
                void ExpectReady() => PumpUntil(() => Button(window, "RecordStart").IsEnabled, "manual recording did not become ready");
                ExpectReady();
                window.Width = 1160; window.Height = 780;
                Assert(Label("ReadySummary").Contains("前排教学大屏") && Label("ReadySummary").Contains("讲台麦克风") &&
                    Label("ReadySummary").Contains("讲台扬声器"), "the initial summary hid one of the actual sources");
                Snapshot(window, "01-wide-ready-sources.png");
                Start(); PumpUntil(() => Label("RecordingError").Contains("无法核实"), "an uncertain rule was accepted");
                Volatile.Write(ref mode, "deny"); Start(); PumpUntil(() => Label("RecordingError").Contains("学校已禁止"), "school denial missing");
                Assert(writes.IsEmpty, "unknown policy or denial sent a mutation");
                Checks.Add("Source summary resolves Windows default endpoints by name. Unknown and denied policies emit zero mutations.");
                Click(window, "EditSettings"); Check(window, "MicrophoneSound").IsChecked = false; Click(window, "ReturnRecordingSettings");
                window.MinHeight = 0; window.Width = 840; window.Height = 480;
                Volatile.Write(ref mode, "both");
                string scenario = "cancel";
                Exception? modalFailure = null;
                timer.Tick += (_, _) =>
                {
                    if (Application.Current.Windows.OfType<NoiseManagementDialog>().FirstOrDefault(d => d.IsVisible) is not { } dialog) return;
                    timer.Stop();
                    try
                    {
                        var labels = ExitVisuals(dialog).OfType<TextBlock>().Select(t => t.Text).ToArray();
                        string expectedSpeaker = useSecondSpeaker ? "教室扩声器" : "讲台扬声器";
                        Assert(dialog.Title == "开始手动录课 · 管理验证" &&
                            labels.Any(t => t.Contains("前排教学大屏") && t.Contains(expectedSpeaker) && t.Contains("麦克风　关闭")),
                            "the grant dialog did not identify the screen and both audio choices");
                        if (scenario == "cancel") { dialog.Close(); return; }
                        if (scenario == "change-default") useSecondSpeaker = true;
                        if (scenario == "move-screen") displayLeft += 1920;
                        if (scenario == "accept") Snapshot(dialog, "02-narrow-verify-sources.png");
                        ExitVisuals(dialog).OfType<PasswordBox>().Single(b => b.IsVisible).Password = "synthetic-admin-key";
                        ExitVisuals(dialog).OfType<Button>().Single(b => b.IsDefault)
                            .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    }
                    catch (Exception error) { modalFailure = error; dialog.Close(); }
                };
                void ViaDialog(string action, Func<bool> finished)
                {
                    scenario = action;
                    timer.Start(); Start();
                    PumpUntil(() => modalFailure is not null || finished(), "recording verification did not settle: " + scenario);
                    if (modalFailure is not null) throw modalFailure;
                }
                ViaDialog("cancel", () => Label("RecordingError").Contains("已取消"));
                Assert(writes.IsEmpty, "cancelling credential entry sent a write");
                foreach (string change in new[] { "change-default", "move-screen" })
                {
                    ViaDialog(change, () => Button(window, "RecordStart").IsEnabled && Label("RecordingError").Contains("来源已变化"));
                    Assert(writes.All(r => r.NoiseManagement?.Purpose == "start-manual-recording"),
                        "recording started despite a changed default endpoint or screen capture region");
                }
                Checks.Add("No recording starts after cancellation, a default-audio change, or a moved capture screen during authentication.");
                ViaDialog("accept", () => Label("RecordingPhase") == "录制中");
                var issued = writes.TakeLast(2).ToArray();
                Assert(issued is [{ NoiseManagement: { RecordingTarget: { Options: { SystemAudio: true, Microphone: false } } target } grant },
                    { Recording: { Action: "start" } startRequest }] && target == startRequest &&
                    grant.ClassroomPolicyVersion == version && grant.TargetRequestId == issued[1].RequestId &&
                    issued[1].NoiseAuthorization == ticket && target.ClientId is not null && target.Options.SpeakerId == speakerB &&
                    target.Options.Display == displayId, "submitted recording did not match its single authorized target");
                Assert(RecordingWindow.ReadSavedOptions(pipe) is { SpeakerId: "default", Microphone: false },
                    "the transient pinned endpoint became the teacher's long-term preference");
                Snapshot(window, "03-narrow-recording.png");
                Volatile.Write(ref mode, "deny"); int priorReads = Volatile.Read(ref reads);
                Click(window, "RecordPause"); PumpUntil(() => Label("RecordingPhase") == "已暂停", "pause unavailable");
                Click(window, "RecordPause"); PumpUntil(() => Label("RecordingPhase") == "录制中", "resume unavailable");
                Click(window, "RecordStop"); PumpUntil(() => Label("RecordingPhase") == "已保存", "stop unavailable");
                var controls = writes.TakeLast(3).ToArray();
                Assert(controls.Select(r => r.Recording?.Action).SequenceEqual(["pause", "resume", "stop"]) &&
                    controls.All(r => r.NoiseAuthorization is null && r.Recording?.Control?.SessionId == controls[0].Recording?.Control?.SessionId) &&
                    priorReads == Volatile.Read(ref reads), "an in-session control took a start grant or changed sessions");
                Checks.Add("Both factors authorize exactly one named-screen/explicit-audio start; pause, resume and stop stay tied to the original session.");
                int writesAfterSave = writes.Count;
                Start();
                PumpUntil(() => Label("RecordingError").Contains("学校已禁止"), "the previous saved result cleared the next start denial");
                int afterDenial = Volatile.Read(ref statusReads);
                PumpUntil(() => Volatile.Read(ref statusReads) >= afterDenial + 2, "saved-state polling stopped");
                Assert(Label("RecordingPhase") == "已保存" && Label("RecordingError").Contains("学校已禁止") && writes.Count == writesAfterSave,
                    "saved-state refresh hid the denied start or started another recording");
                Snapshot(window, "04-narrow-denied-after-save.png");
                Checks.Add("An explicit denial of the next recording survives refresh of the previously saved result, with zero extra writes.");
                Volatile.Write(ref mode, "both"); Volatile.Write(ref rejectAuthorizedStart, true);
                int beforeRejectedStart = writes.Count;
                ViaDialog("host-rejected", () => Label("RecordingStartRefusal").Contains("本次录课未开始") &&
                    Label("RecordingStartRefusal").Contains("重新核对本班权限"));
                int afterHostReply = Volatile.Read(ref statusReads);
                PumpUntil(() => Volatile.Read(ref statusReads) >= afterHostReply + 2, "post-rejection status polling stalled");
                Assert(Label("RecordingStartRefusal").Contains("本次录课未开始") && Label("RecordingPhase") == "已保存" &&
                    !Label("RecordingMessage").Contains("未确认") && Button(window, "OpenVideo") is { IsVisible: true, IsEnabled: true } &&
                    client.State.OutputFile == Path.Combine(_output, "synthetic.mp4") && writes.Count == beforeRejectedStart + 2,
                    "a confirmed Host refusal was retried, lost its reason, or hid the original saved video");
                Snapshot(window, "05-narrow-host-refused-original-saved.png");
                Checks.Add("A confirmed Host MANAGEMENT_REQUIRED after authorization retains its actual saved recording and the explicit refusal across normal refresh.");
                Volatile.Write(ref rejectAuthorizedStart, false);
                Volatile.Write(ref mode, "old"); int before = writes.Count; Start();
                PumpUntil(() => Label("RecordingPhase") == "录制中", "an old Host could not start");
                Assert(writes.Count == before + 1 && writes.Last() is { NoiseAuthorization: null, Recording.Options.SpeakerId: "default" },
                    "legacy start changed the user's default-source or added a ticket");
                Checks.Add("Explicit UnknownCapability alone restores the legacy unmodified start. Default audio preference remains portable.");
                window.Shutdown();
            });
        }
        finally
        {
            timer.Stop(); window.Shutdown(); client.Detach(); lifetime.Cancel();
            var done = client.DisposeAsync().AsTask(); PumpUntil(() => done.IsCompleted, "recording client did not stop"); done.GetAwaiter().GetResult();
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            string preferences = StartupPreferencesStore.PathFor(pipe).Replace(".startup.json", ".recording.json", StringComparison.Ordinal);
            if (File.Exists(preferences)) File.Delete(preferences);
        }
    }
}
