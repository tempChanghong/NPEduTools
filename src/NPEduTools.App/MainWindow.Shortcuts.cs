using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace NPEduTools.App;

public partial class MainWindow
{
    private ShortcutCatalog _shortcutStore = null!;
    private ShortcutEntry[] _shortcuts = [];
    private ShortcutEntry[]? _beforeShortcutDelete;
    private bool _shortcutsReadable, _shortcutBusy;
    private Guid? _shortcutRepairId;
    private string _shortcutMessage = "";

    private void InitializeShortcuts()
    {
        _shortcutStore = new(ShortcutCatalog.PathFor(_pipe));
        ReloadShortcuts();
    }

    private void ReloadShortcuts()
    {
        if (_shortcutBusy) return;
        try
        {
            _shortcuts = _shortcutStore.Read(); _shortcutsReadable = true;
            _beforeShortcutDelete = null; SetShortcutMessage("");
        }
        catch (Exception error) when (IsShortcutError(error))
        {
            _shortcutsReadable = false; _shortcuts = [];
            SetShortcutMessage("快捷启动配置未能读取，原文件已保留。修复配置后可重新读取。");
        }
        PopulateShortcuts();
    }

    private void PopulateShortcuts(Guid? selection = null)
    {
        HomeShortcutItems.ItemsSource = _shortcuts;
        ShortcutList.ItemsSource = _shortcuts;
        ShortcutList.SelectedItem = _shortcuts.FirstOrDefault(item => item.Id == selection);
        ShortcutEmpty.Visibility = _shortcuts.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        HomeShortcutEmpty.Visibility = ShortcutEmpty.Visibility;
        ShortcutCount.Text = $"{_shortcuts.Length} / {ShortcutCatalog.MaximumItems} 项 · 顺序同步到贴边面板";
        _quick?.SetShortcuts(_shortcuts);
        RefreshShortcutControls();
    }

    private void RefreshShortcutControls()
    {
        bool editable = _shortcutsReadable && !_shortcutBusy;
        int index = ShortcutList.SelectedIndex;
        AddShortcutButton.IsEnabled = editable && _shortcuts.Length < ShortcutCatalog.MaximumItems;
        EditShortcutButton.IsEnabled = RemoveShortcutButton.IsEnabled = editable && index >= 0;
        MoveShortcutUp.IsEnabled = editable && index > 0;
        MoveShortcutDown.IsEnabled = editable && index >= 0 && index < _shortcuts.Length - 1;
        UndoShortcutDelete.IsEnabled = editable && _beforeShortcutDelete is not null;
        UndoShortcutDelete.Visibility = _beforeShortcutDelete is null ? Visibility.Collapsed : Visibility.Visible;
        HomeShortcutItems.IsEnabled = ShortcutList.IsEnabled = !_shortcutBusy;
        RepairShortcutButton.Visibility = _shortcutRepairId is null ? Visibility.Collapsed : Visibility.Visible;
        RepairShortcutButton.IsEnabled = editable;
        _quick?.SetShortcutStatus(_shortcutMessage, !_shortcutBusy, _shortcutRepairId is not null);
    }

    private void SetShortcutMessage(string message, Guid? repair = null)
    {
        _shortcutMessage = message; _shortcutRepairId = repair;
        ShortcutMessage.Text = HomeShortcutMessage.Text = message;
        RefreshShortcutControls();
    }

    private void ShortcutNavClicked(object sender, RoutedEventArgs e) => ShowShortcutManager();
    private void ShowShortcutManager()
    {
        SelectPage(false);
        HomePage.Visibility = Visibility.Collapsed; ShortcutPage.Visibility = Visibility.Visible;
        HomeNav.Tag = null; ShortcutNav.Tag = "active";
        RestoreWindow();
    }

