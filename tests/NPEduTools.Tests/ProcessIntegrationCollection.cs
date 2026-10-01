namespace NPEduTools.Tests;

// Each fixture starts real Host/worker processes with bounded IPC and lease deadlines.
// Avoid overlapping independent process trees on small CI runners; concurrency within
// each test (subscribers, resource gates, restart races) remains part of the test.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessIntegrationCollection
{
    public const string Name = "Process integration";
}
