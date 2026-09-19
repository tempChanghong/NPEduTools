#nullable enable
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ClassIsland.Shared.IPC;
using dotnetCampus.Ipc.CompilerServices.GeneratedProxies;
using NPEduTools.ClassIsland.Bridge.Contracts;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Integrations.ClassIsland;

/// <summary>Runs only in the leased, killable bridge worker. One IPC connection per worker lifetime.</summary>
public static class ClassIslandSchoolClock
{
    public static async Task MonitorAsync(string pipe, Func<SchoolClockFrame, Task> publish, CancellationToken token)
    {
        var client = new IpcClient(); using var provider = client.Provider;
        int disconnected = 0;
        try
        {
            provider.StartServer(); client.JsonIpcProvider.StartServer();
            var peer = await provider.GetAndConnectToPeerAsync(pipe).WaitAsync(TimeSpan.FromSeconds(4), token);
            peer.PeerConnectionBroken += (_, _) => Interlocked.Exchange(ref disconnected, 1);
            var service = provider.CreateIpcProxy<IRecordingBridgeP0>(peer);
            var hello = Decode<BridgeHello>(await service.GetHelloAsync().WaitAsync(TimeSpan.FromSeconds(3), token));
            ValidateHello(hello);
            while (!token.IsCancellationRequested)
            {
                long start = Stopwatch.GetTimestamp();
                var snapshot = Decode<BridgeSnapshot>(await service.GetSnapshotAsync().WaitAsync(TimeSpan.FromSeconds(3), token));
                if (Volatile.Read(ref disconnected) != 0) throw new IOException("Disconnected");
                await publish(ConvertSnapshot(hello, snapshot, Stopwatch.GetElapsedTime(start).TotalMilliseconds));
                await Task.Delay(500, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            Console.Error.WriteLine($"School bridge: {error.GetBaseException().GetType().Name}");
            await publish(SchoolClockFrame.Unavailable("学校时间暂不可用，正在重连；请确认 ClassIsland 与桥接插件已启用。"));
        }
    }

    private static T Decode<T>(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > BridgeProtocol.MaxBytes) throw new InvalidDataException("Oversize");
        return JsonSerializer.Deserialize<T>(json, BridgeProtocol.Json) ?? throw new InvalidDataException("Empty");
    }

    public static void ValidateHello(BridgeHello hello)
    {
        if (hello.ProtocolVersion != BridgeProtocol.Version || hello.Contract != "npedutools.recordingbridge.p0" ||
            hello.BridgeInstanceId == Guid.Empty || hello.Lifecycle != "Ready" || hello.ClassIslandVersion != "2.1.0.1" || hello.Capabilities is null ||
            !new[] { "effective-local-clock", "current-day", "sample-age", "read-only" }.All(hello.Capabilities.Contains))
            throw new InvalidDataException("Unsupported bridge handshake");
    }

    public static SchoolClockFrame ConvertSnapshot(BridgeHello hello, BridgeSnapshot s, double roundTripMs)
    {
        if (s.ProtocolVersion != BridgeProtocol.Version || s.BridgeInstanceId != hello.BridgeInstanceId ||
            s.Sequence < 0 || s.ClockEpoch < 0 || !double.IsFinite(s.SampleAgeMs) || s.SampleAgeMs < 0 ||
            !double.IsFinite(roundTripMs) || roundTripMs < 0)
            throw new InvalidDataException("Invalid bridge snapshot");
        if (s.Lifecycle != "Ready" || s.EffectiveLocalDateTime is null)
            return SchoolClockFrame.Unavailable("学校时间尚未就绪。");
        var now = ParseSchoolTime(s.EffectiveLocalDateTime);
        double age = s.SampleAgeMs + roundTripMs;
        string state = age >= SchoolClockFrame.MaxAgeMs ? "Stale" : s.ClockState;
        if (state is not ("Advancing" or "WarmingUp" or "Discontinuous" or "FrozenSuspected" or "Stale" or "Unavailable"))
            throw new InvalidDataException("Unknown clock state");
        DaySchedule? day = null;
        if (s.Day is { } d)
        {
            if (!DateOnly.TryParseExact(d.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                date != DateOnly.FromDateTime(now.Date) || d.Lessons is null or { Length: > 64 } ||
                d.Name is null or { Length: > 256 } || d.Revision is null or { Length: > 128 })
                throw new InvalidDataException("Inconsistent day");
            var slots = d.Lessons.Select(l =>
            {
                if (l is null || l.Subject is null or { Length: > 256 }) throw new InvalidDataException("Invalid lesson");
                var start = ParseSchoolTime(l.Start); var end = ParseSchoolTime(l.End);
                if (DateOnly.FromDateTime(end.Date) != date) throw new InvalidDataException("Invalid end date");
                return new LessonSlot(l.Number, l.SubjectId, l.Subject, start, end, l.Enabled);
            }).ToArray();
            bool ready = d.Status == "Ready" && s.LessonTimerRunning;
            if (ready && (d.ProfileId == Guid.Empty || d.PlanId is null || d.LayoutId is null || d.PlanId == Guid.Empty || d.LayoutId == Guid.Empty))
                throw new InvalidDataException("Missing plan identity");
            day = new(d.ProfileId, d.PlanId ?? Guid.Empty, d.LayoutId ?? Guid.Empty, d.Name, date, d.Revision,
                now, ready, state == "Advancing", d.Status switch
                {
                    "Ready" => "生效课表", "NoPlan" => "本日无课表", "Disabled" => "课表已停用",
                    "TimerStopped" => "课程计时已暂停", "Changing" => "课表更新中", _ => "课表暂不可用"
                }, slots);
            _ = RecordingPlanner.Build(day, new());
        }
        return new(Guid.Empty, s.BridgeInstanceId, s.Sequence, s.ClockEpoch, now, age, state,
            state == "Advancing" ? "正在使用 ClassIsland 学校时间" : "学校时间校验中：" + state, day);
    }

    public static DateTimeOffset ParseSchoolTime(string text)
    {
        if (!DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var value)) throw new InvalidDataException("Invalid school calendar value");
        // No ToUniversalTime/ToLocalTime: ClassIsland has already applied its own correction.
        return new(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), TimeSpan.Zero);
    }
}
