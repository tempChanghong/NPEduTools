using System.Diagnostics;
using NPEduTools.ClassIsland.Bridge.Contracts;

internal sealed class FakeBridge : IRecordingBridgeP0
{
    private readonly Guid _instance = Guid.NewGuid();
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private static readonly Guid Profile = Guid.Parse("0911140f-7f85-44cc-8eeb-cd87314c6c32");
    private static readonly Guid Plan = Guid.Parse("5decb282-6534-4ec5-8de6-4095dd70d404");
    private static readonly Guid Layout = Guid.Parse("994b5642-2fe6-49e0-9fc1-63becc4215b3");
    private static readonly Guid Subject = Guid.Parse("855285d4-0588-4598-a4b5-5d4211291160");
    public Task<string> GetHelloAsync() => Task.FromResult(BridgeProtocol.Encode(new BridgeHello(1,
        "npedutools.recordingbridge.p0", _instance, "test", "2.1.0.1", "Ready",
        ["effective-local-clock", "current-day", "sample-age", "read-only"])));
    public Task<string> GetSnapshotAsync()
    {
        // Deliberately a different calendar from the machine. No Windows wall-clock dependency.
        var now = new DateTime(2031, 4, 7, 9, 59, 0).Add(_elapsed.Elapsed);
        static string F(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture);
        var day = new BridgeDay("2031-04-07", Profile, Plan, Layout, "测试生效课表", "Ready", "fixture-r1",
            [new(1, Subject, "预演数学", F(now.Date.AddHours(10)), F(now.Date.AddHours(10).AddMinutes(40)), true),
             new(2, Subject, "预演数学", F(now.Date.AddHours(11)), F(now.Date.AddHours(11).AddMinutes(40)), false)]);
        return Task.FromResult(BridgeProtocol.Encode(new BridgeSnapshot(1, _instance, "Ready",
            _elapsed.ElapsedMilliseconds / 250 + 1, 0, F(now), 0, "Advancing", true, day, null)));
    }
}
