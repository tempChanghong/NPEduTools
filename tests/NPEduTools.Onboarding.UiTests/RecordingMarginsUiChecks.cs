using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Core;

internal static partial class Program
{
    private static void RunRecordingMarginsChecks()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.margins-ui." + Guid.NewGuid().ToString("N");
        string preferences = StartupPreferencesStore.PathFor(pipe);
        string planPath = preferences.Replace(".startup.json", ".recording-plans.json", StringComparison.Ordinal);
        string previewPath = preferences.Replace(".startup.json", ".recording-preview.json", StringComparison.Ordinal);
        var original = RecordingPlanBook.Create();
        var course = original.Recurring[0] with { Name = "原有课程安排", Filter = new(1, 3, LessonNumbers: [2, 4]) };
        var fixedRule = new RecurringRecordingRule(Guid.NewGuid(), "固定时段", true, new(), FixedStart: new(12, 0), FixedEnd: new(12, 20));
        var book = original with { Recurring = [course, fixedRule], Dated = [new(Guid.NewGuid(), "原有单次安排", new(2031, 4, 7), new(15, 0), new(15, 30))] };
        var store = new RecordingPlanBookStore(planPath); store.Save(book);
        var clientType = typeof(AutoRecordingWindow).Assembly.GetType("NPEduTools.App.RecordingClient")!;
        var client = Activator.CreateInstance(clientType, [Application.Current.Dispatcher, pipe])!;
        clientType.GetMethod("Detach")!.Invoke(client, null);
        var constructor = typeof(AutoRecordingWindow).GetConstructors(flags).Single();
        AutoRecordingWindow? window = null;
        try
        {
            window = (AutoRecordingWindow)constructor.Invoke([pipe, client, (Action)(() => { })]);
            Exercise(window, owner =>
            {
                Click(owner, "AutoRecordingMargins", true);
                Assert(((TabControl)owner.FindName("AutoSections")).SelectedIndex == 1, "margin shortcut did not open weekly settings");
                var lead = (ComboBox)owner.FindName("LeadMinutes"); var tail = (ComboBox)owner.FindName("TailMinutes");
                Assert(lead.Items.Cast<int>().SequenceEqual(Enumerable.Range(0, 11)), "lead minutes are not 0 through 10");
                Assert(tail.Items.Cast<int>().SequenceEqual(Enumerable.Range(0, 11)), "tail minutes are not 0 through 10");
                Assert((int)lead.SelectedItem == 1 && (int)tail.SelectedItem == 3, "old custom margins were replaced by defaults");
                ((TextBox)owner.FindName("RuleName")).Text = "尚未保存的新名称";
                lead.SelectedItem = tail.SelectedItem = 10;
                Click(owner, "SaveMargins");
                var expected = book with { Recurring = [course with { Filter = course.Filter with { BeforeMinutes = 10, AfterMinutes = 10 } }, fixedRule] };
                Assert(JsonSerializer.Serialize(store.Read()) == JsonSerializer.Serialize(expected), "saving margins changed other local preferences");
                Assert(((TextBox)owner.FindName("RuleName")).Text == "尚未保存的新名称", "margin-only save discarded another editor draft");
                Assert(((TextBlock)owner.FindName("ErrorText")).Text.Contains("已保存"), "margin save lacks feedback");
                Checks.Add("Prominent margin shortcut: preserve custom values, offer 0-10 minutes and save only selected rule margins without enabling recording");

                var list = (ComboBox)owner.FindName("RuleList");
                list.SelectedIndex = 1;
                Assert(!Button(owner, "SaveMargins").IsEnabled && !lead.IsEnabled && !tail.IsEnabled, "fixed rule still exposes course margin changes");
                list.SelectedIndex = 0;
                Assert((int)lead.SelectedItem == 10 && (int)tail.SelectedItem == 10, "reselection restored stale margins after saving");
                byte[] before = File.ReadAllBytes(planPath);
                using (var locked = new FileStream(planPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    tail.SelectedItem = 9; Click(owner, "SaveMargins");
                    Assert(((TextBlock)owner.FindName("ErrorText")).Text.StartsWith("未应用修改："), "failed margin write reported success");
                    Assert(File.ReadAllBytes(planPath).SequenceEqual(before), "failed margin save damaged the original plan");
                }
                Click(owner, "SaveMargins");
                Assert(store.Read().Recurring[0].Filter.AfterMinutes == 9, "margin save cannot retry after a write failure");
                Checks.Add("Fixed rule margins stay disabled; reselection reads committed values; write failures preserve old data and allow retry");

                var at = new DateTimeOffset(2031, 4, 7, 8, 0, 0, TimeSpan.Zero); var date = DateOnly.FromDateTime(at.Date);
                var source = new NPEduTools.Contracts.DaySchedule(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "相邻课程", date,
                    "fixture", at, true, true, "隔离学校时间", [new(2, Guid.NewGuid(), "数学", at, at.AddMinutes(45), true),
                        new(4, Guid.NewGuid(), "物理", at.AddMinutes(50), at.AddMinutes(95), true)]);
                typeof(AutoRecordingWindow).GetField("_source", flags)!.SetValue(window, source);
                typeof(AutoRecordingWindow).GetField("_today", flags)!.SetValue(window, date);
                typeof(AutoRecordingWindow).GetField("_viewDate", flags)!.SetValue(window, date);
                typeof(AutoRecordingWindow).GetMethod("Render", flags, [typeof(bool)])!.Invoke(window, [true]);
                Assert(((Border)owner.FindName("PlanOverlapBanner")).Visibility == Visibility.Visible, "overlap summary remains hidden");
                Assert(((TextBlock)owner.FindName("PlanOverlapStatus")).Text.Contains("下一节录课优先"), "overlap policy is missing from the page");
                var rows = ((DataGrid)owner.FindName("Plans")).Items.Cast<object>().ToArray();
                Assert(rows.Any(r => r.GetType().GetProperty("Status")!.GetValue(r)!.ToString()!.Contains("重叠已处理")), "overlap resolution is absent from course rows");
                ((TabControl)owner.FindName("AutoSections")).SelectedIndex = 0; owner.UpdateLayout();
                owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Snapshot(owner, "recording-overlap-status.png");
                Checks.Add("Actual automatic page shows an overlap summary, next-lesson priority and per-course resolved conflict details");
                var sameStart = source with { Lessons = [source.Lessons[0], source.Lessons[1] with { Start = at }] };
                typeof(AutoRecordingWindow).GetField("_source", flags)!.SetValue(window, sameStart);
                typeof(AutoRecordingWindow).GetMethod("Render", flags, [typeof(bool)])!.Invoke(window, [true]);
                Assert(((TextBlock)owner.FindName("PlanOverlapStatus")).Text.Contains("待调整冲突"), "unresolved same-start overlap has no warning");
                Assert(((DataGrid)owner.FindName("Plans")).Items.Cast<object>().Count(r =>
                    r.GetType().GetProperty("Status")!.GetValue(r)!.ToString()!.Contains("待调整冲突")) == 2, "unresolved conflict is not explicit on both course rows");
                typeof(AutoRecordingWindow).GetField("_source", flags)!.SetValue(window, source);
                typeof(AutoRecordingWindow).GetMethod("Render", flags, [typeof(bool)])!.Invoke(window, [true]);
                Checks.Add("Ambiguous equal-start tasks remain visibly blocked in the banner and both course rows");

                owner.MinWidth = 600; owner.MinHeight = 400; owner.Width = 1024; owner.Height = 540;
                Click(owner, "AutoRecordingMargins", true); owner.UpdateLayout();
                owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Snapshot(owner, "recording-margins-compact.png");
                var panel = (Border)owner.FindName("RecordingMargins");
                var bounds = panel.TransformToAncestor(owner).TransformBounds(new Rect(panel.RenderSize));
                Assert(bounds.Width > 0 && bounds.Left >= 0 && bounds.Right <= owner.ActualWidth, "compact margin panel is wider than the page");
                Assert(lead.ActualWidth > 0 && tail.ActualWidth > 0 && Button(owner, "SaveMargins").ActualHeight > 0, "compact margin controls vanished");
                Checks.Add("Compact 1024x540 layout keeps the local margin card scrollable and its controls laid out");
                window.Shutdown();
            });
        }
        finally
        {
            window?.Shutdown(); clientType.GetMethod("Detach")!.Invoke(client, null);
            foreach (string path in new[] { planPath, previewPath, planPath + ".tmp", previewPath + ".tmp" })
                if (File.Exists(path)) File.Delete(path);
        }
    }
}
