using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunRemoteExamRecoveryChecks()
    {
        var failures = new List<Exception>();
        foreach (bool paused in new[] { false, true })
        foreach (string invalid in new[] { "request-id", "version", "zero-length", "empty-message", "disconnected" })
        {
            try { RunRemoteExamRecoveryCheck(paused, invalid); }
            catch (Exception error) { failures.Add(new InvalidOperationException($"Remote check paused={paused}/{invalid}: {error.Message}", error)); }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    private static void RunRemoteExamRecoveryCheck(bool paused, string invalid)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.remote-inspection." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var window = IsolatedMainWindow(pipe);
        window.Width = 960; window.Height = 780;
        var context = new QuickEventContext();
        var previous = SynchronizationContext.Current;
        Exception? eventError = null;
        var requests = new List<HostRequest>();
        var history = (RemoteExamHistoryView)window.FindName("RemoteExamHistory");
        var initial = new RemoteExamStatus(new(1, true, null, Guid.NewGuid(), Guid.NewGuid(), true,
            "隔离学校许可", Binding: "隔离旧学校绑定"), 7, paused, paused ? Guid.NewGuid() : null, null,
            [new(Guid.NewGuid(), "SUCCEEDED", "Verify", null, DateTimeOffset.Now, null)]);
        string Text(string name) => ((TextBlock)window.FindName(name)).Text;
        RemoteExamStatus? State() => (RemoteExamStatus?)typeof(MainWindow).GetField("_remoteExamState", flags)!.GetValue(window);
        void Apply(RemoteExamStatus state) => typeof(MainWindow).GetMethod("ApplyRemoteExam", flags)!.Invoke(window, [state]);
        void OnError(object sender, DispatcherUnhandledExceptionEventArgs args)
        { eventError = args.Exception; args.Handled = true; } // Test-only observation; never suppress production errors.
        async Task<HostRequest> Read()
        {
            await server.WaitForConnectionAsync(timeout.Token);
            return await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
        }
        void Inspect(string responseKind, RemoteExamStatus result)
        {
            if (server.IsConnected) server.Disconnect();
            var read = Read();
            bool inspecting = State()?.AutomaticPaused == true;
            SynchronizationContext.SetSynchronizationContext(context);
            typeof(MainWindow).GetMethod("RemoteExamCheckClicked", flags)!.Invoke(window,
                [Button(window, "RemoteExamCheckButton"), new RoutedEventArgs()]);
            Assert(context.Active == 1, "inspection event did not start");
            Assert(!Button(window, "RemoteExamCheckButton").IsEnabled && !Button(window, "RemoteExamEndButton").IsEnabled,
                "inspection did not disable conflicting controls");
            PumpUntil(() => read.IsCompleted, "inspection did not reach the fake Host");
            var request = read.GetAwaiter().GetResult(); requests.Add(request);
            Assert(request.Capability == (inspecting ? "remoteexam.inspect" : "remoteexam.preflight") &&
                request.RemoteExam is null && Protocol.Validate(request) is null && !Protocol.NoiseInterruption(request),
                "inspection fixture requested a mutation or authorization dialog");
            if (responseKind == "disconnected") server.Disconnect();
            else
            {
                Task reply = responseKind switch
                {
                    "zero-length" => server.WriteAsync(new byte[4], timeout.Token).AsTask(),
                    "empty-message" => Protocol.WriteAsync<HostResponse?>(server, null, timeout.Token),
                    _ => Protocol.WriteAsync(server, new HostResponse(responseKind == "version" ? Protocol.Version + 1 : Protocol.Version,
                        responseKind == "request-id" ? Guid.NewGuid() : request.RequestId, "Succeeded", null,
                        "隔离检查回执", RemoteExam: result), timeout.Token)
                };
                PumpUntil(() => reply.IsCompleted, "inspection reply did not finish"); reply.GetAwaiter().GetResult();
            }
            PumpUntil(() => context.Active == 0, "inspection event did not settle");
            var idle = Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
            PumpUntil(() => idle.IsCompleted, "inspection exception dispatch did not settle");
            Assert(eventError is null, "inspection raised an unhandled event exception: " + eventError?.Message);
        }
        Application.Current.DispatcherUnhandledException += OnError;
        try
        {
            Exercise(window, owner =>
            {
                try
                {
                    ((FrameworkElement)owner.FindName("HomePage")).Visibility = Visibility.Collapsed;
                    ((FrameworkElement)owner.FindName("SettingsPage")).Visibility = Visibility.Visible;
                    ((FrameworkElement)owner.FindName("NpepSettingsPanel")).Visibility = Visibility.Visible;
                    // Keep the screenshot focused on the real remote-exam card; the unrelated shared control is unbound.
                    var panel = (StackPanel)owner.FindName("NpepSettingsPanel");
                    ((FrameworkElement)panel.Children[0]).Visibility = Visibility.Collapsed;
                    ((FrameworkElement)panel.Children[1]).Visibility = Visibility.Collapsed;
                    Apply(initial);
                    Inspect("valid", initial);
                    Assert(State()?.RuntimeRevision == initial.RuntimeRevision && State()?.Policy.ControlEpoch == initial.Policy.ControlEpoch &&
                        State()?.AutomaticPaused == paused && Button(owner, "RemoteExamEndButton").IsEnabled == paused,
                        "valid initial inspection did not establish the expected clearance eligibility");
                    Assert(((ItemsControl)history.FindName("HistoryItems")).Items.Count == 1,
                        "known synthetic history did not render");
                    Inspect(invalid, initial with { RuntimeRevision = 99 });
                    Assert(State() is null && Text("RemoteExamRuntimeMessage").Contains("状态未知"),
                        "inspection failure retained obsolete runtime state");
                    Assert(!Text("RemoteExamBinding").Contains("隔离旧学校绑定") &&
                        ((FrameworkElement)owner.FindName("RemoteExamRuntimeDetailsPanel")).Visibility == Visibility.Collapsed &&
                        !((TextBox)owner.FindName("RemoteExamRuntimeDetails")).Text.Contains(initial.PauseOperationId?.ToString() ?? "impossible"),
                        "inspection failure retained old binding or pause details");
                    Assert(((ItemsControl)history.FindName("HistoryItems")).Items.Count == 0 &&
                        ((TextBlock)history.FindName("EmptyHistory")).Text.Contains("无法确认"),
                        "inspection failure retained historical rows as a readable current snapshot");
                    Assert(Text("RemoteExamCheckMessage").Contains("检查结果未读取") &&
                        Button(owner, "RemoteExamCheckButton").IsEnabled && !Button(owner, "RemoteExamEndButton").IsEnabled &&
                        typeof(MainWindow).GetField("_remoteExamInspectedRevision", flags)!.GetValue(window) is null &&
                        typeof(MainWindow).GetField("_remoteExamInspectedEpoch", flags)!.GetValue(window) is null,
                        "failed inspection claimed success, retained clearance eligibility or blocked retry");
                    if (paused && invalid == "request-id")
                    {
                        owner.UpdateLayout();
                        var scroll = (ScrollViewer)owner.FindName("SettingsScroll");
                        scroll.ScrollToVerticalOffset(scroll.VerticalOffset +
                            ((FrameworkElement)owner.FindName("RemoteExamRuntimeMessage")).TranslatePoint(new Point(0, 0), scroll).Y - 12);
                        owner.UpdateLayout();
                        Snapshot(owner, "remote-inspection-unconfirmed.png");
                    }
                    if (server.IsConnected) server.Disconnect();
                    var read = Read();
                    var refresh = (Task)typeof(MainWindow).GetMethod("RefreshRemoteExamAsync", flags)!.Invoke(window, null)!;
                    PumpUntil(() => read.IsCompleted, "fresh status query did not connect");
                    var request = read.GetAwaiter().GetResult(); requests.Add(request);
                    Assert(request.Capability == "remoteexam.status" && request.RemoteExam is null, "recovery replayed a mutation or an inspection");
                    var fresh = initial with { Policy = initial.Policy with { ControlEpoch = Guid.NewGuid(), Binding = "隔离新学校绑定" },
                        RuntimeRevision = 8, AutomaticPaused = true, PauseOperationId = Guid.NewGuid(), History = [] };
                    var response = Protocol.WriteAsync(server, new HostResponse(Protocol.Version, request.RequestId,
                        "Succeeded", null, "隔离恢复状态", RemoteExam: fresh), timeout.Token);
                    PumpUntil(() => response.IsCompleted && refresh.IsCompleted, "fresh status recovery did not settle");
                    response.GetAwaiter().GetResult(); refresh.GetAwaiter().GetResult();
                    Assert(State()?.RuntimeRevision == fresh.RuntimeRevision && State()?.Policy.ControlEpoch == fresh.Policy.ControlEpoch &&
                        State()?.PauseOperationId == fresh.PauseOperationId && !Button(owner, "RemoteExamEndButton").IsEnabled,
                        "fresh status was ignored or reused the old inspection grant");
                    Inspect("valid", fresh);
                    Assert(Button(owner, "RemoteExamEndButton").IsEnabled && Text("RemoteExamCheckMessage").Contains("当前考试状态已核实"),
                        "explicit fresh inspection failed to restore clearance eligibility");
                    Assert(requests.Count == 4 && requests.Select(r => r.RequestId).Distinct().Count() == 4 &&
                        requests.All(r => r.Capability is "remoteexam.status" or "remoteexam.inspect" or "remoteexam.preflight" &&
                            r.RemoteExam is null && Protocol.Validate(r) is null), "recovery sent an unexpected command or reused request identity");
                    Checks.Add($"Remote {(paused ? "inspection" : "preflight")}/{invalid}: unknown clears binding, pause details and history; fresh status plus explicit inspection restores eligibility without a mutation");
                }
                finally { window.Close(); }
            });
        }
        finally
        {
            window.Close();
            if (context.Active > 0) PumpUntil(() => context.Active == 0, "inspection fixture did not stop");
            Application.Current.DispatcherUnhandledException -= OnError;
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
