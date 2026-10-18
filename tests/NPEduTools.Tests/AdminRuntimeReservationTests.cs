using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using NPEduTools.ClassIsland.Admin;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Tests;

[CollectionDefinition("Desktop runtime reservation", DisableParallelization = true)]
public sealed class DesktopRuntimeReservationCollection { }

[Collection("Desktop runtime reservation")]
[SupportedOSPlatform("windows")]
public sealed class AdminRuntimeReservationTests
{
    [Fact]
    public async Task InvalidFramedPreflightReturnsFailureWithoutDispatchOrReplay()
    {
        // The real helper connects but receives no operation. The preflight reads only
        // an in-memory invalid frame; never request UAC, change tasks or launch ClassIsland.
        string helper = Path.Combine(AppContext.BaseDirectory, "NPEduTools.ClassIsland.Admin.exe");
        string executable = Path.Combine(Path.GetTempPath(), "NPEduTools.Nonexistent." + Guid.NewGuid(), "ClassIsland.exe");
        int checks = 0;
        var reply = await AdminClient.RunAsync(helper, "status", executable, beforeDispatch: async () =>
        {
            checks++;
            using var malformed = new MemoryStream(new byte[4]);
            await Protocol.ReadAsync<AdminResult>(malformed, CancellationToken.None);
        });
        Assert.Equal(1, checks);
        Assert.Equal("Failed", reply.Outcome);
        Assert.StartsWith("InvalidDataException:", reply.ErrorCode);
        Assert.Contains("尚未发送操作", reply.Message);
        Assert.Null(reply.Status);
    }

    [Fact]
    public async Task ProtocolFailureDuringDispatchReturnsUnknownWithoutReplay()
    {
        // The frame-size check runs before any bytes are written. This exercises the
        // post-preflight error boundary without sending a real operation to Windows.
        string helper = Path.Combine(AppContext.BaseDirectory, "NPEduTools.ClassIsland.Admin.exe");
        int checks = 0;
        var reply = await AdminClient.RunAsync(helper, "status", new string('x', Protocol.MaxFrameBytes), beforeDispatch: () =>
        {
            checks++;
            return Task.CompletedTask;
        });
        Assert.Equal(1, checks);
        Assert.Equal("Unknown", reply.Outcome);
        Assert.StartsWith("InvalidDataException:", reply.ErrorCode);
        Assert.Contains("不会自动重复执行", reply.Message);
        Assert.Null(reply.Status);
    }

    [Theory]
    [InlineData("disable", false)]
    [InlineData("enable", false)]
    [InlineData("disable", true)]
    [InlineData("enable", true)]
    [InlineData("launch-mode", false)]
    [InlineData("launch-mode", true)]
    public async Task ActualWorkerAcceptsDelegatedStartupButBlocksUnrelatedStartupUnderHostLease(string requested, bool delegated)
    {
        // Real helper, pipe and Windows file lock. A nonexistent EXE stops validation before
        // any task write, even when the test runner is elevated. Never launch UAC or ClassIsland.
        using var lease = RuntimeOperationFile.TryAcquire(true);
        Assert.NotNull(lease);
        using var blocked = RuntimeOperationFile.TryAcquire(false);
        Assert.Null(blocked);
        string pipeName = "NPEduTools.Admin." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "NPEduTools.ClassIsland.Admin.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        using var worker = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            using var identity = WindowsIdentity.GetCurrent();
            using var self = Process.GetCurrentProcess();
            string executable = Path.Combine(Path.GetTempPath(), "NPEduTools.Nonexistent." + Guid.NewGuid(), "ClassIsland.exe");
            string action = !delegated ? requested : requested == "launch-mode" ? "runtime-launch-mode" : AdminOperationPolicy.StartupAction(requested == "enable");
            await Protocol.WriteAsync(pipe, new AdminRequest(action, executable, identity.User!.Value,
                self.SessionId, "deliberately-invalid-fingerprint"), timeout.Token);
            var reply = await Protocol.ReadAsync<AdminResult>(pipe, timeout.Token);
            await worker.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, worker.ExitCode);
            if (delegated)
            {
                Assert.Equal("Failed", reply.Outcome);
                Assert.Contains("文件不存在", reply.Message); // Passed the lease guard; still validates the executable.
                Assert.NotEqual("RuntimeOperationBusy", reply.ErrorCode);
            }
            else Assert.Equal("RuntimeOperationBusy", reply.ErrorCode);
            using var stillBlocked = RuntimeOperationFile.TryAcquire(false);
            Assert.Null(stillBlocked); // Child did not release or replace its parent's reservation.
        }
        finally
        {
            if (!worker.HasExited) { worker.Kill(); await worker.WaitForExitAsync(); }
        }
    }

    [Theory]
    [InlineData("create")]
    [InlineData("delete")]
    [InlineData("launch")]
    [InlineData("runtime-startup-create")]
    [InlineData("runtime-startup-delete")]
    public void StartupDelegationDoesNotBroadenToOtherCommands(string action)
    {
        Assert.True(AdminOperationPolicy.RequiresReservation(action));
        Assert.Equal(action, AdminOperationPolicy.ScheduledAction(action));
    }
}
