using System.Text;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class StudyPhotoTests
{
    [Fact]
    public void PhotoFilenameMismatchCannotReturnTheSamePacketAfterAcknowledgement()
    {
        using var folder = new Folder(); var journal = new StudyPhotoJournal(folder.Path, FakeProtection);
        var saved = Packet(); journal.Save(saved);
        string file = Assert.Single(Directory.GetFiles(folder.Path, "*.photo"));
        string renamed = Path.Combine(folder.Path, Guid.NewGuid().ToString("N") + ".photo");
        File.Move(file, renamed); byte[] original = File.ReadAllBytes(renamed);
        var packet = journal.Next("scope", "camera", "mic", Utc);
        if (packet is not null) journal.Remove(Guid.Parse(packet.Metadata["eventId"]!.GetValue<string>()));
        var again = journal.Next("scope", "camera", "mic", Utc);
        Assert.True(packet is null && again is null && journal.Error == "PHOTO_QUEUE_UNAVAILABLE",
            $"first={packet is not null}, after acceptance={again is not null}, physical record remains={File.Exists(renamed)}");
        Assert.Equal(original, File.ReadAllBytes(renamed));
    }

    [Fact]
    public void ValidPhotoKeepsCaseInsensitiveFilenameIdentityAndAcknowledgementRemovesIt()
    {
        using var folder = new Folder(); var journal = new StudyPhotoJournal(folder.Path, FakeProtection);
        var saved = Packet(); journal.Save(saved);
        string file = Assert.Single(Directory.GetFiles(folder.Path, "*.photo"));
        string upper = Path.Combine(folder.Path, Path.GetFileNameWithoutExtension(file).ToUpperInvariant() + ".photo");
        File.Move(file, file + ".rename"); File.Move(file + ".rename", upper);
        Assert.Equal(saved.Metadata.ToJsonString(), journal.Next("scope", "camera", "mic", Utc)!.Metadata.ToJsonString());
        journal.Remove(Guid.Parse(saved.Metadata["eventId"]!.GetValue<string>()));
        Assert.Null(journal.Next("scope", "camera", "mic", Utc)); Assert.Equal(0, journal.Pending); Assert.Null(journal.Error);
    }

    [Fact]
    public async Task TheFormalPendingPhotoBoundaryReportsMalformedDataWithoutOpeningCapture()
    {
        using var folder = new Folder();
        var journal = new StudyPhotoJournal(Path.Combine(folder.Path, "photos"), FakeProtection);
        journal.Save(Packet());
        string file = Assert.Single(Directory.GetFiles(Path.Combine(folder.Path, "photos"), "*.photo"));
        var envelope = JsonNode.Parse(FakeProtection(File.ReadAllBytes(file), false))!.AsObject();
        envelope["metadata"] = null;
        byte[] original = FakeProtection(Encoding.UTF8.GetBytes(envelope.ToJsonString()), true); File.WriteAllBytes(file, original);
        await using var noise = new NoiseService(_ => throw new InvalidOperationException("Capture must not open"), () => []);
        var n = noise.Snapshot();
        Assert.Equal("Succeeded", noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("select", n.InstanceId, n.Revision, "mic"))).Outcome);
        var cameras = new StudyDeviceService(() => [new("camera", "Synthetic")], Path.Combine(folder.Path, "devices"));
        var d = cameras.Snapshot();
        Assert.Equal("Succeeded", cameras.Handle(new(1, Guid.NewGuid(), "study.devices.select",
            StudyCamera: new("select", d.InstanceId, d.Revision, "camera"))).Outcome);
        await using var photos = new StudyPhotoService(() => ("scope", false, new(null, "UNAVAILABLE", "", "", "Outside", "", false, null, null, null, null)),
            noise, cameras, (_, _) => throw new InvalidOperationException("Camera must not open"), journal, background: false);
        photos.BindPhotos("scope");
        Assert.Null(Record.Exception(() => Assert.Null(photos.PendingPhoto())));
        Assert.Equal("Faulted", photos.Snapshot().State);
        Assert.Equal("PHOTO_QUEUE_UNAVAILABLE", photos.Snapshot().Reason);
        Assert.Equal("Idle", noise.Snapshot().State); Assert.Equal(original, File.ReadAllBytes(file));
    }

    [Theory]
    [InlineData("metadata", false)]
    [InlineData("metadata", true)]
    [InlineData("bytes", false)]
    [InlineData("bytes", true)]
    [InlineData("scope", false)]
    [InlineData("scope", true)]
    [InlineData("camera", false)]
    [InlineData("camera", true)]
    [InlineData("microphone", false)]
    [InlineData("microphone", true)]
    public void IncompletePhotoPacketCannotEscapeQueueReadOrBeDeletedAsAnotherDevice(string field, bool missing)
    {
        using var folder = new Folder();
        new StudyPhotoJournal(folder.Path, FakeProtection).Save(Packet());
        string file = Assert.Single(Directory.GetFiles(folder.Path, "*.photo"));
        var envelope = JsonNode.Parse(FakeProtection(File.ReadAllBytes(file), false))!.AsObject();
        if (missing) envelope.Remove(field); else envelope[field] = null;
        byte[] original = FakeProtection(Encoding.UTF8.GetBytes(envelope.ToJsonString()), true);
        File.WriteAllBytes(file, original);
        var journal = new StudyPhotoJournal(folder.Path, FakeProtection);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            StudyPhotoPacket? pending = null;
            var error = Record.Exception(() => pending = journal.Next("scope", "camera", "mic", Utc));
            Assert.True(error is null && pending is null && journal.Error == "PHOTO_QUEUE_UNAVAILABLE" && File.Exists(file),
                $"{field} missing={missing}: exception={error?.GetType().Name ?? "none"}, queueError={journal.Error ?? "none"}, originalExists={File.Exists(file)}");
            Assert.Equal(1, journal.Pending); Assert.Equal(original, File.ReadAllBytes(file));
        }
    }

    [Fact]
    public void AValidPhotoStillUploadsAndKnownDifferentDeviceDataStillExpires()
    {
        using var folder = new Folder(); var packet = Packet();
        new StudyPhotoJournal(folder.Path, FakeProtection).Save(packet);
        var restored = new StudyPhotoJournal(folder.Path, FakeProtection);
        Assert.Equal(packet.Metadata.ToJsonString(), restored.Next("scope", "camera", "mic", Utc)!.Metadata.ToJsonString());
        Assert.Null(restored.Error); Assert.Equal(1, restored.Pending);
        Assert.Null(restored.Next("scope", "other-camera", "mic", Utc));
        Assert.Null(restored.Error); Assert.Equal(0, restored.Pending);
    }
}
