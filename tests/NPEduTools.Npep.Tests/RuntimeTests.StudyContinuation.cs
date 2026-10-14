using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    [Fact]
    public async Task Active_connection_resume_does_not_unbind_study_but_pause_and_unpair_still_do()
    {
        using var directory = new TestDirectory(); var server = new FakeServer(); var study = new StudyFixture();
        await using var runtime = new NpepRuntime(() => new(directory.Path, server.Api), "study-resume", Sample, study: study);
        await Command(runtime, "inspect"); await Command(runtime, "pair", server); await Command(runtime, "poll"); await Command(runtime, "confirm", server);
        await Until(() => runtime.Snapshot().Connection == "ONLINE");
        int unbound = study.Unbound;
        await Command(runtime, "resume"); Assert.Equal(unbound, study.Unbound);
        await Until(() => runtime.Snapshot().Connection == "ONLINE");
        await Command(runtime, "pause"); Assert.True(study.Unbound > unbound);
        unbound = study.Unbound;
        await Command(runtime, "resume"); Assert.True(study.Unbound > unbound); // Resuming explicit pause never restores an old permit.
        await Until(() => runtime.Snapshot().Connection == "ONLINE");
        unbound = study.Unbound;
        await Command(runtime, "unpair"); Assert.True(study.Unbound > unbound);
    }
}
