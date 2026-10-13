using System.Diagnostics;
using System.Reflection;
using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class RecordingSchedulerWaitTests
{
    [Fact]
    public async Task WaitingForOrdinaryCommandGateDoesNotDisableAutomaticRecordingAsSystemPause()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.scheduler-gate." + Guid.NewGuid().ToString("N");
        string directory = Path.Combine(Path.GetTempPath(), pipe);
        int ticks = 0;
        var service = new RecordingService(pipe, directory, () =>
        {
            Interlocked.Increment(ref ticks);
            return SchoolClockFrame.Unavailable("isolated clock unavailable; no recorder can start");
        });
        void Set(string name, object value) => typeof(RecordingService).GetField(name, flags)!.SetValue(service, value);
        var gate = (SemaphoreSlim)typeof(RecordingService).GetField("_gate", flags)!.GetValue(service)!;
        bool held = false;
        try
        {
            await gate.WaitAsync(); held = true;
            int ticksBeforeCommand = Volatile.Read(ref ticks);
            // A command can legitimately own this gate while preparing devices.
            // No worker, device, school configuration or media capture is involved.
            Set("_enabled", true); Set("_enabledAt", 1L);
            Set("_appLease", RecorderDeadline.After(TimeSpan.FromMinutes(1)));
            await Task.Delay(TimeSpan.FromSeconds(6));
            Assert.Equal(ticksBeforeCommand, Volatile.Read(ref ticks));
            gate.Release(); held = false;
            var deadline = Stopwatch.StartNew();
            while (Volatile.Read(ref ticks) == ticksBeforeCommand && deadline.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(10);
            Assert.True(Volatile.Read(ref ticks) > ticksBeforeCommand, "scheduler did not resume after the command gate was released");
            Assert.True(service.Automatic.Enabled, service.Automatic.Message);
            Assert.Equal("ClockPending", service.Automatic.NextState);
        }
        finally
        {
            if (held) gate.Release();
            await service.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
