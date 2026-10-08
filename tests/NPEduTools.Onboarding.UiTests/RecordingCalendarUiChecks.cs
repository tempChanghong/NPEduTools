using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Core;

internal static partial class Program
{
    private static void RunRecordingCalendarChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            var failures = new List<Exception>();
            foreach (string navigation in new[] { "later", "earlier", "revisit", "today" })
                foreach (bool lostReply in new[] { false, true })
                    try { RunRecordingCalendarCheck(navigation, lostReply); }
                    catch (Exception error) { failures.Add(new InvalidOperationException(navigation + "/lost=" + lostReply + ": " + error.Message, error)); }
            if (failures.Count != 0) throw new AggregateException(failures);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void RunRecordingCalendarCheck(string navigation, bool lostReply)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.calendar-navigation." + Guid.NewGuid().ToString("N");
        string preferences = StartupPreferencesStore.PathFor(pipe);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        NamedPipeServerStream Server() => new(pipe, PipeDirection.InOut, 2, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        async Task<HostRequest> Read(NamedPipeServerStream server)
        {
            await server.WaitForConnectionAsync(timeout.Token);
            return await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
        }
        var type = typeof(AutoRecordingWindow);
        var clientType = type.Assembly.GetType("NPEduTools.App.RecordingClient")!;
        var client = Activator.CreateInstance(clientType, [Application.Current.Dispatcher, pipe])!;
        clientType.GetMethod("Detach")!.Invoke(client, null);
        var window = (AutoRecordingWindow)type.GetConstructors(flags).Single().Invoke([pipe, client, (Action)(() => { })]);
        void Set(string name, object value) => type.GetField(name, flags)!.SetValue(window, value);
        object? Field(string name) => type.GetField(name, flags)!.GetValue(window);
        Task Refresh(bool force = false) => (Task)type.GetMethod("ReadCalendarAsync", flags)!.Invoke(window, [force])!;
        TimeSpan Elapsed() => Stopwatch.GetElapsedTime(0);
        DateOnly today = new(2026, 10, 8);
        Guid bridge = Guid.NewGuid(), connection = Guid.NewGuid(), profile = Guid.NewGuid();
        DaySchedule Schedule(DateOnly date, string name) => new(profile, Guid.NewGuid(), Guid.NewGuid(), name,
            date, name, new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), true, true, "隔离时间", []);
        var clock = (SchoolClockTracker)Field("_clock")!;
        // Prevent constructor/Loaded polling; only the bounded forecast queries below reach our pipe.
        Set("_querying", true);
        Set("_today", today); Set("_bridgeInstance", bridge); Set("_source", Schedule(today, "today"));
        try
        {
            Exercise(window, owner =>
            {
                try
                {
                    ((DispatcherTimer)Field("_timer")!).Stop();
                    var elapsed = Elapsed();
                    var schoolNow = new DateTimeOffset(today.ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);
                    for (int index = 0; index < 3; index++)
                        clock.Accept(new(connection, bridge, index + 1, 0, schoolNow.AddSeconds(index * .5), 0,
                            "Advancing", "隔离学校时间"), elapsed - TimeSpan.FromSeconds(1 - index * .5), 0);
                    Assert(clock.Read(Elapsed()).CanStart, "synthetic advancing school clock is not ready");
                    int oldOffset = navigation == "earlier" ? 2 : 1;
                    int newOffset = navigation == "later" ? 2 : navigation == "today" ? 0 : 1;
                    using var oldServer = Server();
                    var oldRead = Read(oldServer);
                    Click(owner, oldOffset == 1 ? "TomorrowButton" : "AfterTomorrowButton");
                    PumpUntil(() => oldRead.IsCompleted, "original forecast query did not arrive");
                    var oldRequest = oldRead.GetAwaiter().GetResult();
                    Assert(oldRequest.Capability == "classisland.day-plan" && oldRequest.SchoolDate == today.AddDays(oldOffset), "unexpected original forecast query");
                    using var newServer = Server();
                    var newRead = Read(newServer);
                    if (navigation == "revisit") Click(owner, "AfterTomorrowButton");
                    Click(owner, newOffset == 0 ? "TodayButton" : newOffset == 1 ? "TomorrowButton" : "AfterTomorrowButton");
                    Assert((DateOnly?)Field("_viewDate") == today.AddDays(newOffset) && Field("_forecast") is null,
                        "date buttons did not change the selected day or clear the previous forecast");
                    if (lostReply) oldServer.Disconnect();
                    else
                    {
                        var oldReply = Protocol.WriteAsync(oldServer, new HostResponse(Protocol.Version, oldRequest.RequestId,
                            "Succeeded", null, "旧日期课表", Forecast: new(bridge, today, oldRequest.SchoolDate!.Value,
                                Schedule(oldRequest.SchoolDate.Value, "obsolete-forecast"))), timeout.Token);
                        PumpUntil(() => oldReply.IsCompleted, "old forecast reply did not finish"); oldReply.GetAwaiter().GetResult();
                    }
                    PumpUntil(() => newRead.IsCompleted || !(bool)Field("_calendarReading")!, "obsolete forecast did not release its pending read");
                    Assert(Field("_forecast") is null, "previous date's forecast populated the new visit");
                    if (navigation == "today")
                    {
                        Assert(!newRead.IsCompleted && !(bool)Field("_calendarReading")!, "returning to today queried a forecast unnecessarily");
                    }
                    else
                    {
                        Assert(newRead.IsCompleted, "new date inherited the obsolete forecast's retry cooldown instead of starting its own read");
                        var freshRequest = newRead.GetAwaiter().GetResult();
                        Assert(freshRequest.Capability == "classisland.day-plan" && freshRequest.SchoolDate == today.AddDays(newOffset), "fresh query did not use the selected date");
                        var forecast = new DayForecast(bridge, today, freshRequest.SchoolDate!.Value,
                            Schedule(freshRequest.SchoolDate.Value, "fresh-forecast"));
                        var freshReply = Protocol.WriteAsync(newServer, new HostResponse(Protocol.Version, freshRequest.RequestId,
                            "Succeeded", null, "新日期课表", Forecast: forecast), timeout.Token);
                        PumpUntil(() => freshReply.IsCompleted, "fresh forecast reply did not finish"); freshReply.GetAwaiter().GetResult();
                        PumpUntil(() => !(bool)Field("_calendarReading")!, "fresh forecast query did not settle");
                        Assert(((DayForecast?)Field("_forecast"))?.Schedule.Name == "fresh-forecast", "fresh forecast did not populate the selected date");
                        Assert(((TextBlock)owner.FindName("DateStatus")).Text.Contains(today.AddDays(newOffset).ToString("yyyy-MM-dd")), "forecast UI shows a different date");
                        Assert((TimeSpan)Field("_calendarRetry")! > Elapsed(), "current visit lost its normal polling cooldown");
                        Assert(Refresh().IsCompleted && !(bool)Field("_calendarReading")!, "ordinary repeat read bypassed its cooldown");
                    }
                    Assert(!((RecordingPreview)Field("_preview")!).Enabled, "forecast navigation started a trial");
                    Checks.Add($"calendar {navigation}/lost={lostReply}: obsolete forecast ignored; latest future date reads immediately, today needs no forecast; normal cooldown retained");
                }
                finally { window.Shutdown(); }
            });
        }
        finally
        {
            window.Shutdown(); clientType.GetMethod("Detach")!.Invoke(client, null);
            foreach (string suffix in new[] { ".recording-plans.json", ".recording-preview.json" })
            {
                string path = preferences.Replace(".startup.json", suffix, StringComparison.Ordinal);
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
