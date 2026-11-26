using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Host;

internal static partial class Program
{
    private static void RunStudyDeviceWorkspaceChecks()
    {
        const string secret = "fixture-workspace-school";
        string directory = Path.Combine(_output, "isolated-host");
        var catalog = new NoiseDevice[] { new("mic-a", "讲台原麦克风"), new("mic-b", "同名阵列麦克风"), new("mic-c", "同名阵列麦克风") };
        bool cameraFault = false;
        int captures = 0, cameraWrites = 0, dropReply = 1;
        var noise = new NoiseService(_ =>
        {
            Interlocked.Increment(ref captures);
            throw new InvalidOperationException("physical or formal capture is outside this fixture");
        }, () => catalog, directory: directory, management: new NoiseManagementStore(directory));
        var cameras = new StudyDeviceService(() => cameraFault
            ? throw new IOException("isolated camera catalog is unavailable") : [new("cam-a", "隔离模拟摄像头")], directory);
        var school = new SchoolManagementService(noise, cameras, directory);
        var identity = new JsonObject { ["deviceId"] = Guid.NewGuid().ToString() };
        school.BindSchool(identity.ToJsonString());
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(secret, salt, 210000, HashAlgorithmName.SHA256, 32);
        school.ApplySchool(new()
        {
            ["identity"] = identity,
            ["credential"] = new JsonObject { ["revision"] = 1L,
                ["verifier"] = new JsonObject { ["iterations"] = 210000, ["salt"] = Convert.ToBase64String(salt), ["hash"] = Convert.ToBase64String(hash) } },
            ["equipment"] = new JsonObject { ["revision"] = 1L, ["locked"] = true, ["microphoneId"] = "mic-a",
                ["cameraId"] = "cam-a", ["allowLocalMicrophoneChange"] = true }
        });
        Assert(noise.Snapshot().SelectedDeviceId == "mic-a" && cameras.Snapshot().CameraId == "cam-a",
            "school fixture did not save the initial equipment");
        catalog = [catalog[1], catalog[2]];
        cameraFault = true;
        var changes = new ConcurrentQueue<string>();
        string pipe = "NPEduTools.Test.device-workspace." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var server = Task.Run(() => WorkspaceProxy(pipe, request =>
        {
            if (request.Capability is "study.devices.refresh-microphone-policy" or "study.devices.refresh" or "study.devices.select")
            {
                if (request.Capability == "study.devices.select") Interlocked.Increment(ref cameraWrites);
                return Task.FromResult(cameras.Handle(request));
            }
            if (request.Capability == "noise.devices") return Task.FromResult(noise.Handle(request));
            if (request.NoiseManagement is { Action: "replace-microphone", DeviceId: { } target })
            {
                changes.Enqueue(target);
                var reply = noise.ManagementHandle(request);
                if (reply.Outcome == "Succeeded" && Interlocked.Exchange(ref dropReply, 0) == 1)
                    throw new IOException("fixture disconnected after the real Host saved the target");
                return Task.FromResult(reply);
            }
            return Task.FromResult(new HostResponse(1, request.RequestId, "Succeeded", null, "",
                Noise: noise.Snapshot(), Study: StudyFixture("Outside")));
        }, stop.Token, supportsSchoolMicrophonePolicy: true));
        var main = PrivateWorkspace(pipe);
        try
        {
            Exercise(main, _ =>
            {
                var hwnd = new WindowInteropHelper(main).Handle;
                Click(main, "StudyNav");
                Click(main, "SchoolWorkspaceTab");
                var schoolPage = WorkspaceField<StudyMonitoringWindow>(main, "_studyMonitoringWindow");
                var schoolEntry = Button(schoolPage, "DeviceWorkspaceEntry");
                var schoolStatus = (TextBlock)schoolPage.FindName("DeviceWorkspaceStatus");
                PumpUntil(() => schoolEntry.IsVisible && schoolStatus.Text.Contains("已选麦克风不可用"), "school summary did not expose the unavailable mic");
                Capture(840, 600, "school-entry-narrow", (ScrollViewer)schoolPage.FindName("PageScroll"), schoolEntry, schoolStatus);
                Click(schoolPage, "DeviceWorkspaceEntry");
                var picker = (StudyDevicePicker)((ContentControl)main.FindName("WorkspaceDevicesHost")).Content;
                var scroll = (ScrollViewer)main.FindName("WorkspaceDevicesScroll");
                var mic = Field<ComboBox>("StudyMicrophone");
                var camera = Field<ComboBox>("StudyCamera");
                var password = Field<PasswordBox>("EquipmentSecret");
                var confirm = Field<Button>("ConfirmSchoolMicrophone");
                var message = Field<TextBlock>("DeviceMessage");
                var status = Field<TextBlock>("MicrophoneStatus");
                var candidate = Field<TextBlock>("PendingMicrophoneStatus");
                var cameraStatus = Field<TextBlock>("CameraStatus");
                PumpUntil(() => mic.IsEnabled && Equals(mic.SelectedValue, "mic-a") && cameraStatus.Text.Contains("无法读取"), "device workspace did not read the actual saved/school state");
                Assert(ReferenceEquals(picker, schoolPage.FindName("Devices")) && new WindowInteropHelper(main).Handle == hwnd &&
                    new WindowInteropHelper(schoolPage).Handle == nint.Zero, "device workflow opened a second window or constructed a second picker");
                foreach (string id in new[] { "MinimizeWindow", "MaximizeRestoreWindow", "CloseWindow" })
                    Assert(Button(main, id, automation: true).IsVisible, "missing main caption control: " + id);
                var options = mic.Items.Cast<NoiseDevice>().ToDictionary(device => device.Id);
                Assert(options["mic-b"].Name != options["mic-c"].Name && status.Text.Contains("当前不可用") &&
                    !camera.IsVisible && cameraStatus.Text.Contains("原设置保留") && cameraStatus.Text.Contains("学校已锁定"),
                    "the disconnected saved device or camera fault is being shown as an editable or cleared choice");
                mic.SelectedValue = "mic-b";
                Assert(confirm.IsEnabled && candidate.Text.Contains(options["mic-b"].Name) && status.Text.Contains("当前不可用"),
                    "changing the candidate hides the actual saved state or camera failure blocks authorization");
                Capture(840, 600, "replace-narrow", scroll, status, mic, candidate, password, confirm);
                Assert(Grid.GetRow(Field<Border>("CameraPane")) == 2, "narrow layout put camera ahead of microphone replacement");
                Capture(1040, 760, "replace-wide", scroll, status, mic, candidate, password, confirm, cameraStatus);
                Assert(Grid.GetRow(Field<Border>("CameraPane")) == 1 && Grid.GetColumn(Field<Border>("CameraPane")) == 1,
                    "wide layout did not use a separate camera lane");
                Checks.Add("Original school summary and main HWND lead to the shared picker. Both viewports expose saved state, exact candidate and protected confirmation, independently of a failed locked camera catalog.");

                // Reorder identical labels while a candidate is still unsaved. Its stable ID must remain selected.
                catalog = [catalog[1], catalog[0]];
                Field<Button>("DeviceRefresh").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                PumpUntil(() => confirm.IsEnabled, "refresh lost the live replacement target");
                Assert(Equals(mic.SelectedValue, "mic-b") && noise.Snapshot().SelectedDeviceId == "mic-a" &&
                    ((NoiseDevice)mic.SelectedItem).Name == options["mic-b"].Name,
                    "refresh changed the candidate identity/label or wrote an unsaved edit");
                password.Password = secret;
                confirm.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                PumpUntil(() => noise.Snapshot().SelectedDeviceId == "mic-b" && message.Text.Contains("结果未确认"),
                    "the Host did not commit before the simulated lost reply, or the UI claimed completion");
                Assert(changes.SequenceEqual(["mic-b"]) && !confirm.IsEnabled && password.Password.Length == 0,
                    "a missing reply triggered another write, kept a secret, or allowed a stale save");
                Capture(840, 600, "receipt-unconfirmed-narrow", scroll, message);
                ReturnTo(schoolEntry);
                Click(schoolPage, "DeviceWorkspaceEntry");
                PumpUntil(() => status.Text.Contains("已保存") && !status.Text.Contains("不可用") && message.Text.Contains("当前保存设置与本次选择一致"),
                    "reopening did not read the original attempt against the actual saved device", 8);
                Assert(Equals(mic.SelectedValue, "mic-b") && changes.SequenceEqual(["mic-b"]) && !camera.IsVisible &&
                    cameraStatus.Text.Contains("无法读取") && cameras.Snapshot().CameraId == "cam-a" && cameraWrites == 0 && captures == 0,
                    "reopening replayed a command, changed the camera or started capture");
                Capture(840, 600, "receipt-recovered-narrow", scroll, status, message);
                ReturnTo(schoolEntry);
                Checks.Add("Reordered same-name entries keep the selected ID. One authenticated Host write survives loss of its reply and is reconciled by reopening and reading, with zero camera writes or captures.");

                Click(main, "ManualWorkspaceTab");
                var manualPage = WorkspaceField<NoiseWindow>(main, "_noiseWindow");
                ((Expander)manualPage.FindName("ManualDeviceSettings")).IsExpanded = true;
                var manualEntry = Button(manualPage, "ManualDeviceLink");
                Click(manualPage, "ManualDeviceLink");
                PumpUntil(() => picker.IsVisible && new WindowInteropHelper(main).Handle == hwnd, "manual entry did not open the same workspace");
                ReturnTo(manualEntry);
                Click(main, "ReportWorkspaceTab");
                var reportEntry = Button(main, "ReportDevicesButton");
                Click(main, "ReportDevicesButton");
                PumpUntil(() => picker.IsVisible, "report entry did not open the same picker");
                ReturnTo(reportEntry);
                Assert(captures == 0 && changes.Count == 1, "navigation touched capture or repeated device saves");
                Checks.Add("School, manual and report entries restore keyboard focus to their own control after returning.");
                main.Close();

                T Field<T>(string name) where T : FrameworkElement => (T)picker.FindName(name);
                void ReturnTo(Button anchor)
                {
                    Click(main, "WorkspaceDevicesBack");
                    PumpUntil(() => anchor.IsVisible && Keyboard.FocusedElement == anchor, "return lost the original entry or focus");
                }
                void Capture(int width, int height, string file, ScrollViewer viewport, params FrameworkElement[] fields)
                {
                    main.Width = width; main.Height = height; viewport.ScrollToTop(); main.UpdateLayout();
                    PumpUntil(() => Math.Abs(main.ActualWidth - width) < 2 && Math.Abs(main.ActualHeight - height) < 2, "wrong real window size");
                    Snapshot(main, file + ".png");
                    foreach (var field in fields)
                    {
                        var start = field.TranslatePoint(new Point(), viewport);
                        var end = field.TranslatePoint(new Point(field.ActualWidth, field.ActualHeight), viewport);
                        Assert(field.IsVisible && start.X >= -1 && start.Y >= -1 && end.X <= viewport.ViewportWidth + 1 &&
                            end.Y <= viewport.ViewportHeight + 1 && field.ActualHeight > 0,
                            $"{file}: {field.Name} [{start.X:F0},{start.Y:F0}–{end.X:F0},{end.Y:F0}] at {VisualTreeHelper.GetDpi(main).DpiScaleX:P0} exceeds the viewport");
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
    }
}
