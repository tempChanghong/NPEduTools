using System.Text;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed partial class StudyExitTests
{
    private static byte[] TestProtection(byte[] bytes) => bytes.Select(b => (byte)(b ^ 101)).ToArray();

    [Theory]
    [InlineData("metadata", false)]
    [InlineData("metadata", true)]
    [InlineData("scope", false)]
    [InlineData("scope", true)]
    public async Task IncompleteExitPacketIsReportedUnavailableAndOriginalIsPreserved(string field, bool missing)
    {
        await using var f = new Fixture(); var id = Guid.NewGuid();
        f.Guard.Begin(f.Begin(id)); f.Time.Ms = 30000; f.Guard.Tick();
        string file = Assert.Single(Directory.GetFiles(Path.Combine(f.Folder, "exit"), "*.exit"));
        var envelope = JsonNode.Parse(TestProtection(File.ReadAllBytes(file)))!.AsObject();
        if (missing) envelope.Remove(field); else envelope[field] = null;
        byte[] original = TestProtection(Encoding.UTF8.GetBytes(envelope.ToJsonString())); File.WriteAllBytes(file, original);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            StudyExitPacket? packet = null;
            var error = Record.Exception(() => packet = f.Guard.PendingExit());
            Assert.True(error is null && packet is null && f.Guard.Snapshot().AuditError == "EXIT_JOURNAL_UNAVAILABLE" && File.Exists(file),
                $"{field} missing={missing}: exception={error?.GetType().Name ?? "none"}, auditError={f.Guard.Snapshot().AuditError ?? "none"}, originalExists={File.Exists(file)}");
            Assert.Equal(original, File.ReadAllBytes(file));
        }
    }

    [Fact]
    public async Task ExitFilenameMismatchCannotBeAcknowledgedAsStoredWhileTheSameRecordRemainsPending()
    {
        await using var f = new Fixture(); var id = Guid.NewGuid();
        f.Guard.Begin(f.Begin(id)); f.Time.Ms = 30000; f.Guard.Tick();
        string file = Assert.Single(Directory.GetFiles(Path.Combine(f.Folder, "exit"), "*.exit"));
        string renamed = Path.Combine(Path.GetDirectoryName(file)!, Guid.NewGuid().ToString("N") + ".exit");
        File.Move(file, renamed); byte[] original = File.ReadAllBytes(renamed);
        var packet = f.Guard.PendingExit();
        // Simulate an acceptance only if the local read incorrectly exposed the mismatched record.
        if (packet is not null) f.Guard.ExitStored(Guid.Parse(packet.Metadata["eventId"]!.GetValue<string>()));
        Assert.True(packet is null && f.Guard.Snapshot().AuditError == "EXIT_JOURNAL_UNAVAILABLE" && f.Guard.Snapshot().AuditState != "Stored",
            $"pending={packet is not null}, audit={f.Guard.Snapshot().AuditState}, physical record remains={File.Exists(renamed)}");
        Assert.Equal(original, File.ReadAllBytes(renamed));
    }

    [Fact]
    public async Task ValidExitRecordKeepsCaseInsensitiveFilenameIdentityAndAcknowledgementRemovesIt()
    {
        await using var f = new Fixture(); var id = Guid.NewGuid();
        f.Guard.Begin(f.Begin(id)); f.Time.Ms = 30000; f.Guard.Tick();
        string file = Assert.Single(Directory.GetFiles(Path.Combine(f.Folder, "exit"), "*.exit"));
        string upper = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file).ToUpperInvariant() + ".exit");
        File.Move(file, file + ".rename"); File.Move(file + ".rename", upper);
        Assert.Equal(id.ToString(), f.Guard.PendingExit()!.Metadata["eventId"]!.GetValue<string>());
        f.Guard.ExitStored(id);
        Assert.Null(f.Guard.PendingExit()); Assert.Empty(Directory.GetFiles(Path.Combine(f.Folder, "exit"), "*.exit"));
        Assert.Equal("Stored", f.Guard.Snapshot().AuditState);
    }
}
