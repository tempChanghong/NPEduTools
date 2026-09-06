using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace NPEduTools.App;

public partial class ShortcutEditorWindow : Window
{
    private readonly Guid _id;
    public ShortcutEntry? Result { get; private set; }
    public ShortcutEditorWindow(ShortcutEntry? entry = null)
    {
        _id = entry?.Id ?? Guid.NewGuid();
        InitializeComponent();
        Heading.Text = Title = entry is null ? "添加快捷启动" : "编辑快捷启动";
        if (entry is not null)
        {
            NameBox.Text = entry.Name; TargetBox.Text = entry.Target;
            KindBox.SelectedIndex = entry.Kind switch { "app" => 0, "file" => 1, _ => 2 };
        }
        UpdateKind();
        Loaded += (_, _) => NameBox.Focus();
    }

    private string Kind => (string)((ComboBoxItem)KindBox.SelectedItem).Tag;
    private void KindChanged(object sender, SelectionChangedEventArgs e) { if (Hint is not null) UpdateKind(); }
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
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = Kind == "app" ? "选择应用" : "选择文件", CheckFileExists = true, Multiselect = false,
            DereferenceLinks = false, Filter = Kind == "app" ? "应用与快捷方式|*.exe;*.lnk" : "所有文件|*.*"
        };
        if (picker.ShowDialog(this) != true) return;
        TargetBox.Text = picker.FileName;
        if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = Path.GetFileNameWithoutExtension(picker.FileName)[..Math.Min(40, Path.GetFileNameWithoutExtension(picker.FileName).Length)];
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Result = ShortcutCatalog.Normalize(new(_id, NameBox.Text, Kind, TargetBox.Text));
            DialogResult = true;
        }
        catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException)
        { ErrorText.Text = error.Message; }
    }
}
