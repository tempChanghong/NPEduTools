using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using NPEduTools.App;

internal static partial class Program
{
    private static void RunShortcutRepairWorkspaceChecks()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.shortcut-inline." + Guid.NewGuid().ToString("N");
        string catalogPath = ShortcutCatalog.PathFor(pipe);
        var catalog = new ShortcutCatalog(catalogPath);
        var first = new ShortcutEntry(Guid.NewGuid(), "课堂课件", "file", Path.Combine(_output, pipe + ".missing-a.pptx"));
        var second = new ShortcutEntry(Guid.NewGuid(), first.Name, "file", Path.Combine(_output, pipe + ".missing-b.pptx"));
        catalog.Save([first, second]);
        var main = IsolatedMainWindow(pipe);
        var rail = new QuickAccessWindow(pipe, () => { }, () => { }, () => { }, main.RestoreWindow, _ => { }, () => { }, () => { });
        typeof(MainWindow).GetField("_quick", flags)!.SetValue(main, rail);
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(main.Dispatcher));
        try
        {
            typeof(MainWindow).GetMethod("InitializeShortcuts", flags)!.Invoke(main, null);
            main.Show();
            Click(main, "ShortcutNav");
            PumpUntil(() => main.IsActive, "shortcut workspace could not get foreground");
            nint hwnd = new WindowInteropHelper(main).Handle;
            var homeItems = (ItemsControl)main.FindName("HomeShortcutItems");
            var railItems = (ItemsControl)rail.FindName("ShortcutItems");
            var list = (ListBox)main.FindName("ShortcutList");
            var pane = (FrameworkElement)main.FindName("ShortcutListPane");
            var editor = (ShortcutEditorView)main.FindName("ShortcutEditor");
            var name = (TextBox)editor.FindName("NameBox");
            var target = (TextBox)editor.FindName("TargetBox");
            var error = (TextBlock)editor.FindName("ErrorText");
            var save = (Button)editor.FindName("SaveButton");
            var current = new[] { first, second };

            for (int i = 0; i < current.Length; i++)
            {
                var original = current[i];
                bool wide = i == 0;
                int width = wide ? 1040 : 840, height = wide ? 760 : 600;
                Resize(width, height);
                list.SelectedItem = current[1 - i];
                main.UpdateLayout();
                var failedRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(original);
                var open = (Button)Find(failedRow, original.OpenId);
                Assert(open.IsVisible && open.IsEnabled && Equals(open.DataContext, original), "the fixture did not find the exact visible shortcut row");
                open.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                var repair = Button(main, "RepairShortcutButton");
                PumpUntil(() => repair.IsVisible && repair.IsEnabled, "opening a missing fixture did not offer repair");
                Click(main, "RepairShortcutButton");
                PumpUntil(() => editor.IsVisible && name.IsKeyboardFocused, "repair did not open the embedded editor");
                Assert(Window.GetWindow(editor) == main && (PresentationSource.FromVisual(editor) as HwndSource)?.Handle == hwnd && pane.IsVisible == wide,
                    "repair did not share the main HWND or use the expected responsive layout");
                Assert(target.Text == original.Target && (list.SelectedItem as ShortcutEntry)?.Id == original.Id,
                    "repair used a previously selected or same-named shortcut instead of the failed one");
                var repaired = original with { Name = "已修正课件 " + (i + 1), Target = Path.Combine(_output, $"课堂资料-{i + 1}.txt") };
                File.WriteAllText(repaired.Target, "isolated shortcut fixture");
                name.Text = repaired.Name;
                target.Text = repaired.Target;
                if (wide)
                {
                    list.SelectedItem = current[1 - i];
                    Assert((list.SelectedItem as ShortcutEntry)?.Id == original.Id && target.Text == repaired.Target,
                        "selecting another row retargeted the active draft");
                }
                byte[] before = File.ReadAllBytes(catalogPath);
                using (File.Open(catalogPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Click(main, "ShortcutSave", true);
                    Assert(editor.IsVisible && error.Text.Contains("未能保存") && name.Text == repaired.Name && target.Text == repaired.Target,
                        "a real atomic write failure dropped the draft or hid the inline error");
                    Assert(File.ReadAllBytes(catalogPath).SequenceEqual(before), "a rejected replacement changed the original catalog bytes");
                    VerifyPublished(current);
                    main.UpdateLayout();
                    Reachable(main, save);
                    Reachable(main, error);
                    CheckCaptionHitTargets(main, hwnd);
                    Snapshot(main, $"shortcut-edit-save-failed-{width}-{height}.png");
                }

                Resize(wide ? 840 : 1040, wide ? 600 : 760);
                Assert(name.Text == repaired.Name && target.Text == repaired.Target && pane.IsVisible != wide,
                    "resizing replaced the locked-file draft or used the wrong layout");
                Click(main, "HomeTab", true);
                Click(main, "ShortcutNav");
                PumpUntil(() => editor.IsVisible && name.IsKeyboardFocused, "returning to shortcuts lost the edit in progress");
                Assert(name.Text == repaired.Name && target.Text == repaired.Target, "leaving the page replaced the draft");
                using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                {
                    var cancel = Task.Run(() => RespondToDrawAcknowledgement(hwnd, accept: false, deadline.Token));
                    Click(main, "ShortcutEditorBack", true);
                    Assert(cancel.GetAwaiter().GetResult() && editor.IsVisible && target.Text == repaired.Target,
                        "declining to discard closed the editor or cleared the draft");
                }
                Resize(width, height);
                Click(main, "ShortcutSave", true);
                current[i] = repaired;
                Assert(!editor.IsVisible && catalog.Read().SequenceEqual(current), "retry did not save the original entry once in place");
                VerifyPublished(current);
                Assert(list.SelectedItem is ShortcutEntry selected && selected.Id == original.Id &&
                    list.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem { IsKeyboardFocusWithin: true },
                    "successful retry did not restore focus to the original shortcut");
                Snapshot(main, $"shortcut-edit-saved-{width}-{height}.png");
            }

            Click(main, "AddShortcut", true);
            Assert(editor.IsVisible && Window.GetWindow(editor) == main, "adding a shortcut did not use the same editor");
            var kind = (ComboBox)editor.FindName("KindBox");
            kind.SelectedIndex = 2;
            name.Text = "新增教学平台";
            target.Text = "https://example.com/course";
            Click(main, "ShortcutSave", true);
            var added = catalog.Read();
            Assert(added.Length == 3 && added.Take(2).SequenceEqual(current) && added[2].Name == "新增教学平台", "add did not append a saved entry");
            VerifyPublished(added);
            CheckCaptionStateChanges(main, hwnd);
            Click(main, "CloseWindow", true);
            PumpUntil(() => !main.IsVisible, "the original close command did not act on the main window");
            Checks.Add("Wide and narrow same-HWND shortcut repair uses the failed item's ID; a real file lock preserves draft/list, retry updates disk, home and rail in original order");
            Checks.Add("Shortcut draft survives resize and leaving the page; cancel defaults to keeping the draft, add uses the same workspace, title-bar actions belong to the main HWND");

            void VerifyPublished(ShortcutEntry[] expected)
            {
                Assert(list.Items.Cast<ShortcutEntry>().SequenceEqual(expected) && homeItems.Items.Cast<ShortcutEntry>().SequenceEqual(expected) &&
                    railItems.Items.Cast<ShortcutEntry>().SequenceEqual(expected), "saved or unsaved changes were published inconsistently");
            }

            void Resize(double width, double height)
            {
                main.MinWidth = Math.Min(main.MinWidth, width);
                main.MinHeight = Math.Min(main.MinHeight, height);
                main.Width = width;
                main.Height = height;
                main.UpdateLayout();
            }
        }
        finally
        {
            if (main.IsVisible) main.Close();
            SynchronizationContext.SetSynchronizationContext(previousContext);
            File.Delete(catalogPath);
        }
    }

    private static void RunInlineShortcutBoundsCheck()
    {
        string pipe = "NPEduTools.Test.shortcut-bounds." + Guid.NewGuid().ToString("N");
        var main = IsolatedMainWindow(pipe);
        try
        {
            typeof(MainWindow).GetMethod("InitializeShortcuts", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);
            main.Show();
            Click(main, "ShortcutNav");
            main.Width = 840;
            main.Height = 600;
            main.UpdateLayout();
            Click(main, "AddShortcut", true);
            main.UpdateLayout();
            var editor = (ShortcutEditorView)main.FindName("ShortcutEditor");
            var scroll = (ScrollViewer)editor.FindName("EditorFormScroll");
            var save = (Button)editor.FindName("SaveButton");
            PumpUntil(() => save.IsVisible && save.ActualHeight > 0, "the inline editor footer did not reach the main window");
            Rect before = save.TransformToAncestor(main).TransformBounds(new Rect(save.RenderSize));
            editor.ShowError(string.Join("\n", Enumerable.Repeat("隔离测试：长错误消息", 80)));
            PumpUntil(() => scroll.ScrollableHeight > 0, "long validation text does not produce a reachable scroll viewport");
            scroll.ScrollToBottom();
            main.UpdateLayout();
            Rect after = save.TransformToAncestor(main).TransformBounds(new Rect(save.RenderSize));
            Assert(before == after && Window.GetWindow(editor) == main, "long validation text shifted the editor footer or created a new window");
            Reachable(main, save);
            Snapshot(main, "shortcut-long-error-scroll.png");
            Checks.Add("Inline shortcut editor scrolls long validation text and keeps the save footer fixed in the compact main window");
        }
        finally { main.Close(); }
    }
}
