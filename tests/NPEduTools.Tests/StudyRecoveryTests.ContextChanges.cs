using NPEduTools.Core;

namespace NPEduTools.Tests;

public sealed partial class StudyRecoveryTests
{
    [Theory]
    [InlineData("exam")]
    [InlineData("registration")]
    [InlineData("expected")]
    [InlineData("maintenance")]
    [InlineData("session-ending")]
    [InlineData("incident-missing")]
    [InlineData("scope")]
    [InlineData("selection")]
    public async Task Claimed_recovery_warning_is_hidden_when_its_eligibility_changes(string change)
    {
        await using var f = new Fixture(); await f.Active();
        Assert.Equal("Failed", f.Recovery.Snapshot().State);
        Assert.Equal(30, f.Recovery.Snapshot().WarningRemainingSeconds);
        var incident = f.Files.Read<GuardRecoveryEvent>("recovery.json")!;
        var registration = f.Files.Read<GuardRegistration>("registration.json")!;
        var audit = Assert.Single(Directory.GetFiles(Path.Combine(f.Folder, "audit"), "*.exit"));
        var originalBytes = File.ReadAllBytes(audit);
        f.Advance(10000); var prior = f.State;
        switch (change)
        {
            case "exam": f.State = f.State with { Mode = "Exam", ClockSource = "Windows" }; break;
            case "registration": f.Files.Write("registration.json", registration with { Generation = Guid.NewGuid() }); break;
            case "expected": f.Files.Write("expected-exit.json", new GuardExpectedExit(f.Generation, new(20, 200), f.Time.GetUtcNow().AddMinutes(90))); break;
            case "maintenance": f.Files.Stop(f.Generation, "Maintenance"); break;
            case "session-ending": f.Files.SessionQuery(f.Generation, true); break;
            case "incident-missing": File.Delete(Path.Combine(f.Folder, "guard", "recovery.json")); break;
            case "scope": f.Scope = "other-class"; break;
            case "selection": f.Selected = false; break;
        }
        var invalid = f.Recovery.Snapshot();
        Assert.NotEqual("Failed", invalid.State); Assert.Equal(0, invalid.WarningRemainingSeconds);
        Assert.Equal(0, f.Shots); Assert.Equal(0, f.Resumes); Assert.Equal(originalBytes, File.ReadAllBytes(audit));
        // Restoring the same eligible incident must retain its original deadline and audit.
        f.State = prior; f.Scope = "scope"; f.Selected = true;
        f.Files.Write("registration.json", registration); f.Files.Write("recovery.json", incident);
        File.Delete(Path.Combine(f.Folder, "guard", "expected-exit.json"));
        File.Delete(Path.Combine(f.Folder, "guard", "stop.json"));
        f.Files.SessionQuery(f.Generation, false);
        var restored = f.Recovery.Snapshot();
        Assert.Equal("Failed", restored.State); Assert.Equal(20, restored.WarningRemainingSeconds);
        Assert.Equal(f.Incident, restored.AttemptId); Assert.Equal(originalBytes, File.ReadAllBytes(audit));
        Assert.Single(Directory.GetFiles(Path.Combine(f.Folder, "audit"), "*.exit"));
    }
}
