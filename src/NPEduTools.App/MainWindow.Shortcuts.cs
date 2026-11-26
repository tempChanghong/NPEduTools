using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace NPEduTools.App;

public partial class MainWindow
{
    private ShortcutCatalog _shortcutStore = null!;
    private ShortcutEntry[] _shortcuts = [];
    private (ShortcutEntry[] Items, Guid RemovedId)? _beforeShortcutDelete;
    private sealed record ShortcutEdit(ShortcutEntry? Original, Guid? Selection);
    private ShortcutEdit? _shortcutEdit;
    private bool _shortcutsReadable, _shortcutBusy;
    private Guid? _shortcutRepairId;
    private string _railShortcutMessage = "";

    private void InitializeShortcuts()
    {
        _shortcutStore = new(ShortcutCatalog.PathFor(_pipe));
        ShortcutEditor.SaveRequested += SaveShortcutDraft;
        ShortcutEditor.CancelRequested += CancelShortcutDraft;
        ReloadShortcuts();
    }

    private void ReloadShortcuts()
    {
        if (_shortcutBusy || _shortcutEdit is not null) return;
        try
        {
            _shortcuts = _shortcutStore.Read(); _shortcutsReadable = true;
            _beforeShortcutDelete = null; SetShortcutMessage("");
        }
        catch (Exception error) when (IsShortcutError(error))
        {
            _shortcutsReadable = false; _shortcuts = [];
            SetShortcutMessage("快捷启动配置未能读取，原文件已保留。修复配置后可重新读取。", error: true);
        }
        PopulateShortcuts();
    }

    private void PopulateShortcuts(Guid? selection = null)
    {
        HomeShortcutItems.ItemsSource = _shortcuts.Take(6).ToArray();
        ShortcutList.ItemsSource = _shortcuts;
        ShortcutList.SelectedItem = _shortcuts.FirstOrDefault(item => item.Id == selection);
        ShortcutEmpty.Visibility = _shortcutsReadable && _shortcuts.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        HomeShortcutEmpty.Visibility = ShortcutEmpty.Visibility;
        HomeShortcutCount.Text = !_shortcutsReadable ? "暂不可用"
            : _shortcuts.Length > 6 ? $"{_shortcuts.Length} 项 · 显示前 6 项" : $"{_shortcuts.Length} 项";
        ShortcutCount.Text = _shortcutsReadable ? $"{_shortcuts.Length} / {ShortcutCatalog.MaximumItems} 项" : "快捷项暂不可用";
        ShortcutSelectionText.Visibility = _shortcuts.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _quick?.SetShortcuts(_shortcuts);
        RefreshShortcutControls();
    }

    private void RefreshShortcutControls()
    {
        bool editable = _shortcutsReadable && !_shortcutBusy && _shortcutEdit is null;
        int index = ShortcutList.SelectedIndex;
        AddShortcutButton.IsEnabled = editable && _shortcuts.Length < ShortcutCatalog.MaximumItems;
        EditShortcutButton.IsEnabled = RemoveShortcutButton.IsEnabled = editable && index >= 0;
        MoveShortcutUp.IsEnabled = editable && index > 0;
        MoveShortcutDown.IsEnabled = editable && index >= 0 && index < _shortcuts.Length - 1;
        UndoShortcutDelete.IsEnabled = editable && _beforeShortcutDelete is not null;
        UndoShortcutDelete.Visibility = _beforeShortcutDelete is null ? Visibility.Collapsed : Visibility.Visible;
        HomeShortcutItems.IsEnabled = ShortcutList.IsEnabled = !_shortcutBusy;
        ReloadShortcutsButton.IsEnabled = !_shortcutBusy && _shortcutEdit is null;
        ShortcutEditor?.SetSaveEnabled(_shortcutsReadable && !_shortcutBusy);
        RepairShortcutButton.Visibility = _shortcutRepairId is null ? Visibility.Collapsed : Visibility.Visible;
        RepairShortcutButton.IsEnabled = editable;
        _quick?.SetShortcutStatus(_railShortcutMessage, !_shortcutBusy, _shortcutRepairId is not null);
    }

    private void SetShortcutMessage(string message, Guid? repair = null, bool error = false, string? railMessage = null)
    {
        _railShortcutMessage = railMessage ?? message; _shortcutRepairId = repair;
        ShortcutMessage.Text = HomeShortcutMessage.Text = message;
        string brush = error || repair is not null ? "TeacherErrorBrush" : "TeacherMutedBrush";
        ShortcutMessage.SetResourceReference(TextBlock.ForegroundProperty, brush);
        HomeShortcutMessage.SetResourceReference(TextBlock.ForegroundProperty, brush);
        RefreshShortcutControls();
    }

