using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace NPEduTools.App;

public partial class AboutPage : UserControl
{
    private sealed record Acknowledgement(string Name, string Description, string Url);
    public AboutPage()
    {
        InitializeComponent();
        var assembly = typeof(App).Assembly;
        string fullVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "未知版本";
        string[] parts = fullVersion.Split('+', 2);
        VersionBadge.Text = parts[0];
        ReleaseStatus.Text = parts[0].Contains("InDev", StringComparison.OrdinalIgnoreCase) || parts[0].Contains('-', StringComparison.Ordinal)
            ? "开发预览版 · 功能与正式发布包可能处于不同进度" : "版本详情与变化请查看发布说明";
        BuildInformation.Text = $"NPEduTools {parts[0]}\n程序集：{assembly.GetName().Version}\n运行时：{RuntimeInformation.FrameworkDescription}\n进程架构：{RuntimeInformation.ProcessArchitecture}\n构建基准：{(parts.Length > 1 ? parts[1] : "未附带源码提交信息")}\n本地 Debug 构建可能包含未提交改动。";
        Acknowledgements.ItemsSource = new Acknowledgement[]
        {
            new("ClassIsland", "课表、学校时间与课堂信息桥接", "https://github.com/ClassIsland/ClassIsland"),
            new("ExamAware2", "考试看板、方案投递与放映联动", "https://github.com/ExamAware/ExamAware2"),
            new("SecRandom", "本机课堂点名与闪抽", "https://github.com/SECTL/SecRandom"),
            new("FFmpeg", "屏幕与音频编码、成片检查", "https://ffmpeg.org/"),
            new("NAudio", "Windows 原生音频采集", "https://github.com/naudio/NAudio"),
            new(".NET / WPF", "应用运行时与 Windows 桌面界面", "https://github.com/dotnet/wpf")
        };
    }

    private void OpenLinkClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            ActionMessage.Text = "已请求在默认浏览器中打开链接。";
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        { ActionMessage.Text = $"无法打开默认浏览器，可手动访问：{uri.AbsoluteUri}"; }
    }

    private void CopyVersionClicked(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(BuildInformation.Text); ActionMessage.Text = "已复制版本信息，可粘贴到问题反馈中。"; }
        catch (ExternalException) { ActionMessage.Text = "剪贴板暂时被占用，请稍后重试。"; }
    }
}
