using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunQuickRecordingControlChecks(nint slide)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        const uint down = 0x10006, move = 0x20006, up = 0x40000;
        bool nativeInput = slide != 0;
        string pipe = "NPEduTools.Test.quick-recording-controls." + Guid.NewGuid().ToString("N");
        var original = new RecorderControl("Automatic", Guid.NewGuid(), "period-one", 0, 1);
        var later = new RecorderControl("Manual", Guid.NewGuid(), "", 0, 1);
        var next = new RecorderControl("Manual", Guid.NewGuid(), "", 0, 1);
        var backend = new RecordingState("Recording", "第一场录课", Seconds: 31, Control: original);
        void Publish(RecordingState state) => Volatile.Write(ref backend, state);
        var automatic = new AutomaticRecordingState(true, Guid.NewGuid(), "自动计划照常", null, []);
        var calls = new ConcurrentQueue<RecorderCommand>();
        var allowFirstStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int stops = 0, queries = 0, unavailable = 0;
        using var stop = new CancellationTokenSource();
        var server = Task.Run(() => WorkspaceProxy(pipe, async request =>
        {
            if (request.Capability == "recording.status")
            {
                Interlocked.Increment(ref queries);
                if (Volatile.Read(ref unavailable) != 0) throw new IOException("fixture disconnected");
            }
            if (request.Recording is { } command)
            {
                calls.Enqueue(command);
                if (command.Action == "resume") Publish(Volatile.Read(ref backend) with { Phase = "Recording" });
                if (command.Action == "stop")
                {
                    if (Interlocked.Increment(ref stops) == 1)
                        await allowFirstStop.Task.WaitAsync(stop.Token);
                    else
                    {
                        Volatile.Write(ref unavailable, 1);
                        throw new IOException("stop result lost after acceptance");
                    }
                }
            }
            if (request.Automatic is { Action: not "lease" }) throw new InvalidOperationException("quick stop must not change the plan");
            return new(Protocol.Version, request.RequestId, "Succeeded", null, "", Recording: Volatile.Read(ref backend), Automatic: automatic);
        }, stop.Token));

        var main = IsolatedMainWindow(pipe);
        var quick = new QuickAccessWindow(pipe, () => { }, () => { }, () => { }, () => { }, _ => { }, () => { }, () => { });
        var quickField = typeof(MainWindow).GetField("_quick", flags)!;
        var capture = typeof(QuickAccessWindow).GetField("_recordingPress", flags)!;
        string preference = (string)typeof(QuickAccessWindow).GetField("_settingsPath", flags)!.GetValue(quick)!;
        quickField.SetValue(main, quick);
        var priorContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(main.Dispatcher));
        try
        {
            WorkspaceCall(main, "InitializeRecording");
            WorkspaceCall(main, "ConfigureQuickRecording");
            quick.SetSide(false);
            quick.SetDensity(.9);
            quick.Update(new TouchAssistState(true, false, false, "已开启"), "辅助已开启", true, "就绪", true);
            var pause = Button(quick, "RecordingPause");
            var stopButton = Button(quick, "RecordingStop");
            var confirm = Button(quick, "RecordingConfirmStop");
            var back = Button(quick, "RecordingCancelStop");
            var ppt = Button(quick, "PauseButton");
            var status = (TextBlock)quick.FindName("RecordingStatus");
            var prompt = (TextBlock)quick.FindName("RecordingConfirmationText");
            PumpUntil(() => status.Text.Contains("第一场录课"), "recording status did not reach the normal quick card");
            quick.ShowEntry();
            quick.UpdateLayout();
            var rail = new WindowInteropHelper(quick).Handle;
            if (nativeInput)
            {
                ClickFixturePoint(rail, quick.PointToScreen(QuickHandleCenter(quick)), slide,
                    () => PumpUntil(() => HasForeground(quick) && pause.IsVisible, "ordinary handle did not open the recording card"));
                Assert(InitializeTouchInjection(1, 3), "Windows touch injection unavailable: " + Marshal.GetLastWin32Error());
            }
            else
            {
                Click(quick, "EdgeHandle");
                Assert(pause.IsVisible, "ordinary handle did not open the recording card");
            }
            Snapshot(quick, "01-recording-wide.png");

            quick.SetDensity(.8);
            CapHeight();
            var pptPosition = ppt.PointToScreen(new Point());
            var stopBounds = RelativeBounds(stopButton, quick);
            var stopPoint = CenterOnPanel(stopButton);
            Tap(stopButton);
            PumpUntil(() => confirm.IsVisible, "first stop tap did not open the in-card confirmation");
            Assert(prompt.Text.Contains("后续自动录课照常") && GeometryIsReachable(confirm) && GeometryIsReachable(back) &&
                !RelativeBounds(confirm, quick).IntersectsWith(stopBounds) &&
                RelativeBounds(back, quick).Contains(stopBounds.Location) && ppt.PointToScreen(new Point()) == pptPosition,
                "confirmation overlapped the old stop target, moved PPT or failed to fit the first screen");
            Snapshot(quick, "02-stop-confirmation-short.png");
            if (nativeInput) TapAt(stopPoint);
            else Tap(back);
            PumpUntil(() => !confirm.IsVisible && stopButton.IsVisible, "a repeat tap on the old stop target did not return safely");
            Assert(stops == 0 && (!nativeInput || stopButton.IsKeyboardFocused),
                "the repeat tap issued a stop or left focus on the hidden confirmation");
            Checks.Add($"The original stop target and in-card confirm never overlap ({VisualTreeHelper.GetDpi(quick).PixelsPerInchX} DPI). The return action sends no stop.");

            var point = nativeInput ? Hold(pause) : default;
            if (!nativeInput)
            {
                RaiseKey(pause, Keyboard.PreviewKeyDownEvent);
                Assert(capture.GetValue(quick) is not null, "the WPF key did not latch the visible pause action");
            }
            try
            {
                Publish(Volatile.Read(ref backend) with { Phase = "Paused" });
                PumpUntil(() => Equals(pause.Content, "继续"), "fresh paused state did not reach the held button");
            }
            finally
            {
                if (nativeInput) SendTouch(up, point);
                else RaiseKey(pause, Keyboard.PreviewKeyUpEvent);
            }
            Pause(68);
            Assert(calls.IsEmpty, "releasing the old Pause press sent Resume");

            if (nativeInput)
            {
                point = Hold(pause);
                try
                {
                    point.X += 112;
                    SendTouch(move, point);
                    Pause(32);
                }
                finally { SendTouch(up, point); }
                Pause(68);
                Assert(calls.IsEmpty, "sliding outside released a stale recording action");
            }
            Tap(pause);
            PumpUntil(() => calls.Count == 1 && Equals(pause.Content, "暂停") && pause.IsEnabled,
                "a fresh resume failed to change the original recording");
            Assert(calls.Single() is { Action: "resume", Control: { } resumed } && original.Matches(resumed),
                "resume targeted a different recording");

            Tap(stopButton);
            PumpUntil(() => confirm.IsVisible, "stop did not re-open confirmation");
            Publish(new("Recording", "第二场录课", Seconds: 11, Control: later));
            PumpUntil(() => !confirm.IsVisible && status.Text.Contains("第二场录课"), "the old confirmation survived a session rollover");
            Assert(stops == 0, "changing the session committed the old confirmation");
            Checks.Add(nativeInput
                ? "Native touch release cancels on phase reversal, outside motion and session rollover. A fresh Resume carries the exact original control."
                : "WPF key release cancels on phase reversal; session rollover dismisses confirmation. A fresh Resume carries the exact original control.");

            StartStop();
            PumpUntil(() => Volatile.Read(ref stops) == 1 && status.Text.Contains("正在请求停止"), "pending stop claimed save before the Host responded");
            Assert(!pause.IsEnabled && !stopButton.IsEnabled, "a second command can be issued while the original stop is outstanding");
            allowFirstStop.TrySetResult();
            int readBeforeSaving = Volatile.Read(ref queries);
            PumpUntil(() => Volatile.Read(ref queries) >= readBeforeSaving + 2,
                "the accepted stop did not continue observing the worker");
            Assert(status.Text.Contains("正在请求停止"), "acceptance was presented as saving before the worker reports it");
            Publish(Volatile.Read(ref backend) with { Phase = "Saving", Message = "正在写出" });
            PumpUntil(() => status.Text.Contains("正在保存本场"), "matching saving state was not shown");
            CapHeight();
            Snapshot(quick, "03-recording-saving-short.png");
            Publish(Volatile.Read(ref backend) with { Phase = "Saved", Message = "曾称已保存" });
            PumpUntil(() => status.Text.Contains("待核实"), "Saved without an output path was reported as a verified save");
            var verifiedOutput = Path.Combine(_output, "fixture-output.mp4");
            Publish(Volatile.Read(ref backend) with { OutputFile = verifiedOutput });
            PumpUntil(() => status.Text == "本场录课已保存", "verified completion for the bound session was not shown");

            Publish(new("Recording", "第三场录课", Seconds: 5, Control: next));
            PumpUntil(() => status.Text.Contains("第三场录课") && stopButton.IsEnabled, "new live recording did not win over the completed recording");
            int readBeforeLate = Volatile.Read(ref queries);
            Publish(new("Saved", "迟到的第二场回执", Control: later, OutputFile: verifiedOutput));
            PumpUntil(() => Volatile.Read(ref queries) >= readBeforeLate + 2, "late snapshots were not requested");
            Assert(status.Text.Contains("第三场录课") && stopButton.IsEnabled, "a late save receipt replaced the next recording");
            Publish(new("Recording", "第三场录课", Seconds: 7, Control: next));
            Checks.Add("Requesting, backend Saving, missing output and confirmed Saved were distinguished. Two stale reads could not replace the later live recording.");

            StartStop();
            PumpUntil(() => Volatile.Read(ref stops) == 2 && status.Text.Contains("待核实"), "a lost stop response did not show a result to verify");
            Assert(!pause.IsEnabled && !stopButton.IsEnabled, "disconnect enabled unsafe writes");
            quick.Collapse();
            if (nativeInput) PumpUntil(() => QuickForeground() == slide, "collapse did not return to PowerPoint");
            quick.OpenPanel();
            CapHeight();
            var taskStack = (Panel)quick.FindName("TaskStack");
            Assert(status.Text.Contains("待核实") && stops == 2 &&
                taskStack.Children[0] == quick.FindName("RecordingCard") &&
                Equals(status.Foreground, status.FindResource("TeacherWarningBrush")),
                "reopening replayed or obscured the original stop review");
            Snapshot(quick, "04-recording-unconfirmed-short.png");
            Publish(new("Saved", "原场次已经保存", Control: next, OutputFile: verifiedOutput));
            Volatile.Write(ref unavailable, 0);
            PumpUntil(() => status.Text == "本场录课已保存", "read-only reconciliation did not find the original recording");
            Assert(calls.Select(command => (command.Action, command.Control?.SessionId)).SequenceEqual(
                new[] { ("resume", (Guid?)original.SessionId), ("stop", (Guid?)later.SessionId), ("stop", (Guid?)next.SessionId) }),
                "a stop was duplicated or was routed to the wrong session");
            Checks.Add("Lost response survived close and reopen. A read-only reconnect confirmed the same session with exactly one command and no plan mutation.");

            void StartStop()
            {
                Tap(stopButton);
                PumpUntil(() => confirm.IsVisible, "ordinary stop did not offer confirmation");
                Tap(confirm);
            }

            void CapHeight()
            {
                // Let any queued DPI reposition and the panel's opening fade finish
                // before installing a temporary work-area constraint on the HWND.
                Pause(180);
                var area = System.Windows.Forms.Screen.FromHandle(rail).WorkingArea;
                Assert(ReadQuickTestRect(rail, out var native), "recording panel had no native bounds");
                int height = Math.Min(600, area.Height);
                Assert(ResizeQuickTestWindow(rail, 0, native.Left, Math.Clamp(native.Top, area.Top, area.Bottom - height),
                    native.Right - native.Left, height, 0x14), "could not set the short office screen fixture");
                var panel = (FrameworkElement)quick.FindName("PanelSurface");
                PumpUntil(() =>
                {
                    quick.UpdateLayout();
                    return panel.IsVisible && ReadQuickTestRect(rail, out var capped) && capped.Bottom - capped.Top == height &&
                        Math.Abs(quick.ActualHeight * VisualTreeHelper.GetDpi(quick).DpiScaleY - height) <= 1;
                }, "the actual panel did not settle at the short work area height");
            }

            bool GeometryIsReachable(Button button)
            {
                var bounds = RelativeBounds(button, quick);
                return button.IsVisible && bounds.Height >= 43.5 && bounds.Bottom <= quick.ActualHeight &&
                    (!nativeInput || WindowFromPoint(ScreenCenter(button)) == rail);
            }

            CursorPoint CenterOnPanel(Button button)
            {
                Assert(button.IsVisible && button.IsEnabled, "touch target is unavailable: " + button.Name);
                return ScreenCenter(button);
            }

            CursorPoint Hold(Button button)
            {
                var target = CenterOnPanel(button);
                Assert(WindowFromPoint(target) == rail && HasForeground(quick), "touch would leave the fixture");
                SendTouch(down, target);
                try
                {
                    for (int frame = 0; frame < 12 && capture.GetValue(quick) is null; frame++)
                    {
                        Pause(22);
                        target.X += frame % 2 == 0 ? 8 : -8;
                        SendTouch(move, target);
                    }
                    PumpUntil(() => capture.GetValue(quick) is not null, "the WPF button did not capture the held touch");
                    return target;
                }
                catch { SendTouch(up, target); throw; }
            }

            void TapAt(CursorPoint target)
            {
                Assert(WindowFromPoint(target) == rail && HasForeground(quick), "touch would leave the fixture");
                SendTouch(down, target);
                try { Pause(47); }
                finally { SendTouch(up, target); }
                Pause(90);
            }

            void Tap(Button button)
            {
                if (nativeInput) TapAt(CenterOnPanel(button));
                else Click(quick, button.Name);
            }

            void RaiseKey(Button button, RoutedEvent keyEvent)
            {
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(button)!, 0, Key.Space) { RoutedEvent = keyEvent };
                button.RaiseEvent(args);
            }
        }
        finally
        {
            quickField.SetValue(main, null);
            main.Close();
            quick.Shutdown();
            SynchronizationContext.SetSynchronizationContext(priorContext);
            File.Delete(preference);
            stop.Cancel();
            try { server.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }
    }
}
