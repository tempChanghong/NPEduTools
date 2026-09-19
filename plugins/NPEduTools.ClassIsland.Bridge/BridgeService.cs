using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;
using dotnetCampus.Ipc.CompilerServices.GeneratedProxies;
using NPEduTools.ClassIsland.Bridge.Contracts;

namespace NPEduTools.ClassIsland.Bridge;

public sealed class BridgeService(IExactTimeService clock, ILessonsService lessons,
    IProfileService profiles, IIpcService ipc) : IRecordingBridgeP0, IRecordingBridgeCalendar, IDisposable
{
    private sealed record Published(BridgeSnapshot Value, long Timestamp);
    private readonly Guid _instance = Guid.NewGuid();
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly ClockObservation _clock = new();
    private readonly DispatcherTimer _fallback = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Published? _published;
    private string _lifecycle = "Starting";
    private long _sequence;
    private long _lastSample;
    private bool _started, _stopped;
    private sealed record DayRequest(string Date, long QueuedAt, TaskCompletionSource<BridgeCalendarReply> Completion);
    private readonly Queue<DayRequest> _dates = new();
    private readonly object _dateLock = new();

    public void Start()
    {
        if (_started || _stopped) return;
        ipc.IpcProvider.CreateIpcJoint<IRecordingBridgeP0>(this);
        ipc.IpcProvider.CreateIpcJoint<IRecordingBridgeCalendar>(this);
        _started = true;
        lessons.PostMainTimerTicked += Tick;
        _fallback.Tick += Fallback;
        _fallback.Start();
        Volatile.Write(ref _lifecycle, "Ready");
        // First snapshot comes after the next completed lesson tick.
    }
    private void Fallback(object? sender, EventArgs args)
    { DrainDateRequest(); if (!lessons.IsTimerRunning) Sample(false); }
    private void Tick(object? sender, EventArgs args) => Sample(true);
    private void Sample(bool fromLessonTick)
    {
        if (_stopped || _elapsed.ElapsedMilliseconds - _lastSample < 250) return;
        _lastSample = _elapsed.ElapsedMilliseconds;
        try
        {
            var before = clock.GetCurrentLocalDateTime();
            var plan = lessons.CurrentClassPlan;
            var profile = profiles.Profile;
            BridgeDay day;
            string date = before.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!fromLessonTick || !lessons.IsClassPlanEnabled || plan is null)
                day = new(date, profile.Id, null, null, "", !fromLessonTick ? "TimerStopped" :
                    lessons.IsClassPlanEnabled ? "NoPlan" : "Disabled", "", []);
            else
            {
                var selected = lessons.GetClassPlanByDate(before, out var id);
                if (!ReferenceEquals(selected, plan) || id is null || id == Guid.Empty)
                    throw new InvalidDataException("Changing");
                if (!profile.TimeLayouts.TryGetValue(plan.TimeLayoutId, out var layout)) throw new InvalidDataException("InvalidLayout");
                var slots = layout.Layouts.Where(t => t.TimeType == 0).ToArray();
                if (slots.Length > 64 || slots.Length != plan.Classes.Count) throw new InvalidDataException("InvalidLessonCount");
                var rows = new List<BridgeLesson>();
                for (int i = 0; i < slots.Length; i++)
                {
                    var slot = slots[i]; var info = plan.Classes[i];
                    if (slot.StartTime < TimeSpan.Zero || slot.EndTime >= TimeSpan.FromDays(1) || slot.EndTime <= slot.StartTime)
                        throw new InvalidDataException("InvalidLessonTime");
                    var subjectId = info.SubjectId != Guid.Empty ? info.SubjectId : slot.DefaultClassId;
                    bool defined = profile.Subjects.TryGetValue(subjectId, out var subject);
                    rows.Add(new(i + 1, subjectId, Limit(defined ? subject!.Name : "未定义科目", 60),
                        Format(before.Date + slot.StartTime), Format(before.Date + slot.EndTime), info.IsEnabled && defined && subjectId != Guid.Empty));
                }
                string revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(BridgeProtocol.Encode(
                    new { date, profile.Id, planId = id, plan.TimeLayoutId, plan.Name, rows }))));
                day = new(date, profile.Id, id, plan.TimeLayoutId, Limit(plan.Name, 80), "Ready", revision, rows.ToArray());
            }
            var after = clock.GetCurrentLocalDateTime();
            if (before.Date != after.Date || (after - before).Duration() > TimeSpan.FromSeconds(1) ||
                !ReferenceEquals(profile, profiles.Profile) || !ReferenceEquals(plan, lessons.CurrentClassPlan))
                throw new InvalidDataException("Changing");
            _clock.Observe(after, _elapsed.Elapsed.TotalMilliseconds);
            var snapshot = new BridgeSnapshot(BridgeProtocol.Version, _instance, "Ready", ++_sequence, _clock.Epoch,
                Format(after), 0, _clock.State, lessons.IsTimerRunning, day, null);
            Volatile.Write(ref _published, new(snapshot, Stopwatch.GetTimestamp()));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var failed = new BridgeSnapshot(BridgeProtocol.Version, _instance, "Ready", ++_sequence, _clock.Epoch,
                null, 0, "Unavailable", lessons.IsTimerRunning, null,
                error is InvalidDataException ? error.Message : error.GetType().Name);
            Volatile.Write(ref _published, new(failed, Stopwatch.GetTimestamp()));
        }
    }
    private static string Limit(string value, int max) => value[..Math.Min(value.Length, max)];
    private static string Format(DateTime time) => time.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
    public Task<string> GetHelloAsync() => Task.FromResult(BridgeProtocol.Encode(new BridgeHello(BridgeProtocol.Version,
        "npedutools.recordingbridge.p0", _instance, "0.2.0.0", typeof(AppBase).Assembly.GetName().Version?.ToString() ?? "unknown",
        Volatile.Read(ref _lifecycle), ["effective-local-clock", "current-day", "sample-age", "read-only", "calendar-31-days"])));
    public async Task<string> GetDayAsync(string date)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return BridgeProtocol.Encode(CalendarFailure("", "InvalidDate"));
        var completion = new TaskCompletionSource<BridgeCalendarReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_dateLock)
        {
            if (_stopped) return BridgeProtocol.Encode(CalendarFailure(date, "Stopping"));
            if (_dates.Count >= 4) return BridgeProtocol.Encode(CalendarFailure(date, "Busy"));
            _dates.Enqueue(new(date, Stopwatch.GetTimestamp(), completion));
        }
        try { return BridgeProtocol.Encode(await completion.Task.WaitAsync(TimeSpan.FromMilliseconds(1500)).ConfigureAwait(false)); }
        catch (TimeoutException) { return BridgeProtocol.Encode(CalendarFailure(date, "TimedOut")); }
    }
    private BridgeCalendarReply CalendarFailure(string date, string status) => new(BridgeProtocol.Version, _instance, null, date, status, true, null);
    private void DrainDateRequest()
    {
        DayRequest request;
        lock (_dateLock) { if (_stopped || _dates.Count == 0) return; request = _dates.Dequeue(); }
        if (Stopwatch.GetElapsedTime(request.QueuedAt).TotalMilliseconds >= 1500)
        { request.Completion.TrySetResult(CalendarFailure(request.Date, "TimedOut")); return; }
        try
        {
            var now = clock.GetCurrentLocalDateTime();
            var date = DateOnly.ParseExact(request.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            int distance = date.DayNumber - DateOnly.FromDateTime(now).DayNumber;
            if (distance is < 0 or > 30) { request.Completion.TrySetResult(CalendarFailure(request.Date, "OutOfRange")); return; }
            var profile = profiles.Profile; var current = lessons.CurrentClassPlan;
            var plan = lessons.GetClassPlanByDate(date.ToDateTime(TimeOnly.MinValue), out var id);
            var day = CopyCalendarDay(date, profile, plan, id);
            var after = clock.GetCurrentLocalDateTime();
            if (after.Date != now.Date || (after - now).Duration() > TimeSpan.FromSeconds(1) ||
                !ReferenceEquals(profile, profiles.Profile) || !ReferenceEquals(current, lessons.CurrentClassPlan))
                throw new InvalidDataException("Changing");
            request.Completion.TrySetResult(new(BridgeProtocol.Version, _instance, Format(after), request.Date, "Succeeded", true, day));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Console.Error.WriteLine("Calendar query failed: " + error.GetType().Name);
            request.Completion.TrySetResult(CalendarFailure(request.Date, error is InvalidDataException ? error.Message : "Unavailable"));
        }
    }
    private static BridgeDay CopyCalendarDay(DateOnly date, Profile profile, ClassPlan? plan, Guid? id)
    {
        string day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (plan is null) return new(day, profile.Id, null, null, "", "NoPlan", "", []);
        if (id is null || id == Guid.Empty || !profile.TimeLayouts.TryGetValue(plan.TimeLayoutId, out var layout)) throw new InvalidDataException("InvalidLayout");
        var slots = layout.Layouts.Where(t => t.TimeType == 0).ToArray();
        if (slots.Length > 64 || slots.Length != plan.Classes.Count) throw new InvalidDataException("InvalidLessonCount");
        var rows = new List<BridgeLesson>();
        for (int i = 0; i < slots.Length; i++)
        {
            var slot = slots[i]; var info = plan.Classes[i];
            if (slot.StartTime < TimeSpan.Zero || slot.EndTime >= TimeSpan.FromDays(1) || slot.EndTime <= slot.StartTime) throw new InvalidDataException("InvalidLessonTime");
            Guid subjectId = info.SubjectId != Guid.Empty ? info.SubjectId : slot.DefaultClassId;
            bool defined = profile.Subjects.TryGetValue(subjectId, out var subject);
            rows.Add(new(i + 1, subjectId, Limit(defined ? subject!.Name : "未定义科目", 60),
                Format(date.ToDateTime(TimeOnly.MinValue) + slot.StartTime), Format(date.ToDateTime(TimeOnly.MinValue) + slot.EndTime), info.IsEnabled && defined && subjectId != Guid.Empty));
        }
        string revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(BridgeProtocol.Encode(new { day, profileId = profile.Id, planId = id, plan.TimeLayoutId, plan.Name, rows }))));
        return new(day, profile.Id, id, plan.TimeLayoutId, Limit(plan.Name, 80), plan.IsEnabled ? "Ready" : "Disabled", revision, rows.ToArray());
    }
    public Task<string> GetSnapshotAsync()
    {
        var data = Volatile.Read(ref _published);
        if (data is null) return Task.FromResult(BridgeProtocol.Encode(new BridgeSnapshot(BridgeProtocol.Version, _instance,
            Volatile.Read(ref _lifecycle), 0, 0, null, 0, "WarmingUp", false, null, null)));
        double age = Stopwatch.GetElapsedTime(data.Timestamp).TotalMilliseconds;
        return Task.FromResult(BridgeProtocol.Encode(data.Value with { SampleAgeMs = age,
            Lifecycle = Volatile.Read(ref _lifecycle), ClockState = age > 3000 ? "Stale" : data.Value.ClockState }));
    }
    public void Dispose()
    {
        if (_stopped) return;
        lock (_dateLock)
        {
            _stopped = true;
            while (_dates.TryDequeue(out var request)) request.Completion.TrySetResult(CalendarFailure(request.Date, "Stopping"));
        }
        Volatile.Write(ref _lifecycle, "Stopping");
        lessons.PostMainTimerTicked -= Tick; _fallback.Tick -= Fallback; _fallback.Stop();
        // Provider belongs to ClassIsland. Do not dispose it or await transport during AppStopping.
        Console.Error.WriteLine("NPEduTools bridge stopped; subscriptions released.");
    }
}
