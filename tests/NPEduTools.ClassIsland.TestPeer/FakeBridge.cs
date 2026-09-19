using System.Diagnostics;
using NPEduTools.ClassIsland.Bridge.Contracts;

internal sealed class FakeBridge(string? controlFile = null) : IRecordingBridgeP0, IRecordingBridgeCalendar
{
    private readonly Guid _instance = Guid.NewGuid();
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private static readonly Guid Profile = Guid.Parse("0911140f-7f85-44cc-8eeb-cd87314c6c32");
    private static readonly Guid Plan = Guid.Parse("5decb282-6534-4ec5-8de6-4095dd70d404");
    private static readonly Guid Layout = Guid.Parse("994b5642-2fe6-49e0-9fc1-63becc4215b3");
    private static readonly Guid Subject = Guid.Parse("855285d4-0588-4598-a4b5-5d4211291160");
    public Task<string> GetHelloAsync() => Task.FromResult(BridgeProtocol.Encode(new BridgeHello(1,
        "npedutools.recordingbridge.p0", _instance, "test", "2.1.0.1", "Ready",
        ["effective-local-clock", "current-day", "sample-age", "read-only", "calendar-31-days"])));
    public async Task<string> GetDayAsync(string date)
    {
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<BridgeSnapshot>(await GetSnapshotAsync(), BridgeProtocol.Json)!;
        var requested = DateOnly.ParseExact(date, "yyyy-MM-dd"); var today = new DateOnly(2031, 4, 7);
        if (requested.DayNumber - today.DayNumber is < 0 or > 30)
            return BridgeProtocol.Encode(new BridgeCalendarReply(1, _instance, snapshot.EffectiveLocalDateTime, date, "OutOfRange", true, null));
        var day = snapshot.Day! with { Date = date, Revision = "fixture-" + date,
            Lessons = snapshot.Day.Lessons.Select(l => l with { Subject = l.Number == 1 ? "物理（实验）" : "预演数学",
                SubjectId = l.Number == 1 ? Guid.Parse("f7080ae4-d416-4b3f-b5c4-93448f8b533b") : Subject,
                Start = date + l.Start[10..], End = date + l.End[10..], Enabled = true }).ToArray() };
        return BridgeProtocol.Encode(new BridgeCalendarReply(1, _instance, snapshot.EffectiveLocalDateTime, date, "Succeeded", true, day));
    }
    public Task<string> GetSnapshotAsync()
    {
        // Deliberately a different calendar from the machine. No Windows wall-clock dependency.
        var now = new DateTime(2031, 4, 7, 9, 59, 0).Add(_elapsed.Elapsed);
        int length = 0; bool noPlan = false;
        if (controlFile is not null)
        {
            using var control = System.Text.Json.JsonDocument.Parse(File.ReadAllText(controlFile));
            if (control.RootElement.TryGetProperty("offsetSeconds", out var offset)) now = now.AddSeconds(offset.GetDouble());
            if (control.RootElement.TryGetProperty("lessonLengthSeconds", out var duration)) length = duration.GetInt32();
            if (control.RootElement.TryGetProperty("noPlan", out var empty)) noPlan = empty.GetBoolean();
        }
        static string F(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture);
        var day = new BridgeDay("2031-04-07", Profile, Plan, Layout, "测试生效课表", "Ready", "fixture-r1",
            [new(1, Subject, "预演数学", F(now.Date.AddHours(10)), F(now.Date.AddHours(10).AddMinutes(40)), true),
             new(2, Subject, "预演数学", F(now.Date.AddHours(11)), F(now.Date.AddHours(11).AddMinutes(40)), false)]);
        if (length > 0) day = day with { Lessons = [new(1, Subject, "自动录制短课", F(now.Date.AddHours(9).AddMinutes(59).AddSeconds(3)), F(now.Date.AddHours(9).AddMinutes(59).AddSeconds(3 + length)), true)] };
        if (noPlan) day = day with { Status = "NoPlan", Lessons = [], PlanId = null, LayoutId = null };
        return Task.FromResult(BridgeProtocol.Encode(new BridgeSnapshot(1, _instance, "Ready",
            _elapsed.ElapsedMilliseconds / 250 + 1, 0, F(now), 0, "Advancing", true, day, null)));
    }
}
