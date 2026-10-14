using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class NoiseSelectionRecoveryTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task Microphone_refresh_recovers_saved_selection_after_startup_read_lock_without_capture_or_fallback(bool present, bool locked)
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = Directory.CreateTempSubdirectory("NPEduTools-mic-read-recovery-").FullName;
        string path = Path.Combine(directory, "noise-microphone.json");
        string original = JsonSerializer.Serialize("saved-mic"); File.WriteAllText(path, original);
        int captures = 0;
        try
        {
            NoiseService service; NoiseState before;
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                service = new(_ => { captures++; throw new InvalidOperationException("No microphone capture"); },
                    () => present ? [new("saved-mic", "Saved synthetic microphone"), new("other-mic", "Other synthetic microphone")]
                        : [new("other-mic", "Other synthetic microphone")], directory: directory) { EquipmentLocked = locked };
                before = service.Snapshot(); Assert.Null(before.SelectedDeviceId);
            }
            await using (service)
            {
                var refreshed = service.Handle(new(1, Guid.NewGuid(), "noise.devices"));
                Assert.Equal("Succeeded", refreshed.Outcome); Assert.Equal("saved-mic", refreshed.Noise!.SelectedDeviceId);
                Assert.Equal(before.Revision + 1, refreshed.Noise.Revision); Assert.Equal("Idle", refreshed.Noise.State);
                Assert.Equal(present, refreshed.NoiseDevices!.Any(d => d.Id == "saved-mic"));
                Assert.Equal("NoiseStateChanged", service.Handle(new(1, Guid.NewGuid(), "noise.command",
                    Noise: new("select", before.InstanceId, before.Revision, "other-mic"))).ErrorCode);
                Assert.Equal(refreshed.Noise.Revision, service.Handle(new(1, Guid.NewGuid(), "noise.devices")).Noise!.Revision);
                Assert.Equal(original, File.ReadAllText(path)); Assert.Equal(0, captures);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Refresh_does_not_overwrite_a_new_selection_made_during_device_enumeration()
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = Directory.CreateTempSubdirectory("NPEduTools-mic-read-race-").FullName;
        string path = Path.Combine(directory, "noise-microphone.json");
        File.WriteAllText(path, JsonSerializer.Serialize("saved-mic"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int captures = 0;
        try
        {
            NoiseService service;
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                service = new(_ => { captures++; throw new InvalidOperationException("No microphone capture"); }, () =>
                {
                    entered.TrySetResult();
                    release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                    return [new("saved-mic", "Saved microphone"), new("other-mic", "New microphone")];
                }, directory: directory);
            await using (service)
            {
                var before = service.Snapshot();
                var refresh = Task.Run(() => service.Handle(new(1, Guid.NewGuid(), "noise.devices")));
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    var selected = service.Handle(new(1, Guid.NewGuid(), "noise.command",
                        Noise: new("select", before.InstanceId, before.Revision, "other-mic")));
                    Assert.Equal("Succeeded", selected.Outcome);
                    release.TrySetResult();
                    var refreshed = await refresh.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal("Succeeded", refreshed.Outcome);
                    Assert.Equal("other-mic", refreshed.Noise!.SelectedDeviceId);
                    Assert.Equal(selected.Noise!.Revision, refreshed.Noise.Revision);
                    Assert.Equal(JsonSerializer.Serialize("other-mic"), File.ReadAllText(path));
                    Assert.Equal(0, captures);
                }
                finally { release.TrySetResult(); await refresh.WaitAsync(TimeSpan.FromSeconds(10)); }
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
