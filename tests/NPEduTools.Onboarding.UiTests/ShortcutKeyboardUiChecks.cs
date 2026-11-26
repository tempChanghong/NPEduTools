using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using NPEduTools.App;

internal static partial class Program
{
    private static void RunShortcutKeyboardChecks()
    {
        string pipe = "NPEduTools.Test.shortcut-keyboard." + Guid.NewGuid().ToString("N");
        string path = ShortcutCatalog.PathFor(pipe);
        var catalog = new ShortcutCatalog(path);
        var first = new ShortcutEntry(Guid.NewGuid(), "教学平台", "url", "https://example.com/lesson");
        var second = new ShortcutEntry(Guid.NewGuid(), "当天课件", "file", @"C:\教学资料\隔离用课件.pptx");
        catalog.Save([first, second]);
        var main = IsolatedMainWindow(pipe);
        try
        {
            typeof(MainWindow).GetMethod("InitializeShortcuts", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);
            main.Show();
            Click(main, "ShortcutNav");
            PumpUntil(() => main.IsActive, "shortcut keyboard owner could not get foreground");
            var list = (ListBox)main.FindName("ShortcutList");
            var editor = (ShortcutEditorView)main.FindName("ShortcutEditor");
            nint hwnd = new WindowInteropHelper(main).Handle;
            list.SelectedItem = first;
            main.UpdateLayout();
            var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(first);
            Assert(row.Focus(), "a shortcut row could not enter the keyboard tab order");
            Assert(row.MoveFocus(new TraversalRequest(FocusNavigationDirection.Down)) && list.IsKeyboardFocusWithin,
                "the keyboard could not move between shortcut rows");
            list.SelectedItem = first;

            void Edit()
            {
                Click(main, "EditShortcutButton");
                PumpUntil(() => editor.IsVisible && editor.IsKeyboardFocusWithin, "the inline editor did not receive focus");
                Assert(Window.GetWindow(editor) == main && (PresentationSource.FromVisual(editor) as HwndSource)?.Handle == hwnd,
                    "shortcut editing opened another native window");
            }

            void Focused(Guid id)
            {
                main.UpdateLayout();
                Assert(list.SelectedItem is ShortcutEntry selected && selected.Id == id &&
                    list.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem { IsKeyboardFocusWithin: true },
                    "selection or keyboard focus did not return to the expected shortcut");
            }

            Edit();
            Snapshot(main, "teacher-shortcut-editor.png");
            Click(main, "ShortcutCancel", true);
            Focused(first.Id);
            Assert(catalog.Read()[0] == first, "closing the editor changed the saved shortcut");
            Edit();
            ((TextBox)editor.FindName("NameBox")).Text = "教学平台 · 常用资源";
            Click(main, "ShortcutSave", true);
            Focused(first.Id);
            Assert(catalog.Read()[0].Name == "教学平台 · 常用资源", "editor save did not persist");
            Click(main, "MoveShortcutDown");
            Focused(first.Id);
            Assert(catalog.Read().Last().Id == first.Id, "reordering did not persist the selected shortcut");
            Click(main, "RemoveShortcutButton");
            Focused(second.Id);
            Click(main, "UndoShortcutDelete");
            Focused(first.Id);
            Assert(catalog.Read().Select(item => item.Id).SequenceEqual([second.Id, first.Id]), "undo did not restore the original order");
            Snapshot(main, "teacher-shortcut-list.png");
            Checks.Add("Shortcut manager keyboard focus: same-HWND edit cancel/save, native row navigation, reorder, deletion neighbour and restored row; isolated local catalog");
        }
        finally
        {
            main.Close();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
