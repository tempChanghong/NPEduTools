using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Core;

internal static partial class Program
{
    private static void RunRecordingRuleKindChecks()
    {
        var failures = new List<Exception>();
        foreach (string draft in new[] { "2,?", "65", "999999999999999999999" })
            try { RunRecordingRuleKindCheck(draft); }
            catch (Exception error) { failures.Add(new InvalidOperationException("fixed rule/lesson draft=" + draft + ": " + error.Message, error)); }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    private static void RunRecordingRuleKindCheck(string draft)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.rule-kind." + Guid.NewGuid().ToString("N");
        string preferences = StartupPreferencesStore.PathFor(pipe);
        string planPath = preferences.Replace(".startup.json", ".recording-plans.json", StringComparison.Ordinal);
        string previewPath = preferences.Replace(".startup.json", ".recording-preview.json", StringComparison.Ordinal);
        var original = RecordingPlanBook.Create();
        var course = original.Recurring[0] with { Filter = new(7, 9, LessonNumbers: [2, 4]), ProfileId = Guid.NewGuid() };
        var book = original with { Recurring = [course] };
        var store = new RecordingPlanBookStore(planPath); store.Save(book);
        var type = typeof(AutoRecordingWindow);
        var clientType = type.Assembly.GetType("NPEduTools.App.RecordingClient")!;
        var client = Activator.CreateInstance(clientType, [Application.Current.Dispatcher, pipe])!;
        clientType.GetMethod("Detach")!.Invoke(client, null);
        AutoRecordingWindow? window = null;
        try
        {
            window = (AutoRecordingWindow)type.GetConstructors(flags).Single().Invoke([pipe, client, (Action)(() => { })]);
            // No Loaded event or page timers: use a detached client and isolated stores.
            var kind = (ComboBox)window.FindName("RuleKind");
            var numbers = (TextBox)window.FindName("Numbers");
            var lead = (ComboBox)window.FindName("LeadMinutes");
            var tail = (ComboBox)window.FindName("TailMinutes");
            var all = (CheckBox)window.FindName("AllSubjects");
            var exclusions = (CheckBox)window.FindName("UseExclusions");
            var weekdays = (WrapPanel)window.FindName("Weekdays");
            var date = new DateOnly(2031, 4, 7);
            int dayMask = 1 << (int)date.DayOfWeek;
            ((TextBox)window.FindName("RuleName")).Text = "午间固定时段";
            numbers.Text = draft; lead.SelectedItem = 10; tail.SelectedItem = 0; all.IsChecked = false; exclusions.IsChecked = false;
            foreach (CheckBox day in weekdays.Children) day.IsChecked = int.Parse((string)day.Tag) == (int)date.DayOfWeek;
            kind.SelectedIndex = 1;
            ((TextBox)window.FindName("RuleStart")).Text = "12:10";
            ((TextBox)window.FindName("RuleEnd")).Text = "12:50";
            ((DatePicker)window.FindName("RuleFrom")).SelectedDate = date.ToDateTime(TimeOnly.MinValue);
            ((DatePicker)window.FindName("RuleUntil")).SelectedDate = date.AddDays(7).ToDateTime(TimeOnly.MinValue);
            Assert(!numbers.IsEnabled && !lead.IsEnabled && !tail.IsEnabled && !all.IsEnabled && !exclusions.IsEnabled,
                "fixed-time editor still allows course-only input");
            Click(window, "ApplyRules");
            var fixedRule = store.Read().Recurring.Single();
            Assert(fixedRule.IsFixed && fixedRule.Name == "午间固定时段" && fixedRule.Id == course.Id,
                "disabled lesson draft prevents saving the fixed-time rule: " + ((TextBlock)window.FindName("ErrorText")).Text);
            Assert(JsonSerializer.Serialize(fixedRule.Filter) == JsonSerializer.Serialize(course.Filter with { Weekdays = dayMask }) &&
                fixedRule.UseDefaultExclusions == course.UseDefaultExclusions && fixedRule.ProfileId is null &&
                fixedRule.FixedStart == new TimeOnly(12, 10) && fixedRule.FixedEnd == new TimeOnly(12, 50) &&
                fixedRule.From == date && fixedRule.Until == date.AddDays(7),
                "fixed save lost its active options or overwrote dormant course filters");
            var plans = CalendarRecordingPlanner.Build(store.Read(), date, null);
            Assert(plans is [{ Fixed: true, Selected: true }] && plans[0].Start.TimeOfDay == new TimeSpan(12, 10, 0) &&
                plans[0].End.TimeOfDay == new TimeSpan(12, 50, 0) &&
                CalendarRecordingPlanner.Build(store.Read(), date.AddDays(1), null).Length == 0,
                "saved fixed rule does not follow its time and weekday without a timetable");
            Checks.Add($"fixed rule/draft={draft}: disabled course fields cannot block saving; preserved dormant filters, weekday/date boundaries and executable fixed window");

            kind.SelectedIndex = 0;
            Assert(numbers.IsEnabled && numbers.Text == "2,4" && (int)lead.SelectedItem == 7 && (int)tail.SelectedItem == 9 && all.IsChecked == true && exclusions.IsChecked == true,
                "returning to course mode did not restore the previously saved filters");
            byte[] saved = File.ReadAllBytes(planPath);
            numbers.Text = "65"; Click(window, "ApplyRules");
            Assert(File.ReadAllBytes(planPath).SequenceEqual(saved) &&
                ((TextBlock)window.FindName("ErrorText")).Text.StartsWith("未应用修改："),
                "course mode no longer validates lesson numbers or rejected changes damaged the fixed rule");
            numbers.Text = "3,5"; lead.SelectedItem = 10; tail.SelectedItem = 0;
            Click(window, "ApplyRules");
            var restored = store.Read().Recurring.Single();
            Assert(!restored.IsFixed && restored.Filter.LessonNumbers!.SequenceEqual(new[] { 3, 5 }) &&
                restored.Filter.BeforeMinutes == 10 && restored.Filter.AfterMinutes == 0 && restored.Filter.Weekdays == dayMask,
                "valid course edits cannot save after a fixed-time conversion");
            Checks.Add($"course roundtrip/draft={draft}: original course fields return; invalid numbers preserve disk data; explicit valid edits save normally");
        }
        finally
        {
            window?.Shutdown(); clientType.GetMethod("Detach")!.Invoke(client, null);
            foreach (string path in new[] { planPath, previewPath, planPath + ".tmp", previewPath + ".tmp" })
                if (File.Exists(path)) File.Delete(path);
        }
    }
}
