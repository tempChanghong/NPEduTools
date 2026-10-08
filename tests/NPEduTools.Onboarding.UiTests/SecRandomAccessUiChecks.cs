using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunSecRandomAccessChecks()
    {
        var failures = new List<Exception>();
        foreach (string entry in new[] { "poll", "command", "sidebar" })
            try { RunSecRandomAccessCheck(entry); }
            catch (Exception error) { failures.Add(new InvalidOperationException("SecRandom access/" + entry + ": " + error.Message, error)); }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    private static void RunSecRandomAccessCheck(string entry)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        string pipe = "NPEduTools.Test.secrandom-access." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // A write-only server rejects the real HostClient's duplex open with UnauthorizedAccessException.
        // This reproduces access denial without changing any user, registry or production pipe ACL.
        using var denied = new NamedPipeServerStream(pipe, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var window = new SecRandomWindow(pipe);
        var render = typeof(SecRandomWindow).GetMethod("Render", flags)!;
        var busy = typeof(SecRandomWindow).GetField("_busy", flags)!;
        busy.SetValue(window, true); // Keep constructor polling out of the explicit command setup.
        var ready = new SecRandomState("fixture.exe", 7, "Ready", DateTimeOffset.Now,
            new(Guid.NewGuid(), "quick-draw", "Succeeded", "隔离历史回执", DateTimeOffset.Now,
                Winner: new("fixture-student", "测试姓名", "")));
        try
        {
            Exercise(window, owner =>
            {
                try
                {
                    render.Invoke(window, [ready]); busy.SetValue(window, false);
                    if (entry != "poll")
                    {
                        Task command = entry == "sidebar" ? window.QuickDrawFromSidebarAsync()
                            : (Task)typeof(SecRandomWindow).GetMethod("RunAsync", flags)!.Invoke(window, ["check", false, null])!;
                        PumpUntil(() => command.IsCompleted, "access-denied command did not settle");
                        command.GetAwaiter().GetResult(); // The baseline leaks UnauthorizedAccessException here.
                        Assert(!(bool)busy.GetValue(window)!, "access denial retained the operation's busy flag");
                    }
                    else
                        PumpUntil(() => ((TextBlock)owner.FindName("Connection")).Text.Contains("未确认"),
                            "access-denied status poll stopped without invalidating the old ready snapshot");
                    Assert(!Button(owner, "DrawButton").IsEnabled && ((TextBlock)owner.FindName("Connection")).Text.Contains("未确认"),
                        "access denial left the old draw-ready state available");
                    Assert(((TextBlock)owner.FindName("OperationText")).Text.Contains("仅供核对") &&
                        ((TextBlock)owner.FindName("WinnerText")).Text.Contains("上次回执"), "access denial discarded history or presented it as a current result");
                    if (entry == "sidebar")
                        Assert(((TextBlock)owner.FindName("Message")).Text.Contains("未收到可靠回执"), "sidebar did not offer reconciliation guidance");
                    denied.Dispose();
                    using var recovered = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    async Task Reply()
                    {
                        await recovered.WaitForConnectionAsync(timeout.Token);
                        var request = await Protocol.ReadAsync<HostRequest>(recovered, timeout.Token);
                        Assert(request.Capability == "secrandom.status" && request.SecRandom is null,
                            "recovery automatically replayed a command instead of reading status");
                        await Protocol.WriteAsync(recovered, new HostResponse(Protocol.Version, request.RequestId,
                            "Succeeded", null, "隔离连接恢复", SecRandom: ready), timeout.Token);
                    }
                    var reply = Reply();
                    PumpUntil(() => reply.IsCompleted && Button(owner, "DrawButton").IsEnabled,
                        "polling did not recover after restoring duplex pipe access");
                    reply.GetAwaiter().GetResult();
                    Assert(((TextBlock)owner.FindName("Connection")).Text.Contains("已就绪"), "fresh status did not restore its ready label");
                    Checks.Add($"SecRandom access/{entry}: access denial is handled, commands are disabled and history is labelled; fresh polling recovers without replaying a draw");
                }
                finally { window.Shutdown(); }
            });
        }
        finally { window.Shutdown(); }
    }
}