    private void AddShortcutClicked(object sender, RoutedEventArgs e) => EditShortcut(null);
    private void EditShortcutClicked(object sender, RoutedEventArgs e) { if (ShortcutList.SelectedItem is ShortcutEntry entry) EditShortcut(entry); }
    private void EditShortcut(ShortcutEntry? entry)
    {
        if (!_shortcutsReadable || _shortcutBusy || (entry is null && _shortcuts.Length >= ShortcutCatalog.MaximumItems)) return;
        ShowShortcutManager();
        var editor = new ShortcutEditorWindow(entry) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } result) return;
        var next = entry is null ? [.. _shortcuts, result] : _shortcuts.Select(item => item.Id == entry.Id ? result : item).ToArray();
        if (SaveShortcuts(next, result.Id)) SetShortcutMessage("已保存，主页面与贴边面板已同步。");
    }

    private bool SaveShortcuts(ShortcutEntry[] next, Guid? selection = null)
    {
        try
        {
            _shortcutStore.Save(next); _shortcuts = next; _beforeShortcutDelete = null;
            PopulateShortcuts(selection); return true;
        }
        catch (Exception error) when (IsShortcutError(error))
        { SetShortcutMessage("未能保存更改，列表保持原样。请检查配置目录的访问权限。"); return false; }
    }

    private void RemoveShortcutClicked(object sender, RoutedEventArgs e)
    {
        if (_shortcutBusy || ShortcutList.SelectedItem is not ShortcutEntry entry) return;
        var before = _shortcuts;
        if (!SaveShortcuts(_shortcuts.Where(item => item.Id != entry.Id).ToArray())) return;
        _beforeShortcutDelete = before;
        SetShortcutMessage($"已移除“{entry.Name}”。原应用或文件仍保留，可撤销本次移除。");
    }

    private void UndoShortcutClicked(object sender, RoutedEventArgs e)
    {
        if (!_shortcutBusy && _beforeShortcutDelete is { } before && SaveShortcuts(before)) SetShortcutMessage("已恢复移除前的列表。");
    }

    private void MoveShortcutClicked(object sender, RoutedEventArgs e)
    {
        if (_shortcutBusy || ShortcutList.SelectedItem is not ShortcutEntry entry) return;
        int current = ShortcutList.SelectedIndex, nextIndex = current + (ReferenceEquals(sender, MoveShortcutUp) ? -1 : 1);
        if (nextIndex < 0 || nextIndex >= _shortcuts.Length) return;
        var next = _shortcuts.ToArray(); (next[current], next[nextIndex]) = (next[nextIndex], next[current]);
        if (SaveShortcuts(next, entry.Id)) SetShortcutMessage("顺序已保存。");
    }

    private void ShortcutSelectionChanged(object sender, SelectionChangedEventArgs e) { if (AddShortcutButton is not null) RefreshShortcutControls(); }
    private void ReloadShortcutsClicked(object sender, RoutedEventArgs e) => ReloadShortcuts();
    private void RepairShortcutClicked(object sender, RoutedEventArgs e) => RepairShortcut();
    private void RepairShortcut()
    {
        if (_shortcuts.FirstOrDefault(item => item.Id == _shortcutRepairId) is { } entry) EditShortcut(entry);
    }

    private void OpenShortcutClicked(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: ShortcutEntry entry }) _ = OpenShortcutAsync(entry); }

    private async Task OpenShortcutAsync(ShortcutEntry entry)
    {
        if (_shortcutBusy || _exitBusy || _exiting || !_shortcuts.Any(item => item.Id == entry.Id)) return;
        _shortcutBusy = true;
        SetShortcutMessage($"正在打开“{entry.Name}”…");
        // Shell handlers can require STA and may wait for an application. Keep the UI responsive.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { using var process = Process.Start(ShortcutCatalog.PrepareLaunch(entry)); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true, Name = "NPEduTools shortcut launch" };
        thread.SetApartmentState(ApartmentState.STA);
        try
        {
            thread.Start(); await completion.Task;
            if (!_exiting) SetShortcutMessage($"已将“{entry.Name}”交给 Windows 打开。");
        }
        catch (Exception error) when (IsShortcutError(error))
        {
            string message = error switch
            {
                FileNotFoundException => "文件不存在或无法访问，请编辑此项并重新选择位置。",
                Win32Exception { NativeErrorCode: 1223 } => "已取消打开。",
                Win32Exception { NativeErrorCode: 1155 } => "没有可打开此文件的默认应用，请在 Windows 中设置文件关联。",
                _ => "Windows 未能打开此项，请检查路径、访问权限或默认应用。"
            };
            if (!_exiting) SetShortcutMessage($"{entry.Name}：{message}", entry.Id);
        }
        finally
        {
            await Task.Delay(700); // Coalesce a touchscreen double tap across both entry points.
            _shortcutBusy = false;
            if (!_exiting) RefreshShortcutControls();
        }
    }

    private static bool IsShortcutError(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or
        ArgumentException or NotSupportedException or InvalidOperationException or Win32Exception or System.Security.SecurityException;
}
