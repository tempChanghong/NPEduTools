using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed class StatusTests
{
    [Fact]
    public async Task MissingHostIsUnknownAndDoesNotStartOne()
    {
        var result = await NpepStatusReader.ReadAsync("NPEduTools.Test.Npep.Missing." + Guid.NewGuid(), "test");
        Assert.Equal("UNKNOWN", result.Status.Text("mode"));
        Assert.Equal("UNKNOWN", result.Status.Text("recording"));
        Assert.InRange(result.AgeMs, 0, 5000);
    }
    [Theory]
    [InlineData("Recording", "RECORDING")]
    [InlineData("Paused", "PAUSED")]
    [InlineData("Saving", "FINALIZING")]
    [InlineData("Starting", "UNKNOWN")]
    [InlineData("Pausing", "UNKNOWN")]
    [InlineData("Failed", "UNKNOWN")]
    public void OnlyMinimalAllowlistedFieldsLeaveHost(string phase, string expected)
    {
        var response = new HostResponse(1, Guid.NewGuid(), "Succeeded", null, "private exception",
            Recording: new(phase, "private message", OutputFile: "private-path"),
            Automatic: new(true, Guid.NewGuid(), "private subject", null, [], SuspendedByMode: true),
            ExamAware: new("private-path", 1, "Connected", "private message", "1.5.2", false, true, 12345),
            ClassroomMode: new(3, "Exam", "Incomplete"));
        var status = NpepStatusReader.Map(response, "test");
        Assert.Equal(expected, status.Text("recording"));
        Assert.Equal("ENABLED", status.Text("automaticRecording")); // User preference, not actual capture activity.
        Assert.Equal("EXAM", status.Text("mode"));
        Assert.Equal("INCOMPLETE", status.Text("modePhase"));
        Assert.DoesNotContain("private", status.ToJsonString());
        Assert.DoesNotContain("12345", status.ToJsonString());
        Assert.Null(status["examAware"]!["bridgeVersion"]); // App version is not plugin version.
    }
    [Fact]
    public void RejectedHostSnapshotCannotAppearReady()
    {
        var response = new HostResponse(1, Guid.NewGuid(), "Rejected", null, "", ClassroomMode: new(1, "Exam"));
        Assert.Equal("UNKNOWN", NpepStatusReader.Map(response, "test").Text("mode"));
    }
}
