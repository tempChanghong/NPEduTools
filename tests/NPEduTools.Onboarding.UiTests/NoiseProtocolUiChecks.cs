using System.IO.Pipes;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunNoiseProtocolChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(System.Windows.Application.Current.Dispatcher));
        try
        {
            var failures = new List<Exception>();
            foreach (string operation in new[] { "initial-devices", "status", "select" })
                foreach (string invalid in new[] { "request-id", "version", "zero-length", "empty-message" })
                    try { RunNoiseProtocolCheck(operation, invalid); }
                    catch (Exception error) { failures.Add(new InvalidOperationException(operation + "/" + invalid + ": " + error.Message, error)); }
            if (failures.Count > 0) throw new AggregateException("Noise protocol recovery failed", failures);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void RunNoiseProtocolCheck(string operation, string invalid)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.noise-protocol." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var active = new NoiseState(Guid.NewGuid(), 2, "Active", "隔离本机监测中", "fixture", "合成麦克风",
            DateTimeOffset.UtcNow, -57, "Good", new(30, 29.6, 29.6 / 30, -78.6, -48.3, 0, 100),
            [new(1, -65, "Good"), new(1.5, -57, "Good")], SessionId: Guid.NewGuid(), SelectedDeviceId: "fixture");
        var initial = operation == "select" ? active with { State = "Stopped", CurrentDbfs = null, Message = "隔离已停止" } : active;
        NoiseDevice[] devices = [new("fixture", "合成麦克风")];
        var window = new NoiseWindow(pipe);
        Task? command = null;
        string Text(string name) => ((TextBlock)window.FindName(name)).Text;
        var microphone = (ComboBox)window.FindName("Microphone");
        async Task<HostRequest> Read()
        {
            await server.WaitForConnectionAsync(timeout.Token);
            return await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
        }
        try
        {
            Exercise(window, owner =>
            {
                try
                {
                    // The real constructor starts enumeration; no actual capture source exists.
                    var read = Read();
                    PumpUntil(() => read.IsCompleted, "initial noise device query did not arrive");
                    var request = read.GetAwaiter().GetResult();
                    Assert(request.Capability == "noise.devices", "unexpected initial noise query");
                    if (operation != "initial-devices")
                    {
                        var warmup = Protocol.WriteAsync(server, new HostResponse(Protocol.Version, request.RequestId,
                            "Succeeded", null, "合成设备", Noise: initial, NoiseDevices: devices), timeout.Token);
                        PumpUntil(() => warmup.IsCompleted && Text("SummaryText").Contains("29.6"), "valid initial noise state did not render");
                        warmup.GetAwaiter().GetResult();
                        server.Disconnect();
                        read = Read();
                        if (operation == "select")
                        {
                            Assert(Button(owner, "SaveDeviceButton").IsEnabled, "known stopped device cannot be saved");
                            command = (Task)typeof(NoiseWindow).GetMethod("RunAsync", flags)!.Invoke(window,
                                ["noise.command", new NoiseCommand("select", initial.InstanceId, initial.Revision, "fixture")])!;
                        }
                        PumpUntil(() => read.IsCompleted, "noise state or select request did not arrive");
                        request = read.GetAwaiter().GetResult();
                        Assert(request.Capability == (operation == "select" ? "noise.command" : "noise.status") &&
                            Protocol.Validate(request) is null, "unexpected noise request");
                        if (operation == "select")
                            Assert(request.Noise is { Action: "select" } selection && selection.InstanceId == initial.InstanceId &&
                                selection.Revision == initial.Revision, "device selection lost its instance/revision fence");
                    }
                    else
                    {
                        microphone.ItemsSource = devices; microphone.SelectedIndex = 0;
                        typeof(NoiseWindow).GetMethod("Render", flags | BindingFlags.DeclaredOnly)!.Invoke(window, [active]);
                    }
                    Assert(Text("SummaryText").Contains("29.6"), "isolated old statistics did not render");
                    Task reply = invalid switch
                    {
                        "zero-length" => server.WriteAsync(new byte[4], timeout.Token).AsTask(),
                        "empty-message" => Protocol.WriteAsync<object?>(server, null, timeout.Token),
                        _ => Protocol.WriteAsync(server, new HostResponse(
                            invalid == "version" ? Protocol.Version + 1 : Protocol.Version,
                            invalid == "request-id" ? Guid.NewGuid() : request.RequestId, "Succeeded", null, "无效隔离回执",
                            Noise: active with { CurrentDbfs = -10, Message = "不可采信的监测状态" },
                            NoiseDevices: [new("untrusted", "不可采信的麦克风")]), timeout.Token)
                    };
                    PumpUntil(() => reply.IsCompleted && (command is null || command.IsCompleted), "invalid noise response did not settle");
                    reply.GetAwaiter().GetResult();
                    if (command is not null)
                    {
                        Assert(!command.IsFaulted, "invalid receipt escaped device selection: " + command.Exception?.GetBaseException().Message);
                        command.GetAwaiter().GetResult();
                        Assert(!(bool)typeof(NoiseWindow).GetField("_busy", flags)!.GetValue(window)!, "invalid receipt retained the busy gate");
                    }
                    var idle = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
                    PumpUntil(() => idle.IsCompleted, "noise failure rendering did not settle");
                    Assert(Text("StateText").Contains("未知") && Text("LevelText") == "—" && Text("QualityText") == "未知",
                        "invalid receipt retained live sampling facts");
                    Assert(Text("SummaryTitle").Contains("未知") && !Text("SummaryText").Contains("29.6") &&
                        ((Canvas)owner.FindName("TrendCanvas")).Children.Count == 0 &&
                        Text("DisplayStatusText").Contains("未知"), "invalid receipt retained old summary, trend or display facts");
                    Assert(!Button(owner, "StartButton").IsEnabled && !Button(owner, "StopButton").IsEnabled &&
                        !Button(owner, "SaveDeviceButton").IsEnabled && microphone.SelectedValue as string == "fixture",
                        "invalid receipt enabled capture controls or applied untrusted devices");
                    if (operation == "status" && invalid == "request-id")
                    {
                        owner.Width = 650; owner.Height = 560;
                        ((ScrollViewer)owner.Content).ScrollToBottom(); owner.UpdateLayout();
                        Snapshot(owner, "noise-protocol-unknown-statistics.png");
                    }
                    server.Disconnect();
                    var recovery = Read();
                    PumpUntil(() => recovery.IsCompleted, "noise polling did not recover");
                    var fresh = recovery.GetAwaiter().GetResult();
                    Assert(fresh.Capability == (operation == "initial-devices" ? "noise.devices" : "noise.status") &&
                        fresh.Noise is null, "recovery replayed a command or lost the enumeration stage");
                    var now = active with { Revision = 3, CurrentDbfs = -42, Message = "隔离最新监测状态",
                        Summary = new(40, 39.6, .99, -60, -30, 0, 150), Trend = [new(39, -42, "Good")] };
                    var restored = Protocol.WriteAsync(server, new HostResponse(Protocol.Version, fresh.RequestId,
                        "Succeeded", null, "隔离恢复", Noise: now, NoiseDevices: operation == "initial-devices" ? devices : null), timeout.Token);
                    PumpUntil(() => restored.IsCompleted && Text("LevelText") == "-42.0", "fresh noise state did not render");
                    restored.GetAwaiter().GetResult();
                    Assert(Text("SummaryText").Contains("39.6") && Button(owner, "StopButton").IsEnabled &&
                        !Button(owner, "StartButton").IsEnabled && Text("StateHintText") == "",
                        "fresh noise state failed to restore current statistics or controls");
                    if (operation == "status" && invalid == "request-id") Snapshot(owner, "noise-protocol-recovered-statistics.png");
                    Checks.Add(operation + "/" + invalid + ": unknown clears old sampling facts; the existing read-only poll recovers without repeating device selection or starting capture");
                }
                finally { window.Shutdown(); }
            });
        }
        finally
        {
            window.Shutdown();
            if (command?.IsFaulted == true) _ = command.Exception;
        }
    }
}
