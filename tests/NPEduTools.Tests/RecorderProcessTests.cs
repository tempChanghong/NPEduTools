using NPEduTools.Contracts;
using NPEduTools.Host;

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
}
