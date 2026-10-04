using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace NPEduTools.App;

public partial class AgreementsWindow : Window
{
    private readonly IReadOnlyList<AgreementDefinition> _agreements;
    private readonly AgreementAcceptanceStore _store;
    private readonly bool _required;
    public AgreementsWindow(string endpoint, bool required = true)
    {
        _required = required;
        _store = new(AgreementAcceptanceStore.PathFor(endpoint));
        _agreements = AgreementCatalog.Load();
        InitializeComponent();
        try
        {
            var saved = _store.Read();
            AcceptApp.IsChecked = saved?.HasAccepted(_agreements[0]) == true;
            AcceptService.IsChecked = saved?.HasAccepted(_agreements[1]) == true;
            AcceptPrivacy.IsChecked = saved?.HasAccepted(_agreements[2]) == true;
            AcceptanceStatus.Text = saved?.IsCurrent(_agreements) == true
                ? $"三份协议已分别确认。\n记录时间：{saved.Agreements.Max(a => a.AcceptedAtUtc).ToLocalTime():yyyy-MM-dd HH:mm}"
                : "三份协议需分别确认，未确认项不会预先勾选。";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { ErrorText.Text = "旧确认记录无法读取，原文件保留。请重新阅读并分别确认；保存失败时不会进入应用。"; }
        if (!required)
        {
            AcceptApp.IsEnabled = AcceptService.IsEnabled = AcceptPrivacy.IsEnabled = false;
            ContinueButton.Visibility = Visibility.Collapsed;
            DeclineButton.Content = "关闭";
            Introduction.Text = "查看当前三份协议及本机确认记录。学校设备管理授权与个人信息处理同意是不同事项。";
        }
        ShowDocument(_agreements[0]);
        AcceptanceChanged(this, new RoutedEventArgs());
    }
    private void AcceptanceChanged(object sender, RoutedEventArgs e)
    {
        if (ContinueButton is null) return;
        ContinueButton.IsEnabled = _required && AcceptApp.IsChecked == true && AcceptService.IsChecked == true && AcceptPrivacy.IsChecked == true;
    }
    private void ReadClicked(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: string id }) ShowDocument(id == "gpl-3.0" ? AgreementCatalog.License : _agreements.Single(a => a.Id == id)); }
    private void ShowDocument(AgreementDefinition agreement)
    {
        DocumentVersion.Text = agreement.Id == "gpl-3.0"
            ? "GNU GPL · 第三版\n保障许可证授予的使用、修改及再分发权利，不另行要求服务同意。"
            : "协议版本 1.0\n生效：2026年10月4日";
        var document = new FlowDocument { FontFamily = FontFamily, FontSize = 14, PagePadding = new Thickness(6), LineHeight = 25 };
        Table? table = null;
        foreach (string raw in agreement.Text.Split('\n'))
        {
            string text = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(text)) { table = null; continue; }
            string Plain(string value) => Regex.Replace(value, @"\[([^\]]+)\]\([^)]+\)", "$1").Replace("**", "").Replace("`", "");
            if (text.StartsWith('|'))
            {
                if (Regex.IsMatch(text, @"^\|[\s|:\-]+$")) continue;
                if (table is null) { table = new Table { CellSpacing = 0, BorderThickness = new Thickness(1), BorderBrush = System.Windows.Media.Brushes.LightGray }; table.RowGroups.Add(new TableRowGroup()); document.Blocks.Add(table); }
                var row = new TableRow();
                foreach (string cell in text.Trim('|').Split('|')) row.Cells.Add(new TableCell(new Paragraph(new Run(Plain(cell.Trim())))) { Padding = new Thickness(8), BorderThickness = new Thickness(0, 0, 1, 1), BorderBrush = System.Windows.Media.Brushes.LightGray });
                table.RowGroups[0].Rows.Add(row); continue;
            }
            table = null;
            int heading = text.TakeWhile(c => c == '#').Count();
            var paragraph = new Paragraph { Margin = new Thickness(0, heading > 0 ? 16 : 0, 0, 10) };
            if (heading > 0) { paragraph.FontSize = heading == 1 ? 23 : 18; paragraph.FontWeight = FontWeights.SemiBold; text = text[heading..].TrimStart(); }
            foreach (string part in Regex.Split(text, @"(\*\*.*?\*\*)")) { var run = new Run(Plain(part)); if (part.StartsWith("**", StringComparison.Ordinal)) run.FontWeight = FontWeights.SemiBold; paragraph.Inlines.Add(run); }
            document.Blocks.Add(paragraph);
        }
        DocumentViewer.Document = document;
        DocumentViewer.Dispatcher.BeginInvoke(() =>
        {
            if (DocumentViewer.Template?.FindName("PART_ContentHost", DocumentViewer) is ScrollViewer scroll) scroll.ScrollToHome();
        });
    }
    private void ContinueClicked(object sender, RoutedEventArgs e)
    {
        if (!ContinueButton.IsEnabled || !_required) return;
        try
        {
            var selected = new[] { AcceptApp.IsChecked == true ? _agreements[0].Id : "", AcceptService.IsChecked == true ? _agreements[1].Id : "", AcceptPrivacy.IsChecked == true ? _agreements[2].Id : "" };
            _store.Save(AgreementAcceptanceRecord.Create(_agreements, selected, DateTimeOffset.UtcNow));
            DialogResult = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { ErrorText.Text = "确认记录未保存，尚未进入应用。请检查配置目录权限后重试，也可以退出。"; }
    }
    private void DeclineClicked(object sender, RoutedEventArgs e) { if (_required) DialogResult = false; else Close(); }
    private void WebsiteClicked(object sender, RoutedEventArgs e)
    {
        try { using var process = Process.Start(new ProcessStartInfo("https://novark.ink") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { ErrorText.Text = "无法打开浏览器，请手动访问 https://novark.ink 。"; }
    }
}
