using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using NPEduTools.Core;
namespace NPEduTools.App;
public partial class MainWindow
{
    private void ReadManualStudyPreferences()
    {
        try { ManualDisplayLead.Text = (new ManualStudyPreferencesStore(ManualStudyPreferencesStore.PathFor(_pipe)).Read().DisplayLeadSeconds / 60.0).ToString("0.##", CultureInfo.InvariantCulture); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
            ManualDisplayLead.Text = ""; ManualDisplayLeadMessage.Text = "设置无法读取，原文件已保留；请重新确认提前时间。";
        }
    }
    private void SaveManualDisplayLead(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(ManualDisplayLead.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var minutes) ||
            !double.IsFinite(minutes) || minutes is < 0 or > 30) {
            ManualDisplayLeadMessage.Text = "请输入 0–30 分钟，0 表示到点才打开展示。"; return;
        }
        try {
            new ManualStudyPreferencesStore(ManualStudyPreferencesStore.PathFor(_pipe)).Save(new(DisplayLeadSeconds: (int)Math.Round(minutes * 60)));
            ManualDisplayLeadMessage.Text = "已保存，适用于新建的手动自习；已有预约保持原提前时间。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ManualDisplayLeadMessage.Text = "保存失败，请检查设置目录的访问权限。"; }
    }
}
