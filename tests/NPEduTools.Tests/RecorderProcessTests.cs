using NPEduTools.Contracts;
using NPEduTools.Host;
using System.Diagnostics;
using System.Reflection;

namespace NPEduTools.Tests;

[Collection(ProcessIntegrationCollection.Name)]
public sealed class RecorderProcessTests
{
    [Theory]
    [InlineData("recorder-null")]
    [InlineData("recorder-oversized")]
    [InlineData("recorder-malformed")]
    [InlineData("recorder-empty")]
    [InlineData("recorder-unknown")]
    [InlineData("recorder-missing-message")]
    [InlineData("recorder-owned-idle")]
    [InlineData("recorder-saved")]
    public async Task InvalidInitialStateFailsStartupAndStillAllowsCleanup(string mode)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "GuardFixture", "NPEduTools.GuardFixture.exe");
        var recorder = new RecorderProcess(executable, mode);
        try
        {
            var control = new RecorderControl("Manual", Guid.NewGuid(), "", 0,
                RecorderDeadline.After(TimeSpan.FromSeconds(8)));
            await Assert.ThrowsAsync<IOException>(() => recorder.StartAsync(
                new("synthetic", Path.GetTempPath()), control));
            Assert.Equal("Failed", recorder.State.Phase);
            Assert.NotNull(recorder.State.Error);
        }
        finally
        {
            // A malformed receipt must not fault the reader task and escape disposal.
            await recorder.DisposeAsync();
        }
        Assert.False(recorder.Alive);
    }

    [Fact]
    public async Task ReadyWorkerReceivesStartAndStopForTheCurrentSession()
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "GuardFixture", "NPEduTools.GuardFixture.exe");
        await using var recorder = new RecorderProcess(executable, "recorder-ready");
        var control = new RecorderControl("Manual", Guid.NewGuid(), "", 0,
            RecorderDeadline.After(TimeSpan.FromSeconds(8)));
        await recorder.StartAsync(new("synthetic", Path.GetTempPath()), control);
        await WaitForPhaseAsync(recorder, "Recording");
        Assert.Equal("start", recorder.State.Message);
        Assert.True(control.Matches(recorder.State.Control));
        await recorder.CommandAsync("stop", control);
        await WaitForPhaseAsync(recorder, "Saved");
        Assert.Equal("stop", recorder.State.Message);
        await recorder.DisposeAsync();
        Assert.False(recorder.Alive);
    }

    [Fact]
    public async Task FailedWorkerPreservesItsStartupError()
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "GuardFixture", "NPEduTools.GuardFixture.exe");
        await using var recorder = new RecorderProcess(executable, "recorder-failed");
        var control = new RecorderControl("Manual", Guid.NewGuid(), "", 0,
            RecorderDeadline.After(TimeSpan.FromSeconds(8)));
        var error = await Assert.ThrowsAsync<IOException>(() => recorder.StartAsync(
            new("synthetic", Path.GetTempPath()), control));
        Assert.Equal("fixture-startup-failed", error.Message);
        await recorder.DisposeAsync();
        Assert.False(recorder.Alive);
    }

    private static async Task WaitForPhaseAsync(RecorderProcess recorder, string phase)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (recorder.State.Phase != phase) await Task.Delay(20, timeout.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimedOutCleanupCanBeRetriedAfterWorkerFinishes(bool concurrent)
    {
        string directory = Directory.CreateTempSubdirectory("NPEduTools-recorder-cleanup-").FullName;
        string release = Path.Combine(directory, "release-worker");
        string executable = Path.Combine(AppContext.BaseDirectory, "GuardFixture", "NPEduTools.GuardFixture.exe");
        var recorder = new RecorderProcess(executable, "recorder-finalizing");
        Process? process = null;
        Microsoft.Win32.SafeHandles.SafeProcessHandle? handle = null;
        try
        {
            var control = new RecorderControl("Manual", Guid.NewGuid(), "", 0,
                RecorderDeadline.After(TimeSpan.FromSeconds(8)));
            await recorder.StartAsync(new("synthetic", directory), control);
            await WaitForPhaseAsync(recorder, "Recording");
            await recorder.CommandAsync("stop", control);
            await WaitForPhaseAsync(recorder, "Saved");
            // Observe the actual owned native handle, rather than relying on GC or process-count timing.
            process = (Process)typeof(RecorderProcess).GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(recorder)!;
            handle = process.SafeHandle;
            Task cleanup = recorder.DisposeAsync().AsTask();
            Task? concurrentCleanup = concurrent ? recorder.DisposeAsync().AsTask() : null;
            await cleanup;
            Assert.True(recorder.Alive); // Timeout must not kill a worker that is still finalizing.
            Assert.False(handle.IsClosed);
            File.WriteAllText(release, "done");
            if (concurrentCleanup is not null) await concurrentCleanup;
            else await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await recorder.DisposeAsync();
            Assert.False(recorder.Alive);
            Assert.True(handle.IsClosed); // A retry must finish releasing the first worker's resources.
            Assert.Equal("Saved", recorder.State.Phase);
            await recorder.DisposeAsync(); // Completed cleanup remains idempotent.
        }
        finally
        {
            File.WriteAllText(release, "done");
            await recorder.DisposeAsync();
            if (process is not null && handle is { IsClosed: false })
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                process.Dispose();
            }
            Directory.Delete(directory, true);
        }
    }
}
