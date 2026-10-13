using System.IO.Pipes;
using NPEduTools.Contracts;

internal static partial class Program
{
    // NoiseWindow now pairs the signal snapshot with the separately owned manual session.
    // The single-client transport fixtures must answer both reads before injecting a fault.
    private static async Task ReplyManualFixtureAsync(NamedPipeServerStream server, NoiseState noise, CancellationToken token)
    {
        server.Disconnect();
        await server.WaitForConnectionAsync(token);
        var request = await Protocol.ReadAsync<HostRequest>(server, token);
        Assert(request.Capability == "manual.study.status", "signal fixture did not query manual ownership");
        var manual = ManualFixture(noise);
        await Protocol.WriteAsync(server, new HostResponse(1, request.RequestId, "Succeeded", null, "隔离手动会话", ManualStudy: manual), token);
    }
    private static ManualStudyState ManualFixture(NoiseState noise) => new(noise.InstanceId, noise.Revision, noise.SessionId,
        noise.State is "Active" or "Starting" ? "Active" : "Stopped", null, noise.StartedAt, null, false, 600);
}
