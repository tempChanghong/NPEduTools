using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunManualStartPolicyChecks()
    {
        string pipe = "NPEduTools.Test.manual-start-policy." + Guid.NewGuid().ToString("N");
        Guid instance = Guid.NewGuid(), ticket = Guid.NewGuid();
        string version = new('a', 64), mode = "unavailable";
        var writes = new ConcurrentQueue<HostRequest>();
        int policyReads = 0;
        var manual = new ManualStudyState(Guid.NewGuid(), 1, null, "Idle", null, null, null, false, null);
        var device = new NoiseDevice("synthetic-classroom", "教室麦克风");
        NoiseState Signal(ManualStudyState m) => new(instance, 1, m.State is "Starting" or "Active" ? m.State : "Idle",
            m.State == "Active" ? "正在采集教室麦克风。" : "", device.Id, device.Name, null, m.State == "Active" ? -48 : null,
            m.State == "Active" ? "Good" : "NoSignal", null, [], SessionId: m.SessionId, SelectedDeviceId: device.Id);
        using var stop = new CancellationTokenSource();
        var server = Task.Run(() => WorkspaceProxy(pipe, request =>
        {
            var current = Volatile.Read(ref manual);
            string rule = Volatile.Read(ref mode);
            var protection = new NoiseProtectionState(false, true, instance, null, ManagementSource: "School",
                ClassroomPolicy: new(version, "deny", "deny", StartManualStudy: rule));
            HostResponse Success() => new(1, request.RequestId, "Succeeded", null, "", Noise: Signal(current), ManualStudy: current);
            HostResponse Reject(string code, string message) => new(1, request.RequestId, "Rejected", code, message);
            HostResponse reply;
            switch (request.Capability)
            {
                case "noise.status":
                case "manual.study.status":
                    reply = Success(); break;
                case "noise.devices":
                    reply = Success() with { NoiseDevices = [device] }; break;
                case ClassroomActionAccess.ManualStudyStatusCapability:
                    Interlocked.Increment(ref policyReads);
                    reply = rule switch {
                        "unavailable" => Reject("PolicyUnavailable", "暂无法读取学校策略"),
                        "old" => Reject("UnknownCapability", "旧版后台"),
                        _ => Success() with { NoiseProtection = protection }
                    };
                    break;
                case "noise.management.command":
                    writes.Enqueue(request);
                    reply = Success() with { NoiseProtection = protection with { Ticket = ticket } }; break;
                case "manual.study.command":
                    writes.Enqueue(request);
                    var command = request.ManualStudy!;
                    current = command.Action == "start" ? new(current.InstanceId, current.Revision + 1, Guid.NewGuid(),
                        command.StartUtc.HasValue ? "Waiting" : "Starting", null, command.StartUtc ?? DateTimeOffset.UtcNow,
                        command.EndUtc, command.EndUtc.HasValue, command.HideSeconds) :
                        current with { Revision = current.Revision + 1, State = current.State == "Waiting" ? "Cancelled" : "Stopped",
                            Reason = current.State == "Waiting" ? "CancelledBeforeStart" : "UserEnded" };
                    Volatile.Write(ref manual, current);
                    reply = Success(); break;
                default:
                    writes.Enqueue(request);
                    reply = Reject("ProbeUnexpectedCapability", "Unexpected fixture request"); break;
            }
            return Task.FromResult(reply);
        }, stop.Token));
        // The controller starts polling in its constructor. Create it in the same UI context as the running shell.
        var window = Dispatcher.CurrentDispatcher.Invoke(() => new NoiseWindow(pipe), DispatcherPriority.Normal);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        try
        {
            Exercise(window, _ =>
            {
                var start = Button(window, "StartButton");
                string Label(string id) => ((TextBlock)window.FindName(id)).Text;
                void Submit() => Click(window, "StartButton");
                void ExpectEnded()
                {
                    try { PumpUntil(() => start.IsVisible && start.IsEnabled, "start action did not become available"); }
                    catch (InvalidOperationException error)
                    {
                        throw new InvalidOperationException(error.Message + " / " + Label("ManualHeading") + " / " +
                            Label("ManualActionHint") + " / " + Label("StateText") + " / " + Label("MessageText"), error);
                    }
                }
                ExpectEnded();
                Submit();
                PumpUntil(() => start.IsEnabled && Label("MessageText").Contains("无法核实操作权限"), "status failure silently allowed a start");
                Volatile.Write(ref mode, "deny");
                Submit();
                PumpUntil(() => start.IsEnabled && Label("MessageText").Contains("学校已禁止"), "school denial not visible");
                Assert(writes.IsEmpty, "denial/status failure dispatched a grant or manual command");
                Checks.Add("A failed v4 status and an explicit school denial emit no grant or start.");

                // Use the normal reservation fields and focus the same buttons as a classroom operator.
                window.MinHeight = 0; window.Width = 840; window.Height = 480;
                ((RadioButton)window.FindName("UseStart")).IsChecked = true;
                ((DatePicker)window.FindName("ManualStartDate")).SelectedDate = DateTime.Today.AddDays(1);
                ((TextBox)window.FindName("ManualStartTime")).Text = "09:15";
                Volatile.Write(ref mode, "both");
                var wait = new Stopwatch();
                Exception? modalFailure = null;
                bool cancel = true;
                timer.Tick += (_, _) =>
                {
                    var dialog = Application.Current.Windows.OfType<NoiseManagementDialog>().FirstOrDefault(d => d.IsVisible);
                    if (dialog is null) return;
                    try
                    {
                        if (cancel) { dialog.Close(); return; }
                        if (!wait.IsRunning)
                        {
                            wait.Start();
                            var text = ExitVisuals(dialog).OfType<TextBlock>().ToArray();
                            Assert(dialog.Title.Contains("预约手动自习") &&
                                text.Any(t => t.Text.Contains("09:15") && t.Text.Contains("同时验证")) &&
                                text.Any(t => t.Text.Contains("预约受理后即可拔出")),
                                "reservation dialog lost the time and school-required factors");
                            var scroll = (ScrollViewer)dialog.Content;
                            Assert(scroll.ComputedVerticalScrollBarVisibility != Visibility.Visible && scroll.ScrollableHeight == 0,
                                $"reservation fits but shows a scrollbar: {scroll.ExtentHeight:F1} / {scroll.ViewportHeight:F1}");
                            Snapshot(dialog, "02-narrow-reservation-verification.png");
                            double originalLimit = dialog.MaxHeight;
                            dialog.MaxHeight = dialog.ActualHeight - 72;
                            dialog.UpdateLayout();
                            PumpUntil(() => scroll.ScrollableHeight > 0 && scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible,
                                "a genuinely short reservation did not allow scrolling");
                            scroll.ScrollToEnd();
                            PumpUntil(() => scroll.VerticalOffset >= scroll.ScrollableHeight - 1, "short reservation cannot reach the actions");
                            dialog.MaxHeight = originalLimit;
                            dialog.UpdateLayout();
                        }
                        if (wait.Elapsed.TotalSeconds < 5.3) return;
                        timer.Stop();
                        ExitVisuals(dialog).OfType<PasswordBox>().Single(b => b.IsVisible).Password = "isolated-classroom-pass";
                        var submit = ExitVisuals(dialog).OfType<Button>().Single(b => b.IsDefault);
                        submit.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    }
                    catch (Exception ex) { modalFailure = ex; timer.Stop(); dialog.Close(); }
                };
                timer.Start(); Submit();
                PumpUntil(() => start.IsEnabled && Label("MessageText").Contains("预约未提交"), "credential dialog cancel did not keep original reservation");
                Assert(writes.IsEmpty, "cancelling sent a grant or reservation");
                cancel = false; Submit();
                PumpUntil(() => modalFailure is not null || Label("ManualHeading").Contains("已预约"), "reservation did not survive verification beyond five seconds", 10);
                if (modalFailure is not null) throw modalFailure;
                var issued = writes.ToArray();
                Assert(issued is [{ NoiseManagement: { Purpose: "start-manual-study" } grant }, { ManualStudy: { Action: "start" } command }] &&
                    grant.ClassroomPolicyVersion == version && grant.TargetRequestId == issued[1].RequestId && grant.ManualStudyTarget == command &&
                    issued[1].NoiseAuthorization == ticket && command.DeviceId == device.Id && command.StartUtc is not null,
                    "verification or sent ticket escaped its original reservation, device, revision or request");
                Assert(Label("ManualStatus").Contains("尚未开始采集") && Button(window, "StopButton").IsEnabled,
                    "an accepted reservation claimed that capture had already started");
                Snapshot(window, "03-narrow-reservation-accepted.png");
                Checks.Add("Both-factor reservation keeps its request, device and time beyond the five-second transport timer. Cancel sends nothing.");
                Volatile.Write(ref mode, "deny");
                int readsBeforeEnd = Volatile.Read(ref policyReads);
                Click(window, "StopButton"); ExpectEnded();
                Assert(writes.Last() is { ManualStudy.Action: "end", NoiseAuthorization: null } && readsBeforeEnd == Volatile.Read(ref policyReads),
                    "cancelling an accepted reservation used initiation permission");
                Checks.Add("A later deny still allows cancellation of the original reservation without a policy read or ticket.");

                ((RadioButton)Find(window, "ManualStartNow")).IsChecked = true;
                Volatile.Write(ref mode, "allow");
                Submit();
                PumpUntil(() => Label("ManualHeading").Contains("正在启动"), "immediate initiation did not distinguish startup from sampling");
                var pair = writes.TakeLast(2).ToArray();
                Assert(pair is [{ NoiseManagement: { Secret: null, ManualStudyTarget.StartUtc: null } allowed }, { ManualStudy.Action: "start" }] &&
                    allowed.TargetRequestId == pair[1].RequestId && pair[1].NoiseAuthorization == ticket, "allow failed to obtain a scoped grant");
                Volatile.Write(ref manual, Volatile.Read(ref manual) with { State = "Active" });
                PumpUntil(() => Label("ManualHeading").Contains("进行中") && Label("LevelText").Contains("48"), "active signal and manual state did not appear together");
                window.Width = 1160; window.Height = 780; window.UpdateLayout();
                Snapshot(window, "01-wide-manual-study-active.png");
                Volatile.Write(ref mode, "deny"); readsBeforeEnd = Volatile.Read(ref policyReads);
                Click(window, "StopButton"); ExpectEnded();
                Assert(writes.Last() is { ManualStudy.Action: "end", NoiseAuthorization: null } && readsBeforeEnd == Volatile.Read(ref policyReads),
                    "ending actual manual sampling used initiation permission");
                Checks.Add("Allow authorizes only the immediate start; Active comes from Host status, and a subsequent deny leaves End available.");
                Volatile.Write(ref mode, "old");
                int priorWrites = writes.Count;
                Submit();
                PumpUntil(() => Label("ManualHeading").Contains("正在启动"), "old Host did not retain its ordinary unprotected manual start");
                Assert(writes.Count == priorWrites + 1 && writes.Last() is { ManualStudy.Action: "start", NoiseAuthorization: null },
                    "explicit UnknownCapability did not retain the old command");
                var revoked = Volatile.Read(ref manual) with { State = "Cancelled", Reason = "CLASSROOM_POLICY_CHANGED" };
                Assert(ManualStudyPresentation.Heading(revoked).Contains("预约已取消") && ManualStudyPresentation.Details(revoked).Contains("学校授权已变化"),
                    "revoked reservation hid the reason for not starting");
                Checks.Add("Only explicit UnknownCapability takes the old start route. A revoked reservation names the changed authorization.");
                window.Shutdown();
            });
        }
        finally
        {
            timer.Stop(); window.Shutdown(); stop.Cancel();
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
    }
}
