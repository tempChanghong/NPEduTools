using NPEduTools.Contracts;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Tests;

public sealed class SchoolUpdateMaintenanceTests
{
    private static readonly DateTimeOffset End = new(2026, 11, 20, 17, 0, 0, TimeSpan.Zero);
    private static readonly SchoolClockFrame AfterSchool = new(Guid.NewGuid(), Guid.NewGuid(), 2, 1, End, 2, "Advancing", "",
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "教室课表", DateOnly.FromDateTime(End.Date), "r1", End, true, true, "",
            [new(1, Guid.NewGuid(), "语文", End.AddHours(-8), End.AddHours(-7), true),
             new(2, Guid.NewGuid(), "物理", End.AddHours(-1), End, true)]));
    private static readonly StudyHostState NoStudy = new(null,"APPLIED","","","Outside","OUTSIDE_WINDOW",true,End,null,null,null);
    private static readonly ManualStudyState NoManual = new(Guid.NewGuid(), 1, null, "Idle", null, null, null, false, null);
    [Theory]
    [InlineData("end", null)]
    [InlineData("class", "CLASS_IN_SESSION")]
    [InlineData("break", "CLASS_IN_SESSION")]
    [InlineData("stale", "STATE_UNKNOWN")]
    [InlineData("no-calendar", "STATE_UNKNOWN")]
    [InlineData("old-day", "STATE_UNKNOWN")]
    [InlineData("unverified", "STATE_UNKNOWN")]
    [InlineData("unknown-study", "STATE_UNKNOWN")]
    [InlineData("school-study", "STUDY_ACTIVE")]
    [InlineData("manual-study", "STUDY_ACTIVE")]
    [InlineData("recording", "RECORDING_ACTIVE")]
    [InlineData("saving", "RECORDING_ACTIVE")]
    [InlineData("microphone", "STUDY_ACTIVE")]
    [InlineData("exam", "STATE_UNKNOWN")]
    public void ClassroomEvidenceMustAgreeBeforeMaintenance(string test, string? reason)
    {
        var clock = test switch
        {
            "class" => AfterSchool with { SchoolNow = End.AddMinutes(-20) },
            "break" => AfterSchool with { SchoolNow = End.AddHours(-3) },
            "stale" => AfterSchool with { AgeMs = SchoolClockFrame.MaxAgeMs },
            "no-calendar" => AfterSchool with { Schedule = null },
            "old-day" => AfterSchool with { SchoolNow = End.AddDays(1) },
            "unverified" => AfterSchool with { Schedule = AfterSchool.Schedule! with { ClockVerified = false } },
            _ => AfterSchool
        };
        var study = test switch { "school-study" => NoStudy with { Phase = "Preparing" },
            "unknown-study" => NoStudy with { PolicyState = "UNAVAILABLE" }, _ => NoStudy };
        var manual = test == "manual-study" ? NoManual with { State = "Waiting" } : NoManual;
        var recording = new RecordingState(test switch { "recording" => "Recording", "saving" => "Saving", _ => "Idle" }, "");
        Assert.Equal(reason, SchoolUpdateMaintenance.Deferral(clock, study, manual, recording, test == "microphone", test == "exam"));
    }
    private sealed class Worker(bool accepted = true) : IPreparedSchoolUpdate
    {
        internal int Applies;
        public Task ApplyAsync(CancellationToken token)
        { Applies++; return accepted ? Task.CompletedTask : Task.FromException(new SchoolUpdateException("UNCONFIRMED")); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [Fact]
    public async Task MaintenanceWaitsForAllMutationsAndDoesNotChangeTheSchoolMode()
    {
        var gate = new RuntimeOperationGate();
        using (var task = gate.TryEnterMutation()) Assert.Null(gate.TryReserveMaintenance());
        using (var maintenance = gate.TryReserveMaintenance())
        {
            Assert.NotNull(maintenance);
            Assert.False(gate.Switching);
            Assert.Null(gate.TryEnterMutation());
            Assert.Null(gate.TryReserveSwitch());
            await Assert.ThrowsAsync<RemoteExamException>(() => gate.ReservePrioritySwitchAsync(default));
        }
        using var next = gate.TryEnterMutation();
        Assert.NotNull(next);
    }
    [Theory]
    [InlineData(true, true, true, 1, true)]
    [InlineData(false, true, true, 0, false)]
    [InlineData(true, false, true, 0, false)]
    [InlineData(true, true, false, 1, false)]
    public async Task ExitRequiresAnIdleReservationAnOnlineGrantAndDurableWorkerAcceptance(
        bool idleAfterReservation, bool grant, bool accepted, int applies, bool stopped)
    {
        bool reserved = false, released = false, closing = false, guardPrepared = false;
        var worker = new Worker(accepted);
        var maintenance = new SchoolUpdateMaintenance(() => !reserved || idleAfterReservation ? null : "STUDY_ACTIVE",
            _ => { reserved = true; return Task.FromResult<IDisposable?>(new SchoolUpdateMaintenance.Reservation(() => released = true)); },
            () => false, () => guardPrepared = true, () => guardPrepared = false, () => closing = true);
        if (!accepted)
            await Assert.ThrowsAsync<SchoolUpdateException>(() => maintenance.HandoffAsync(worker, () => Task.FromResult(grant), default));
        else Assert.Equal(stopped, await maintenance.HandoffAsync(worker, () => Task.FromResult(grant), default));
        Assert.Equal(applies, worker.Applies);
        Assert.Equal(stopped, closing);
        Assert.Equal(!stopped, released);
        Assert.Equal(stopped, guardPrepared);
    }
}
