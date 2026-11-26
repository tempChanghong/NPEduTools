using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NPEduTools.App;

public partial class ShortcutEditorView : UserControl
{
    private ShortcutEntry? _initial;
    public ShortcutEditorView() => InitializeComponent();

    internal event Action<ShortcutEntry>? SaveRequested;
    internal event Action? CancelRequested;

    internal void Begin(ShortcutEntry? entry)
    {
        _initial = entry ?? new(Guid.NewGuid(), "", "app", "");
        Heading.Text = entry is null ? "添加快捷启动" : "编辑快捷启动";
        NameBox.Text = _initial.Name;
        TargetBox.Text = _initial.Target;
        KindBox.SelectedIndex = _initial.Kind switch { "app" => 0, "file" => 1, _ => 2 };
        UpdateKind();
        ErrorText.Text = "";
        EditorFormScroll.ScrollToTop();
    }

    internal void FocusName() => NameBox.Focus();
    internal void SetSaveEnabled(bool enabled) => SaveButton.IsEnabled = enabled;
    internal void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.BringIntoView();
    }

    private string Kind => (string)((ComboBoxItem)KindBox.SelectedItem).Tag;
    private ShortcutEntry Draft(ShortcutEntry original) => original with { Name = NameBox.Text, Target = TargetBox.Text, Kind = Kind };

    private void KindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Hint is not null && KindBox.SelectedItem is ComboBoxItem) UpdateKind();
    }

    private void UpdateKind()
    {
        TargetLabel.Text = Kind == "url" ? "网址" : Kind == "app" ? "程序位置" : "文件位置";
        BrowseButton.Visibility = Kind == "url" ? Visibility.Collapsed : Visibility.Visible;
        Hint.Text = Kind switch
        {
            "app" => "选择 .exe 程序或已有的 .lnk 快捷方式。",
            "file" => "课件、文档等文件会使用 Windows 默认应用打开。",
            _ => "输入完整网址，例如 https://example.com。"
        };
    }

    private void BrowseClicked(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not { } owner || Kind == "url") return;
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = Kind == "app" ? "选择应用" : "选择文件", CheckFileExists = true, Multiselect = false,
            DereferenceLinks = false, Filter = Kind == "app" ? "应用与快捷方式|*.exe;*.lnk" : "所有文件|*.*"
        };
        if (picker.ShowDialog(owner) != true) return;
        TargetBox.Text = picker.FileName;
        string name = Path.GetFileNameWithoutExtension(picker.FileName);
        if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = name[..Math.Min(40, name.Length)];
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (_initial is not { } initial || !SaveButton.IsEnabled) return;
        ShortcutEntry result;
        try { result = ShortcutCatalog.Normalize(Draft(initial)); }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            ShowError(error.Message);
            return;
        }
        SaveRequested?.Invoke(result);
    }

    private void CancelClicked(object sender, RoutedEventArgs e) => Cancel();

    private void EditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (NameBox.IsKeyboardFocusWithin || TargetBox.IsKeyboardFocusWithin))
        {
            e.Handled = true;
            SaveClicked(sender, e);
            return;
        }
        if (e.Key != Key.Escape || KindBox.IsDropDownOpen) return;
        e.Handled = true;
        Cancel();
    }

    private void Cancel()
    {
        if (_initial is { } initial && Draft(initial) != initial && Window.GetWindow(this) is { } owner &&
            MessageBox.Show(owner, "本次输入尚未保存。要放弃修改并返回列表吗？", "放弃快捷项修改", MessageBoxButton.OKCancel,
                MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        CancelRequested?.Invoke();
    }
}
