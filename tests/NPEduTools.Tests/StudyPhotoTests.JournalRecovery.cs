using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class StudyPhotoTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Healthy_queue_reread_recovers_temporary_read_failure_without_resetting_cooldown(bool acknowledged)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var folder = new Folder(); var clock = new Clock();
        var journal = new StudyPhotoJournal(folder.Path, FakeProtection, clock);
        Assert.True(journal.Claim(Utc, 30));
        var packet = Packet(); journal.Save(packet);
        string file = Assert.Single(Directory.GetFiles(folder.Path, "*.photo"));
        byte[] original = File.ReadAllBytes(file);
        using (var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(journal.Next("scope", "camera", "mic", Utc));
            Assert.Equal("PHOTO_QUEUE_UNAVAILABLE", journal.Error);
            Assert.False(journal.Claim(Utc, 30));
        }
        Assert.Equal(original, File.ReadAllBytes(file));
        if (acknowledged) journal.Remove(Guid.Parse(packet.Metadata["eventId"]!.GetValue<string>()));
        var restored = journal.Next("scope", "camera", "mic", Utc);
        Assert.Equal(!acknowledged, restored is not null);
        Assert.Null(journal.Error);
        clock.Ms = 29999; Assert.False(journal.Claim(Utc.AddHours(1), 30));
        clock.Ms = 30000; Assert.True(journal.Claim(Utc.AddSeconds(30), 30));
        if (!acknowledged) Assert.Equal(original, File.ReadAllBytes(file));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Queue_recovery_cannot_erase_an_independent_cooldown_read_failure(bool overlap)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var folder = new Folder(); var clock = new Clock();
        var journal = new StudyPhotoJournal(folder.Path, FakeProtection, clock);
        string cooldown = Path.Combine(folder.Path, "cooldown.json");
        const string broken = "invalid cooldown"; File.WriteAllText(cooldown, broken);
        Assert.False(journal.Claim(Utc, 30));
        journal.Save(Packet()); string file = Assert.Single(Directory.GetFiles(folder.Path, "*.photo"));
        if (overlap)
        {
            using var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Null(journal.Next("scope", "camera", "mic", Utc));
        }
        Assert.NotNull(journal.Next("scope", "camera", "mic", Utc));
        Assert.Equal("PHOTO_JOURNAL_UNAVAILABLE", journal.Error);
        clock.Ms = 60000; Assert.False(journal.Claim(Utc.AddMinutes(1), 30));
        Assert.Equal(broken, File.ReadAllText(cooldown));
    }

    [Fact]
    public async Task Formal_pending_photo_reread_clears_only_the_recovered_queue_fault()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var folder = new Folder(); int captures = 0;
        var journal = new StudyPhotoJournal(Path.Combine(folder.Path, "photos"), FakeProtection);
        journal.Save(Packet()); string file = Assert.Single(Directory.GetFiles(Path.Combine(folder.Path, "photos"), "*.photo"));
        await using var noise = new NoiseService(_ => { captures++; throw new InvalidOperationException("No real microphone"); }, () => []);
        var n = noise.Snapshot();
        Assert.Equal("Succeeded", noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("select", n.InstanceId, n.Revision, "mic"))).Outcome);
        var devices = new StudyDeviceService(() => [new("camera", "Synthetic camera")], Path.Combine(folder.Path, "devices"));
        var d = devices.Snapshot();
        Assert.Equal("Succeeded", devices.Handle(new(1, Guid.NewGuid(), "study.devices.select",
            StudyCamera: new("select", d.InstanceId, d.Revision, "camera"))).Outcome);
        await using var photos = new StudyPhotoService(() => ("scope", false, new(null, "UNAVAILABLE", "", "", "Outside", "", false, null, null, null, null)),
            noise, devices, (_, _) => { captures++; throw new InvalidOperationException("No real camera"); }, journal, background: false);
        photos.BindPhotos("scope");
        using (var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(photos.PendingPhoto()); Assert.Equal("Faulted", photos.Snapshot().State);
        }
        Assert.NotNull(photos.PendingPhoto());
        Assert.Equal("NotRequired", photos.Snapshot().State); Assert.Null(photos.Snapshot().Reason);
        Assert.Equal(0, captures); Assert.Equal("Idle", noise.Snapshot().State);
    }
}
