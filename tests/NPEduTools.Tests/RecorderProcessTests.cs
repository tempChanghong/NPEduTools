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
}
