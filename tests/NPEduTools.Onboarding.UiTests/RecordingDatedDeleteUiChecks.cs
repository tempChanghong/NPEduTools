using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Core;

internal static partial class Program
{
    private static void RunRecordingDatedDeleteChecks()
    {
        var failures = new List<Exception>();
        foreach (bool saveInstead in new[] { false, true })
            try { RunRecordingDatedDeleteCheck(saveInstead); }
            catch (Exception error) { failures.Add(new InvalidOperationException("dated delete/retry=" + (saveInstead ? "save" : "delete") + ": " + error.Message, error)); }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    private static void RunRecordingDatedDeleteCheck(bool saveInstead)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.dated-delete." + Guid.NewGuid().ToString("N");
        string preferences = StartupPreferencesStore.PathFor(pipe);
        string planPath = preferences.Replace(".startup.json", ".recording-plans.json", StringComparison.Ordinal);
        string previewPath = preferences.Replace(".startup.json", ".recording-preview.json", StringComparison.Ordinal);
        var type = typeof(AutoRecordingWindow);
        var clientType = type.Assembly.GetType("NPEduTools.App.RecordingClient")!;
        var client = Activator.CreateInstance(clientType, [Application.Current.Dispatcher, pipe])!;
        clientType.GetMethod("Detach")!.Invoke(client, null);
        var window = (AutoRecordingWindow)type.GetConstructors(flags).Single().Invoke([pipe, client, (Action)(() => { })]);
        type.GetField("_querying", flags)!.SetValue(window, true); // No school or recorder query.
        object? Field(string name) => type.GetField(name, flags)!.GetValue(window);
        RecordingPlanBook Book() => (RecordingPlanBook)Field("_book")!;
        try
        {
            Exercise(window, owner =>
            {
                try
                {
                    ((DispatcherTimer)Field("_timer")!).Stop();
                    DateOnly date = new(2026, 10, 9);
                    type.GetField("_today", flags)!.SetValue(window, date);
                    type.GetMethod("SetViewDate", flags)!.Invoke(window, [date, true]);
                    ((Expander)Find(owner, "AutoDatedEditor")).IsExpanded = true;
                    owner.UpdateLayout();
                    ((TextBox)owner.FindName("DatedName")).Text = "隔离单次录课";
                    Click(owner, "AutoSaveDated", true);
                    Assert(Book().Dated.Length == 1, "initial single-date recording was not saved");
                    var original = Book().Dated.Single();
                    var rows = (DataGrid)owner.FindName("Plans");
                    rows.SelectedItem = rows.Items.Cast<object>().Single(row =>
                        (string)row.GetType().GetProperty("Key")!.GetValue(row)! == $"fixed/{original.Id:N}/{date:yyyy-MM-dd}");
                    Assert((Guid?)Field("_editingDated") == original.Id, "selecting a fixed plan did not bind its editor");
                    byte[] before = File.ReadAllBytes(planPath);
                    using (var locked = new FileStream(planPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        Click(owner, "AutoDeleteDated", true);
                        Assert(((TextBlock)owner.FindName("ErrorText")).Text.StartsWith("未应用修改："), "failed delete did not show its failure");
                        Assert(File.ReadAllBytes(planPath).SequenceEqual(before) && Book().Dated.Single() == original,
                            "failed delete changed the saved or in-memory plan");
                    }
                    if (saveInstead)
                    {
                        ((TextBox)owner.FindName("DatedName")).Text = "原时段修改后的名称";
                        Click(owner, "AutoSaveDated", true);
                        Assert(Book().Dated.Length == 1 && Book().Dated[0].Id == original.Id &&
                            Book().Dated[0].Name == "原时段修改后的名称", "saving after failed delete added a duplicate instead of updating the same entry");
                        Assert(new RecordingPlanBookStore(planPath).Read().Dated.SequenceEqual(Book().Dated), "updated entry did not persist");
                        // The successful update must remain editable and deletable by the same ID.
                        Click(owner, "AutoDeleteDated", true);
                    }
                    else Click(owner, "AutoDeleteDated", true);
                    Assert(Book().Dated.Length == 0 && new RecordingPlanBookStore(planPath).Read().Dated.Length == 0,
                        "delete retry after releasing the file lock did not remove the original entry");
                    Assert(Field("_editingDated") is null && ((TextBlock)owner.FindName("ErrorText")).Text == "单日固定时段已删除。",
                        "successful delete retained its edit identity or failure feedback");
                    Assert(!((RecordingPreview)Field("_preview")!).Enabled, "failed deletion or retry started a trial");
                    Checks.Add("dated delete: locked file preserves entry; " + (saveInstead ? "save updates the original ID without duplication, then deletes normally" : "explicit delete retry removes the original entry") + "; trial stays stopped");
                }
                finally { window.Shutdown(); }
            });
        }
        finally
        {
            window.Shutdown(); clientType.GetMethod("Detach")!.Invoke(client, null);
            foreach (string path in new[] { planPath, previewPath, planPath + ".tmp", previewPath + ".tmp" })
                if (File.Exists(path)) File.Delete(path);
        }
    }
}
