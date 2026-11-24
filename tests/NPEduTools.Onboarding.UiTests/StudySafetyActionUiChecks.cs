using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunScheduledStopSafetyChecks()
    {
        foreach (string scenario in new[] { "School", "Legacy", "Cancel", "Changed", "ChangedAtCredential", "WrongWindow", "LostReply", "Manual", "LegacyReadFailed",
            "PolicyDeny", "PolicyAllow", "PolicyPassword", "PolicyUsb", "EitherPassword", "EitherUsb", "Both", "BothUsbMissing", "PolicyChanged", "PolicyUnavailable" })
            RunScheduledStopSafetyCheck(scenario);
    }

    private static void RunScheduledStopSafetyCheck(string scenario)
    {
        string pipe = "NPEduTools.Test.study-stop." + Guid.NewGuid().ToString("N");
        var instance = Guid.NewGuid(); var session = Guid.NewGuid(); var ticket = Guid.NewGuid();
        var study = StudyFixture();
        var schedule = new SchoolNoiseWindow(new(study.Window!.Start.DateTime, TimeSpan.FromHours(8)), new(study.Window.End.DateTime, TimeSpan.FromHours(8)));
        static string Stamp(DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var executionWindow = new StudyExecutionWindow(Stamp(schedule.Start), Stamp(scenario == "WrongWindow" ? schedule.End.AddMinutes(1) : schedule.End));
        var noise = new NoiseState(instance, 4, "Active", "Synthetic", "fixture", "Fixture", null, null, "Good", null, [], SessionId: session);
        bool legacy = scenario is "Legacy" or "LegacyReadFailed";
        study = study with
        {
            Window = schedule,
            Display = study.Display! with { Selected = !legacy },
            Execution = study.Execution! with { SessionId = session, Owner = scenario == "Manual" ? "Manual" : "Schedule", Window = executionWindow }
        };
        string? mode = scenario switch
        {
            "PolicyDeny" => "deny", "PolicyAllow" => "allow", "PolicyPassword" => "password", "PolicyUsb" => "usb",
            "EitherPassword" or "EitherUsb" => "either", "Both" or "BothUsbMissing" or "PolicyChanged" => "both", _ => null
        };
        var policy = mode is null ? null : new ClassroomActionPolicyState(new string('a', 64), mode, "deny");
        var protection = new NoiseProtectionState(true, scenario != "PolicyUsb", instance, session,
            ManagementSource: legacy ? "Local" : "School", ClassroomPolicy: policy);
        using var end = new CancellationTokenSource();
        var writes = new ConcurrentQueue<HostRequest>();
        int revision = 4, legacyReads = 0, stopped = 0, scheduleChanged = 0, policyChanged = 0;
        var server = Task.Run(() => WorkspaceProxy(pipe, request =>
        {
            var active = Volatile.Read(ref stopped) == 0;
            var current = noise with { Revision = Volatile.Read(ref revision), State = active ? "Active" : "Idle" };
            HostResponse Answer(StudyHostState? state = null, NoiseDisplayState? display = null, NoiseProtectionState? auth = null) =>
                new(1, request.RequestId, "Succeeded", null, "Synthetic status", Study: state, Noise: current, NoiseDisplay: display, NoiseProtection: auth);
            switch (request.Capability)
            {
                case "study.status":
                    var currentWindow = Volatile.Read(ref scheduleChanged) == 0 ? study : study with
                    {
                        Window = new(schedule.Start, schedule.End.AddMinutes(1)),
                        Execution = study.Execution! with { Window = new(Stamp(schedule.Start), Stamp(schedule.End.AddMinutes(1))) }
                    };
                    return Task.FromResult(Answer(active ? currentWindow : study with
                    { Execution = study.Execution! with { Owner = "None", State = "Idle", SessionId = null, Reason = "WINDOW_SKIPPED" } }));
                case "noise.display.status":
                    Interlocked.Increment(ref legacyReads);
                    if (scenario == "LegacyReadFailed") throw new IOException("Drop only the optional legacy read");
                    return Task.FromResult(Answer(display: new("FALLBACK", "Synthetic old schedule", true, instance, session, study.Window)));
                case ClassroomActionAccess.StatusCapability:
                    if (legacy) return Task.FromResult(new HostResponse(1, request.RequestId, "Rejected", "UnknownCapability", "Older generation"));
                    if (scenario == "PolicyUnavailable") return Task.FromResult(new HostResponse(1, request.RequestId, "Rejected", "PolicyUnavailable", "No proof"));
                    return Task.FromResult(Answer(auth: Volatile.Read(ref policyChanged) == 0 ? protection :
                        protection with { ClassroomPolicy = new(new string('b',64), "deny", "deny") }));
                case "noise.management.status":
                    return Task.FromResult(Answer(auth: protection));
                case "noise.management.command":
                    writes.Enqueue(request);
                    if (scenario == "BothUsbMissing")
                        return Task.FromResult(new HostResponse(1, request.RequestId, "Rejected", "CLASSROOM_USB_REQUIRED", "USB absent"));
                    return Task.FromResult(Answer(auth: protection with { Ticket = ticket }));
                case "noise.command":
                    writes.Enqueue(request);
                    Interlocked.Exchange(ref stopped, 1);
                    if (scenario == "LostReply") throw new IOException("Drop the response after applying the synthetic stop");
                    return Task.FromResult(new HostResponse(1, request.RequestId, "Accepted", null, "Synthetic receipt"));
                case "noise.devices":
                    return Task.FromResult(Answer());
                default:
                    writes.Enqueue(request);
                    return Task.FromResult(new HostResponse(1, request.RequestId, "Rejected", "ProbeUnexpectedCapability", "Unexpected fixture request"));
            }
        }, end.Token));
        var window = new StudyMonitoringWindow(pipe, () => { });
        var type = typeof(MainWindow).Assembly.GetType("NPEduTools.App.NoiseManagementDialog")!;
        bool sawConfirmation = false, submittedCredential = false, sawMissingUsb = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            if (Application.Current.Windows.OfType<StudyActionConfirmation>().FirstOrDefault(d => d.IsVisible) is { } confirmation)
            {
                sawConfirmation = Button(confirmation, "CancelAction").IsDefault &&
                    ((TextBlock)confirmation.FindName("EffectText")).Text.Contains("后台保持运行");
                if (scenario == "School") Snapshot(confirmation, "school-scheduled-stop-confirmation.png");
                if (scenario == "Cancel") confirmation.Close();
                else
                {
                    if (scenario == "Changed") Interlocked.Increment(ref revision);
                    if (scenario == "PolicyChanged") Interlocked.Exchange(ref policyChanged, 1);
                    Click(confirmation, "ConfirmAction");
                }
            }
            if (Application.Current.Windows.Cast<Window>().FirstOrDefault(d => d.GetType() == type && d.IsVisible) is not { } dialog) return;
            if (!submittedCredential)
            {
                submittedCredential = true;
                if (scenario == "ChangedAtCredential") Interlocked.Exchange(ref scheduleChanged, 1);
                var secret = (PasswordBox)type.GetField("_old", WorkspaceFlags)!.GetValue(dialog)!;
                if (scenario == "EitherUsb")
                {
                    secret.Password = "discard-on-factor-switch";
                    ExitVisuals(dialog).OfType<RadioButton>().Single(button => Equals(button.Content, "使用教师 U 盘")).IsChecked = true;
                    Assert(!secret.IsVisible && secret.Password.Length == 0, "choosing USB must clear and hide the alternative password");
                }
                bool usbOnly = scenario is "PolicyUsb" or "EitherUsb";
                Assert(secret.IsVisible != usbOnly, "wrong credential input for " + scenario);
                if (!usbOnly) secret.Password = "isolated-demo-credential";
                if (scenario == "Both") Snapshot(dialog, "scheduled-stop-password-and-usb.png");
                if (scenario == "EitherUsb") Snapshot(dialog, "scheduled-stop-either-usb.png");
                ((Button)type.GetField("_submit", WorkspaceFlags)!.GetValue(dialog)!).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            }
            else if (scenario == "BothUsbMissing" && ExitVisuals(dialog).OfType<TextBlock>().Any(text => text.Text.Contains("未检测到本班获准")))
            {
                sawMissingUsb = true;
                dialog.Close();
            }
        };
        try
        {
            Exercise(window, _ =>
            {
                var button = Button(window, "StopScheduledButton");
                var capture = (TextBlock)window.FindName("StudyCapture");
                var notice = (TextBlock)window.FindName("StopNotice");
                if (scenario is "Manual" or "LegacyReadFailed" or "WrongWindow")
                {
                    var phase = (TextBlock)window.FindName("StudyPhase");
                    PumpUntil(() => scenario == "Manual" ? capture.Text.Contains("手动采样") : phase.Text == "自习进行中", "valid school status did not render");
                    Assert(!button.IsVisible && writes.IsEmpty && Volatile.Read(ref legacyReads) == (legacy ? 1 : 0),
                        "a manual session, different capture window, or failed optional legacy read offered scheduled stop");
                }
                else if (scenario is "PolicyDeny" or "PolicyUnavailable")
                {
                    var rule = (TextBlock)window.FindName("StopRule");
                    PumpUntil(() => rule.Text.Contains(scenario == "PolicyDeny" ? "学校已禁止" : "无法确认") && button.IsVisible, "denied/unknown policy did not render");
                    Click(window, "StopScheduledButton");
                    Assert(!button.IsEnabled && writes.IsEmpty, "denied action initiated verification or stop");
                }
                else
                {
                    PumpUntil(() => button.IsVisible && button.IsEnabled, "scheduled stop unavailable for a matching current window");
                    timer.Start();
                    Click(window, "StopScheduledButton");
                    if (scenario == "Cancel")
                        PumpUntil(() => button.IsEnabled, "cancelled stop did not return to the current scene");
                    else
                    {
                        string expected = scenario switch { "Changed" or "ChangedAtCredential" or "PolicyChanged" => "已变化",
                            "LostReply" => "结果待确认", "BothUsbMissing" => "未取得", _ => "已受理" };
                        PumpUntil(() => notice.Text?.Contains(expected) == true, "scheduled stop did not show the expected evidence: " + scenario);
                    }
                    Assert(sawConfirmation, "stop must explain the scope and default to cancel");
                    if (scenario is "Changed" or "Cancel" or "PolicyChanged") Assert(writes.IsEmpty, "stale or cancelled target sent a write");
                    else if (scenario == "BothUsbMissing")
                        Assert(sawMissingUsb && writes.Count == 1 && writes.Single().NoiseManagement?.Secret == "isolated-demo-credential",
                            "one factor alone granted stop or triggered a new submission");
                    else if (scenario == "ChangedAtCredential")
                        Assert(writes.Count == 1 && writes.TryPeek(out var grantRequest) && grantRequest.NoiseManagement is { Purpose: "stop" },
                            "a new window adopted the same session after confirmation, but the original stop was dispatched");
                    else
                    {
                        var sent = writes.ToArray();
                        Assert(sent.Length == 2 && sent[0].NoiseManagement is { Purpose: "stop" } grant && grant.TargetRequestId == sent[1].RequestId &&
                            grant.SessionId == session && grant.InstanceId == instance && sent[1].Noise == new NoiseCommand("stop", instance, 4) &&
                            sent[1].NoiseAuthorization == ticket && sent[1].Capability == "noise.command", "stop escaped its original authorization or was replayed");
                        Assert(sent[0].NoiseManagement!.ClassroomPolicyVersion == policy?.Version &&
                            (sent[0].NoiseManagement!.Secret is null) == (scenario is "PolicyAllow" or "PolicyUsb" or "EitherUsb") &&
                            submittedCredential == (scenario != "PolicyAllow"), "wrong factor or scoped proof for " + scenario);
                        Assert(!notice.Text.Contains("已停止") && notice.Text.Contains("19:00–21:30"),
                            "receipt claimed that actual capture had ended or lost its original window");
                        if (!legacy)
                            PumpUntil(() => capture.Text.Contains("本场监测已停止"), "actual skipped-window status did not render after the stop");
                        if (scenario == "School") Snapshot(window, "school-scheduled-stop-result.png");
                    }
                }
                window.Close();
            });
            Checks.Add($"Scheduled stop {scenario}: WPF action and isolated status/authorization/receipt; no hardware or shared Host");
        }
        finally
        {
            timer.Stop(); end.Cancel();
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) when (end.IsCancellationRequested) { }
        }
    }
}
