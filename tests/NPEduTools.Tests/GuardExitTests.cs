using NPEduTools.Core;

namespace NPEduTools.Tests;

public sealed class GuardExitTests
{
    [Fact]
    public async Task Stopping_with_unavailable_current_policy_retains_the_previously_known_window_end()
    {
        string directory = Path.Combine(Path.GetTempPath(), "NPEduTools.GuardExitTests", Guid.NewGuid().ToString("N"));
        var files = new GuardFiles(directory); var r = new GuardRegistration(Guid.NewGuid(), GuardProcess.Current(), null);
        files.Write("registration.json", r);
        bool available = true; var utc = DateTimeOffset.UtcNow; var end = utc.AddHours(1);
        try
        {
            await using var host = new GuardHostSession(files, () => true, (generation, process) => available
                ? new(generation, process, utc, [new(utc, end)], end) : new(generation, process, utc, []));
            Assert.Equal(end, files.Read<GuardWakePlan>("wake.json")!.CurrentEndUtc);
            available = false; Assert.True(host.ExpectedStop());
            Assert.Equal(end, files.Read<GuardExpectedExit>("expected-exit.json")!.SkipUntilUtc);
            Assert.Empty(files.Read<GuardWakePlan>("wake.json")!.Windows);
            Assert.False(files.Stopped(r.Generation));
        }
        finally { Directory.Delete(directory, true); }
    }
}