    private void ShortcutNavClicked(object sender, RoutedEventArgs e) => ShowShortcutManager();
    private void ShowShortcutManager()
    {
        SelectPage(false);
        HomePage.Visibility = Visibility.Collapsed; ShortcutPage.Visibility = Visibility.Visible;
        HomeNav.Tag = null; ShortcutNav.Tag = "active";
        PageBreadcrumb.Text = "快捷启动";
        RestoreWindow();
        if (_shortcutEdit is not null) FocusShortcutDraft();
    }

    private void AddShortcutClicked(object sender, RoutedEventArgs e) => EditShortcut(null);
    private void EditShortcutClicked(object sender, RoutedEventArgs e) { if (ShortcutList.SelectedItem is ShortcutEntry entry) EditShortcut(entry); }
    private void EditShortcut(ShortcutEntry? entry)
    {
        if (_shortcutEdit is { } open)
        {
            ShowShortcutManager();
            if (open.Original?.Id != entry?.Id) ShortcutEditor.ShowError("请先保存或取消当前编辑，再编辑另一项。");
            return;
        }
        if (!_shortcutsReadable || _shortcutBusy || (entry is null && _shortcuts.Length >= ShortcutCatalog.MaximumItems)) return;
        Guid? previous = entry?.Id ?? (ShortcutList.SelectedItem as ShortcutEntry)?.Id;
        _shortcutEdit = new(entry, previous);
        ShowShortcutManager();
        ShortcutList.SelectedItem = _shortcuts.FirstOrDefault(item => item.Id == previous);
        ShortcutEditor.Begin(entry);
        UpdateShortcutLayout();
        RefreshShortcutControls();
    }

    private void ShortcutLayoutChanged(object sender, SizeChangedEventArgs e) => UpdateShortcutLayout();

