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
    private static void RunRecordingDatedEditChecks()
    {
        var failures = new List<Exception>();
        foreach (bool changedRow in new[] { false, true })
            try { RunRecordingDatedEditCheck(changedRow); }
            catch (Exception error) { failures.Add(new InvalidOperationException("dated edit/changed-row=" + changedRow + ": " + error.Message, error)); }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    private static void RunRecordingDatedEditCheck(bool changedRow)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.dated-edit." + Guid.NewGuid().ToString("N");
        string preferences = StartupPreferencesStore.PathFor(pipe);
        string planPath = preferences.Replace(".startup.json", ".recording-plans.json", StringComparison.Ordinal);
        string previewPath = preferences.Replace(".startup.json", ".recording-preview.json", StringComparison.Ordinal);
        DateOnly date = new(2026, 10, 8);
        var first = new DatedRecording(Guid.NewGuid(), "原录课时段", date, new(10, 0), new(10, 40));
        var second = new DatedRecording(Guid.NewGuid(), "另一个时段", date, new(11, 0), new(11, 40));
        new RecordingPlanBookStore(planPath).Save(RecordingPlanBook.Create() with { Dated = [first, second] });
        var type = typeof(AutoRecordingWindow);
        var clientType = type.Assembly.GetType("NPEduTools.App.RecordingClient")!;
        var client = Activator.CreateInstance(clientType, [Application.Current.Dispatcher, pipe])!;
        clientType.GetMethod("Detach")!.Invoke(client, null);
        var window = (AutoRecordingWindow)type.GetConstructors(flags).Single().Invoke([pipe, client, (Action)(() => { })]);
        void Set(string name, object value) => type.GetField(name, flags)!.SetValue(window, value);
        object? Field(string name) => type.GetField(name, flags)!.GetValue(window);
        Set("_querying", true); // Only the explicit synthetic school-clock query below reaches this pipe.
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            Exercise(window, owner =>
            {
                try
                {
                    ((DispatcherTimer)Field("_timer")!).Stop();
                    Guid bridge = Guid.NewGuid();
                    var now = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);
                    var source = new DaySchedule(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "隔离课表", date,
                        "before", now, true, true, "隔离学校时间", []);
                    Set("_today", date); Set("_bridgeInstance", bridge); Set("_source", source);
                    type.GetMethod("SetViewDate", flags)!.Invoke(window, [date, true]);
                    ((Expander)Find(owner, "AutoDatedEditor")).IsExpanded = true; owner.UpdateLayout();
                    var grid = (DataGrid)owner.FindName("Plans");
                    void Select(DatedRecording item) => grid.SelectedItem = grid.Items.Cast<object>().Single(row =>
                        (string)row.GetType().GetProperty("Key")!.GetValue(row)! == $"fixed/{item.Id:N}/{date:yyyy-MM-dd}");
                    var name = (TextBox)owner.FindName("DatedName");
                    var start = (TextBox)owner.FindName("DatedStart");
                    var end = (TextBox)owner.FindName("DatedEnd");
                    Select(second);
                    Assert(name.Text == second.Name && start.Text == "11:00" && end.Text == "11:40", "selecting a different entry did not load its fields");
                    Select(first);
                    Assert(name.Text == first.Name && start.Text == "10:00" && end.Text == "10:40", "returning to the original entry did not load its fields");
                    name.Text = "尚未保存的新名称"; start.Text = "10:15"; end.Text = "10:55";
                    byte[] saved = File.ReadAllBytes(planPath);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    async Task Reply()
                    {
                        await server.WaitForConnectionAsync(timeout.Token);
                        var request = await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
                        Assert(request.Capability == "classisland.school-clock", "unexpected background query");
                        await Protocol.WriteAsync(server, new HostResponse(Protocol.Version, request.RequestId, "Succeeded", null,
                            "隔离课表刷新", SchoolClock: new(Guid.NewGuid(), bridge, 1, 0, now, 0, "Advancing", "隔离时间",
                                source with { Revision = "after", Lessons = changedRow
                                    ? [new(1, Guid.NewGuid(), "数学", now.AddHours(1), now.AddHours(1).AddMinutes(40), true)] : [] })), timeout.Token);
                    }
                    var reply = Reply(); Set("_querying", false);
                    var refresh = (Task)type.GetMethod("ReadAsync", flags)!.Invoke(window, null)!;
                    PumpUntil(() => refresh.IsCompleted && reply.IsCompleted, "synthetic school-clock refresh did not finish");
                    reply.GetAwaiter().GetResult(); refresh.GetAwaiter().GetResult();
                    Assert(((DaySchedule?)Field("_source"))?.Revision == "after", "new timetable did not reach the window");
                    if (changedRow)
                        Assert(((string)grid.SelectedItem.GetType().GetProperty("Status")!.GetValue(grid.SelectedItem)!).Contains("重叠"),
                            "table rows did not display the newly detected overlap");
                    Assert(name.Text == "尚未保存的新名称" && start.Text == "10:15" && end.Text == "10:55",
                        "background timetable refresh overwrote unsaved single-date recording fields");
                    Assert((Guid?)Field("_editingDated") == first.Id && File.ReadAllBytes(planPath).SequenceEqual(saved),
                        "refresh changed the edit identity or persisted the draft without a save");
                    Checks.Add($"dated edit/changed-row={changedRow}: background timetable refresh updates rows while preserving unsaved name/times and original edit ID; draft is not persisted");
                    Click(owner, "AutoSaveDated", true);
                    var book = new RecordingPlanBookStore(planPath).Read();
                    Assert(book.Dated.Length == 2 && book.Dated.Single(d => d.Id == first.Id) == first with
                        { Name = "尚未保存的新名称", Start = new(10, 15), End = new(10, 55) }, "explicit save lost the draft or duplicated the original entry");
                    Select(second);
                    Assert(name.Text == second.Name && start.Text == "11:00" && end.Text == "11:40", "another entry inherited the previous draft");
                    Checks.Add($"dated edit/changed-row={changedRow}: explicit save persists the original entry; selecting another entry loads that entry's fields");
                }
                finally { window.Shutdown(); }
            });
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            window.Shutdown(); clientType.GetMethod("Detach")!.Invoke(client, null);
            foreach (string path in new[] { planPath, previewPath, planPath + ".tmp", previewPath + ".tmp" })
                if (File.Exists(path)) File.Delete(path);
        }
    }
}
