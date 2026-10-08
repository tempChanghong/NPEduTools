using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;

internal static partial class Program
{
    private static void RunShortcutRecoveryChecks()
    {
        var failures = new List<Exception>();
        foreach (string scenario in new[] { "version", "null", "missing-items", "invalid-target", "duplicate", "too-many", "oversized", "invalid-json", "valid", "absent" })
        {
            try { RunShortcutRecoveryCheck(scenario); }
            catch (Exception error) { failures.Add(new InvalidOperationException(scenario + ": shortcut recovery failed", error)); }
        }
        foreach (string scenario in new[] { "empty-name", "missing-scheme", "wrong-application", "relative-file" })
        {
            try { RunShortcutEditorCheck(scenario); }
            catch (Exception error) { failures.Add(new InvalidOperationException(scenario + ": shortcut editor failed", error)); }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    private static void RunShortcutEditorCheck(string scenario)
    {
        var window = new ShortcutEditorWindow();
        try
        {
            ((ComboBox)window.FindName("KindBox")).SelectedIndex = scenario is "empty-name" or "missing-scheme" ? 2 :
                scenario == "wrong-application" ? 0 : 1;
            ((TextBox)window.FindName("NameBox")).Text = scenario == "empty-name" ? "" : "隔离快捷项目";
            ((TextBox)window.FindName("TargetBox")).Text = scenario switch
            {
                "missing-scheme" => "example.com",
                "wrong-application" => @"C:\Isolated\lesson.cmd",
                "relative-file" => "lesson.pptx",
                _ => "https://example.com/course"
            };
            typeof(ShortcutEditorWindow).GetMethod("SaveClicked", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [window, new RoutedEventArgs()]);
            Assert(window.Result is null && window.DialogResult != true &&
                !string.IsNullOrWhiteSpace(((TextBlock)window.FindName("ErrorText")).Text),
                "invalid editor input throws, accepts a result or fails to explain the error");
            Checks.Add(scenario + ": actual editor save handler rejects invalid input with inline feedback; no window display or launch");
        }
        finally { window.Close(); }
    }

    private static void RunShortcutRecoveryCheck(string scenario)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.shortcut-recovery." + Guid.NewGuid().ToString("N");
        string path = ShortcutCatalog.PathFor(pipe);
        var entry = new ShortcutEntry(Guid.NewGuid(), "隔离教学平台", "url", "https://example.com/course");
        string valid = JsonSerializer.Serialize(new ShortcutDocument(1, [entry]));
        string? content = scenario switch
        {
            "version" => JsonSerializer.Serialize(new ShortcutDocument(999, [])),
            "null" => "null",
            "missing-items" => """{"Version":1}""",
            "invalid-target" => JsonSerializer.Serialize(new ShortcutDocument(1, [entry with { Target = "example.com" }])),
            "duplicate" => JsonSerializer.Serialize(new ShortcutDocument(1, [entry, entry])),
            "too-many" => JsonSerializer.Serialize(new ShortcutDocument(1, Enumerable.Range(0, 25).Select(_ => entry with { Id = Guid.NewGuid() }).ToArray())),
            "oversized" => new string(' ', 128 * 1024 + 1),
            "invalid-json" => "{broken",
            "valid" => valid,
            _ => null
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (content is not null) File.WriteAllText(path, content);
        byte[]? original = content is null ? null : File.ReadAllBytes(path);
        var window = IsolatedMainWindow(pipe);
        try
        {
            // Invoke the real startup path, without tray, Host, polling, or showing a window.
            typeof(MainWindow).GetMethod("InitializeShortcuts", flags)!.Invoke(window, null);
            bool invalid = scenario is not ("valid" or "absent");
            bool Readable() => (bool)typeof(MainWindow).GetField("_shortcutsReadable", flags)!.GetValue(window)!;
            string Feedback() => ((TextBlock)window.FindName("ShortcutMessage")).Text;
            var list = (ListBox)window.FindName("ShortcutList");
            Assert(Readable() == !invalid, "startup accepted invalid shortcuts or rejected a healthy catalog");
            Assert(Button(window, "AddShortcutButton").IsEnabled == !invalid, "invalid shortcut file permits editing or healthy file blocks editing");
            Assert(list.Items.Count == (scenario == "valid" ? 1 : 0), "invalid catalog partially applied shortcuts");
            if (invalid)
            {
                Assert(Feedback().Contains("原文件已保留"), "startup did not explain the unreadable file");
                Assert(((TextBlock)window.FindName("HomeShortcutCount")).Text == "暂不可用", "home shows an invalid catalog as healthy");
                typeof(MainWindow).GetMethod("ReloadShortcuts", flags)!.Invoke(window, null);
                Assert(!Readable() && File.ReadAllBytes(path).SequenceEqual(original!), "retry changed or accepted the damaged file");
                File.WriteAllText(path, valid);
                // The same window must recover after an administrator repairs the isolated file.
                typeof(MainWindow).GetMethod("ReloadShortcuts", flags)!.Invoke(window, null);
                Assert(Readable() && list.Items.Count == 1 && Button(window, "AddShortcutButton").IsEnabled,
                    "same window did not recover after the catalog was repaired");
                Assert(string.IsNullOrEmpty(Feedback()) && File.ReadAllText(path) == valid, "recovery rewrote the file or retained the old failure");
            }
            else
            {
                Assert(string.IsNullOrEmpty(Feedback()), "healthy or missing catalog shows a failure");
                Assert(original is null ? !File.Exists(path) : File.ReadAllBytes(path).SequenceEqual(original), "startup rewrote the healthy catalog");
                if (scenario == "valid")
                {
                    var saved = (bool)typeof(MainWindow).GetMethod("SaveShortcuts", flags)!.Invoke(window,
                        [new ShortcutEntry[] { entry, entry }, null])!;
                    Assert(!saved && Readable() && list.Items.Count == 1 && File.ReadAllBytes(path).SequenceEqual(original!),
                        "rejected edit threw, changed the visible list or replaced the saved file");
                    Assert(Feedback().Contains("未能保存"), "rejected edit did not explain that the list was preserved");
                }
            }
            Checks.Add(scenario + ": real shortcut startup/reload handlers preserve invalid files, disable edits and recover after repair; healthy or absent catalogs remain usable");
        }
        finally
        {
            window.Close();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
