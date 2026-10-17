using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunCalibrationOrderingChecks(bool reference)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            foreach (string scenario in new[] { "revision", "instance", "cancelled", "unchanged" })
            {
                CheckCalibrationBeginConfirmation(reference, false, scenario);
                if (reference) CheckCalibrationBeginConfirmation(true, true, scenario);
            }
            foreach (bool oldFailure in new[] { false, true }) CheckCalibrationOrdering(reference, oldFailure);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void CheckCalibrationOrdering(bool reference, bool oldFailure)
    {
        string pipe = "NPEduTools.Test.calibration-order." + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        NamedPipeServerStream Server() => new(pipe, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        async Task<HostRequest> Read(NamedPipeServerStream server)
        { await server.WaitForConnectionAsync(deadline.Token); return await Protocol.ReadAsync<HostRequest>(server, deadline.Token); }
        var instance = Guid.NewGuid(); var draft = Guid.NewGuid();
        var oldReference = new NoiseReferenceState(instance, 10, "Ready", "fixture-mic", "Fixture microphone", 0, 0, null,
            new(false, null, null, "old evaluation"), null, false, null);
        var newReference = oldReference with { Revision = 11, Draft = new(draft, "fixture-mic", "fixture", "Slow", 2, 2, null, []) };
        var oldQuick = new NoiseCalibrationState(instance, 10, null, "fixture-mic", "context", "Ready", 0, [],
            new(false, null, null, "old suggestion"), null, false, null);
        var newQuick = oldQuick with { Revision = 11, DraftId = draft };
        Window window = reference ? new NoiseReferenceWindow(pipe) : new NoiseCalibrationWindow(pipe);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var type = window.GetType();
        Task Invoke(string name, params object?[] arguments) => (Task)type.GetMethod(name, flags)!.Invoke(window, arguments)!;
        HostResponse Reply(HostRequest r, bool updated) => new(Protocol.Version, r.RequestId, "Succeeded", null,
            updated ? "fresh operation confirmed" : "old poll", NoiseReference: reference ? updated ? newReference : oldReference : null,
            NoiseCalibration: reference ? null : updated ? newQuick : oldQuick);
        try
        {
            type.GetMethod("Render", flags, [reference ? typeof(NoiseReferenceState) : typeof(NoiseCalibrationState)])!.Invoke(window, [reference ? (object)oldReference : oldQuick]);
            type.GetField("_connected", flags)!.SetValue(window, true);
            type.GetMethod("UpdateButtons", flags)!.Invoke(window, null);
            ((PasswordBox)window.FindName("ManagementSecret")).Password = "fixture-admin";
            if (reference)
            {
                ((TextBox)window.FindName("Stability")).Text = "1";
                ((TextBox)window.FindName("CheckTolerance")).Text = "1";
            }
            using var access = Server(); using var status = Server();
            var readStatus = Read(status);
            async Task ReplyAccess()
            {
                var r = await Read(access); Assert(r.Capability == "noise.management.status", "unexpected access query");
                await Protocol.WriteAsync(access, new HostResponse(1, r.RequestId, "Succeeded", null, "fixture access"), deadline.Token);
            }
            var accessReply = ReplyAccess(); var poll = Invoke("ReadAsync");
            PumpUntil(() => readStatus.IsCompleted, "old calibration query did not arrive"); accessReply.GetAwaiter().GetResult();
            using var command = Server();
            async Task ReplyCommand()
            {
                var r = await Read(command); Assert(r.Capability == (reference ? "noise.reference.command" : "noise.calibration.command"), "unexpected calibration command");
                await Protocol.WriteAsync(command, Reply(r, true), deadline.Token);
            }
            var operationReply = ReplyCommand();
            var method = type.GetMethod("SendAsync", flags)!;
            var arguments = method.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray(); arguments[0] = "begin";
            var operation = (Task)method.Invoke(window, arguments)!;
            PumpUntil(() => operation.IsCompleted && operationReply.IsCompleted, "new calibration operation did not finish");
            operation.GetAwaiter().GetResult(); operationReply.GetAwaiter().GetResult();
            Assert(((TextBlock)window.FindName("OperationMessage")).Text == "fresh operation confirmed", "fresh operation was not shown");
            if (oldFailure) status.Disconnect();
            else
            {
                var write = Protocol.WriteAsync(status, Reply(readStatus.Result, false), deadline.Token);
                PumpUntil(() => write.IsCompleted, "old status write did not finish"); write.GetAwaiter().GetResult();
            }
            PumpUntil(() => poll.IsCompleted, "old calibration poll did not finish"); poll.GetAwaiter().GetResult();
            var latest = type.GetField("_state", flags)!.GetValue(window)!;
            Assert((long)latest.GetType().GetProperty("Revision")!.GetValue(latest)! == 11 &&
                ((TextBlock)window.FindName("OperationMessage")).Text == "fresh operation confirmed" && Button(window, "BeginDraft").IsEnabled,
                $"late {(oldFailure ? "failure" : "status")} replaced the newer {(reference ? "reference" : "quick")} calibration operation");
            Checks.Add($"{(reference ? "Reference/USB" : "Quick")} calibration: old poll {(oldFailure ? "failure" : "response")} cannot replace a newer operation; actual private Pipe and WPF handlers, no devices");
        }
        finally { window.Close(); }
    }
}
