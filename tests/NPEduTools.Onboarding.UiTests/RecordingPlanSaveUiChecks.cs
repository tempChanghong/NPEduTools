using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Core;

internal static partial class Program
{
    private static void RunRecordingPlanSaveChecks()
    {
        RunRecordingCalendarChecks();
        RunRecordingDatedDeleteChecks();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.plan-save." + Guid.NewGuid().ToString("N");
        string preferences = StartupPreferencesStore.PathFor(pipe);
        string planPath = preferences.Replace(".startup.json", ".recording-plans.json", StringComparison.Ordinal);
        string previewPath = preferences.Replace(".startup.json", ".recording-preview.json", StringComparison.Ordinal);
        var clientType = typeof(AutoRecordingWindow).Assembly.GetType("NPEduTools.App.RecordingClient")!;
        var client = Activator.CreateInstance(clientType, [Application.Current.Dispatcher, pipe])!;
        clientType.GetMethod("Detach")!.Invoke(client, null);
        var constructor = typeof(AutoRecordingWindow).GetConstructors(flags).Single();
        AutoRecordingWindow? window = null;
        object Field(string name) => typeof(AutoRecordingWindow).GetField(name, flags)!.GetValue(window)!;
        void Set(string name, object value) => typeof(AutoRecordingWindow).GetField(name, flags)!.SetValue(window, value);
        void Save() => typeof(AutoRecordingWindow).GetMethod("Save", flags)!.Invoke(window, null);
        bool Change(Func<RecordingPlanBook, RecordingPlanBook> change, string message) =>
            (bool)typeof(AutoRecordingWindow).GetMethod("ChangeBook", flags)!.Invoke(window, [change, message])!;
        string Feedback() => ((TextBlock)window!.FindName("ErrorText")).Text;
        try
        {
            window = (AutoRecordingWindow)constructor.Invoke([pipe, client, (Action)(() => { })]);
            Exercise(window, owner =>
            {
                // Real stores and trial engine; only synthetic timetable/clock and recorder status.
                var actual = new AutomaticRecordingState(true, Guid.NewGuid(), "隔离实际状态：等待课程", null, []);
                clientType.GetMethod("Apply", flags)!.Invoke(client,
                    [new HostResponse(Protocol.Version, Guid.NewGuid(), "Succeeded", null, "隔离回执", Automatic: actual)]);
                byte[] originalPlan = File.ReadAllBytes(planPath);
                using (var locked = new FileStream(planPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Assert(!Change(b => b with { ExcludedNames = ["隔离科目"] }, "不应成功"), "plan write failure reported success");
                    Assert(Feedback().StartsWith("未应用修改："), "plan write failure lacks failure feedback");
                    Assert(File.ReadAllBytes(planPath).SequenceEqual(originalPlan), "failed plan save rewrote original file");
                    Assert((bool)Field("_writable"), "plan-only failure prevents retry");
                }
                Assert(Change(b => b with { ExcludedNames = [] }, "重试已保存"), "plan retry failed after releasing lock");
                Assert(Feedback() == "重试已保存", "healthy save lost success feedback");
                Checks.Add("Plan write failure preserves original configuration and allows an explicit retry after the lock is released");

                var school = new DateTimeOffset(2031, 4, 7, 10, 0, 0, TimeSpan.Zero);
                var date = DateOnly.FromDateTime(school.Date);
                var source = new DaySchedule(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "隔离课表", date,
                    "fixture", school, true, true, "隔离学校时间",
                    [new LessonSlot(1, Guid.NewGuid(), "数学", school, school.AddMinutes(40), true)]);
                Set("_source", source); Set("_today", date); Set("_viewDate", date);
                var clock = (SchoolClockTracker)Field("_clock");
                var elapsed = Stopwatch.GetElapsedTime(0);
                var connection = Guid.NewGuid(); var bridge = Guid.NewGuid();
                for (int i = 0; i < 3; i++)
                    clock.Accept(new(connection, bridge, i + 1, 0, school.AddMilliseconds(i * 500), 0, "Advancing", "隔离时间", source),
                        elapsed - TimeSpan.FromMilliseconds(1000 - i * 500), 0);
                Assert(clock.Read(elapsed).CanStart, "synthetic school clock did not become fresh");
                var preview = (RecordingPreview)Field("_preview");
                var plans = CalendarRecordingPlanner.Build((RecordingPlanBook)Field("_book"), date, source);
                preview.SetEnabled(true, school);
                preview.Tick(source, plans, clock.Read(elapsed), elapsed);
                Assert(preview.State.Active is not null, "trial did not start from the synthetic timetable");
                Save();
                byte[] originalPreview = File.ReadAllBytes(previewPath);
                using (var locked = new FileStream(previewPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Assert(Change(b => b with { Recurring = b.Recurring.Select(r => r with { Enabled = false }).ToArray() }, "规则已保存"),
                        "a persisted plan was incorrectly reported as unapplied");
                    Assert(new RecordingPlanBookStore(planPath).Read().Recurring.All(r => !r.Enabled), "new plan was not persisted");
                    Assert(preview.State.Active is null && !preview.Enabled && !(bool)Field("_writable"), "trial did not stop after preview save failed");
                    Assert(File.ReadAllBytes(previewPath).SequenceEqual(originalPreview), "failed preview save rewrote original history");
                    Assert(((TextBlock)owner.FindName("RealStatus")).Text.Contains(actual.Message), "trial failure changed actual recording status");
                    Assert(Button(owner, "ToggleReal").Content.ToString() == "关闭自动录课", "trial failure changed actual recording master toggle");
                    Assert(Feedback().Contains("录课计划已保存") && Feedback().Contains("试运行记录无法保存") && Feedback().Contains("试运行已停止"),
                        "partial save is masked by generic success: " + Feedback());
                    Snapshot(owner, "partial-save.png");
                }
                Checks.Add("Persisted plan plus locked trial history: retain both outcomes, stop trial only, preserve disk history and actual recording state");
                window.Shutdown();
            });

            window = (AutoRecordingWindow)constructor.Invoke([pipe, client, (Action)(() => { })]);
            Exercise(window, owner =>
            {
                Assert(((RecordingPlanBook)Field("_book")).Recurring.All(r => !r.Enabled), "reopening lost committed plan changes");
                Assert((bool)Field("_writable") && !((RecordingPreview)Field("_preview")).Enabled, "reopening automatically resumed trial or remained unwritable");
                Assert(Change(b => b with { ExcludedNames = ["隔离恢复"] }, "计划已保存"), "reopened window cannot save again");
                Assert(Feedback() == "计划已保存", "healthy reopened window reports stale save failure");
                Checks.Add("Reopen after releasing history lock: load the committed plan, keep trial stopped, and save normally");
                window.Shutdown();
            });
        }
        finally
        {
            window?.Shutdown();
            clientType.GetMethod("Detach")!.Invoke(client, null);
            foreach (string path in new[] { planPath, previewPath, planPath + ".tmp", previewPath + ".tmp" })
                if (File.Exists(path)) File.Delete(path);
        }
    }
}
