using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunClassroomRejectedFeedbackCheck()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.classroom-feedback." + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var window = ClassroomWindow(pipe);
        var state = new ClassroomModeState(7, Message: "尚未设置课堂模式。");
        async Task Reply(string capability, string outcome, string? error, string message)
        {
            await server.WaitForConnectionAsync(deadline.Token);
            var request = await Protocol.ReadAsync<HostRequest>(server, deadline.Token);
            Assert(request.Capability == capability, "unexpected feedback fixture request");
            await Protocol.WriteAsync(server, new HostResponse(1, request.RequestId, outcome, error, message,
                ClassroomMode: state), deadline.Token);
        }
        try
        {
            Exercise(window, owner =>
            {
                var initial = Reply("classroom.status", "Succeeded", null, state.Message);
                PumpUntil(() => initial.IsCompleted && !ClassroomUnknown(window), "initial classroom state missing");
                initial.GetAwaiter().GetResult(); server.Disconnect();
                var response = Reply("classroom.refresh", "Rejected", "ClassroomStorageUnavailable", "隔离模式记录不可访问。");
                var command = (Task)typeof(NPEduTools.App.ClassroomModeWindow).GetMethod("SendAsync", flags)!
                    .Invoke(window, ["classroom.refresh", null, null, false])!;
                PumpUntil(() => response.IsCompleted && command.IsCompleted, "rejected verification did not settle");
                response.GetAwaiter().GetResult(); command.GetAwaiter().GetResult(); server.Disconnect();
                // Render is the same path used by the next ordinary status poll.
                ShowClassroom(window, state);
                var feedback = (TextBlock)window.FindName("OperationFeedbackText");
                Assert(feedback.Visibility == Visibility.Visible && feedback.Text.Contains("模式记录不可访问"),
                    "ordinary polling erased the rejected operation reason");
                Assert(((TextBlock)window.FindName("ActualClassIslandText")).Text.Contains("尚未核实"),
                    "rejected verification falsely confirmed startup state");
                Checks.Add("Rejected classroom verification retains a separate visible cause after ordinary status rendering; no mode mutation or real external tools");
                window.Shutdown();
            });
        }
        finally { window.Shutdown(); }
    }
}