    private void UpdateShortcutLayout()
    {
        if (ShortcutEditor is null) return;
        bool editing = _shortcutEdit is not null;
        bool split = editing && ShortcutPage.ActualWidth >= 840;
        ShortcutListPane.Visibility = editing && !split ? Visibility.Collapsed : Visibility.Visible;
        ShortcutEditorColumn.Width = split ? new GridLength(0.95, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(ShortcutEditor, split ? 1 : 0);
        ShortcutEditor.Margin = split ? new Thickness(16, 0, 0, 0) : new Thickness(0);
        ShortcutEditor.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FocusShortcutDraft() => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
    {
        if (ShortcutEditor.IsVisible && _shortcutEdit is not null && IsActive) ShortcutEditor.FocusName();
    });

    private void SaveShortcutDraft(ShortcutEntry result)
    {
        if (_shortcutEdit is not { } edit || _shortcutBusy) return;
        if (edit.Original is { } original && !_shortcuts.Any(item => item.Id == original.Id))
        {
            ShortcutEditor.ShowError("原快捷项已不存在，当前输入仍保留。请核对后再处理。");
            return;
        }
        var next = edit.Original is null ? [.. _shortcuts, result] :
            _shortcuts.Select(item => item.Id == edit.Original.Id ? result : item).ToArray();
        if (!WriteShortcuts(next, ShortcutEditor.ShowError)) return;
        _shortcutEdit = null;
        UpdateShortcutLayout();
        ApplySavedShortcuts(next, result.Id);
        SetShortcutMessage("已保存，首页与贴边面板已同步。");
        FocusShortcut(result.Id);
    }

    private void CancelShortcutDraft()
    {
        if (_shortcutEdit is not { } edit) return;
        _shortcutEdit = null;
        UpdateShortcutLayout();
        RefreshShortcutControls();
        FocusShortcut(edit.Selection);
    }

    private bool SaveShortcuts(ShortcutEntry[] next, Guid? selection = null)
    {
        if (!WriteShortcuts(next, message => SetShortcutMessage(message, error: true))) return false;
        ApplySavedShortcuts(next, selection);
        return true;
    }

    private bool WriteShortcuts(ShortcutEntry[] next, Action<string> onError)
    {
        try
        {
            _shortcutStore.Save(next);
            return true;
        }
        catch (Exception error) when (IsShortcutError(error))
        {
            onError(error is InvalidDataException
                ? $"未能保存更改，列表保持原样。{error.Message}"
                : "未能保存更改，列表保持原样。请检查配置文件是否被占用，或目录是否可写。");
            return false;
        }
    }

    private void ApplySavedShortcuts(ShortcutEntry[] next, Guid? selection)
    {
        _shortcuts = next;
        _beforeShortcutDelete = null;
        PopulateShortcuts(selection);
    }

    private void RemoveShortcutClicked(object sender, RoutedEventArgs e)
    {
        if (_shortcutBusy || _shortcutEdit is not null || ShortcutList.SelectedItem is not ShortcutEntry entry) return;
        var before = _shortcuts;
        int index = ShortcutList.SelectedIndex;
        var next = _shortcuts.Where(item => item.Id != entry.Id).ToArray();
        Guid? neighbour = next.Length == 0 ? null : next[Math.Min(index, next.Length - 1)].Id;
        if (!SaveShortcuts(next, neighbour)) return;
        _beforeShortcutDelete = (before, entry.Id);
        SetShortcutMessage($"已移除“{entry.Name}”。原应用或文件仍保留，可撤销本次移除。");
        FocusShortcut(neighbour);
    }

    private void UndoShortcutClicked(object sender, RoutedEventArgs e)
    {
        if (_shortcutBusy || _shortcutEdit is not null || _beforeShortcutDelete is not { } before || !SaveShortcuts(before.Items, before.RemovedId)) return;
        SetShortcutMessage("已恢复移除前的列表。");
        FocusShortcut(before.RemovedId);
    }

    private void MoveShortcutClicked(object sender, RoutedEventArgs e)
    {
        if (_shortcutBusy || _shortcutEdit is not null || ShortcutList.SelectedItem is not ShortcutEntry entry) return;
        int current = ShortcutList.SelectedIndex, nextIndex = current + (ReferenceEquals(sender, MoveShortcutUp) ? -1 : 1);
        if (nextIndex < 0 || nextIndex >= _shortcuts.Length) return;
        var next = _shortcuts.ToArray(); (next[current], next[nextIndex]) = (next[nextIndex], next[current]);
        if (!SaveShortcuts(next, entry.Id)) return;
        SetShortcutMessage("顺序已保存。");
        FocusShortcut(entry.Id);
    }

    private void FocusShortcut(Guid? id)
    {
        if (!ShortcutPage.IsVisible) return;
        if (id is not null && _shortcuts.FirstOrDefault(item => item.Id == id) is { } entry)
        {
            ShortcutList.SelectedItem = entry;
            ShortcutList.ScrollIntoView(entry);
            ShortcutList.UpdateLayout();
            if (ShortcutList.ItemContainerGenerator.ContainerFromItem(entry) is ListBoxItem row && row.Focus()) return;
            if (ShortcutList.Focus()) return;
        }
        if (!AddShortcutButton.Focus()) ReloadShortcutsButton.Focus();
    }

    private void ShortcutSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_shortcutEdit is { } edit && (ShortcutList.SelectedItem as ShortcutEntry)?.Id != edit.Selection)
            ShortcutList.SelectedItem = _shortcuts.FirstOrDefault(item => item.Id == edit.Selection);
        if (AddShortcutButton is not null) RefreshShortcutControls();
    }
    private void ReloadShortcutsClicked(object sender, RoutedEventArgs e) => ReloadShortcuts();
    private void RepairShortcutClicked(object sender, RoutedEventArgs e) => RepairShortcut();
    private void RepairShortcut()
    {
        if (_shortcuts.FirstOrDefault(item => item.Id == _shortcutRepairId) is { } entry) EditShortcut(entry);
    }

    private void OpenShortcutClicked(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: ShortcutEntry entry }) _ = OpenShortcutAsync(entry); }

    private void OpenShortcutFromRail(ShortcutEntry entry)
    {
        if (_quick is not { } rail) return;
        long view = rail.ShortcutViewRevision;
        _ = OpenShortcutAsync(entry, () => rail.CompleteShortcutHandoff(view));
    }

    private async Task OpenShortcutAsync(ShortcutEntry entry, Action? onHandoff = null)
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
            if (!_exiting)
            {
                SetShortcutMessage($"已将“{entry.Name}”交给 Windows 打开。", railMessage: "已交给 Windows 打开。");
                onHandoff?.Invoke();
            }
        }
        catch (Exception error) when (IsShortcutError(error))
        {
            var (message, railMessage) = error switch
            {
                FileNotFoundException => ("文件不存在或无法访问，请编辑此项并重新选择位置。", "文件缺失或不可读。"),
                Win32Exception { NativeErrorCode: 1223 } => ("已取消打开。", "已取消打开。"),
                Win32Exception { NativeErrorCode: 1155 } => ("没有可打开此文件的默认应用，请在 Windows 中设置文件关联。", "未设置默认应用。"),
                _ => ("Windows 未能打开此项，请检查路径、访问权限或默认应用。", "Windows 未能打开此项。")
            };
            if (!_exiting) SetShortcutMessage($"{entry.Name}：{message}", entry.Id, railMessage: railMessage);
        }
        finally
        {
            await Task.Delay(700); // Coalesce a touchscreen double tap across both entry points.
            _shortcutBusy = false;
            if (!_exiting) RefreshShortcutControls();
        }
    }

    private static bool IsShortcutError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
        ArgumentException or NotSupportedException or InvalidOperationException or Win32Exception or System.Security.SecurityException;
}
