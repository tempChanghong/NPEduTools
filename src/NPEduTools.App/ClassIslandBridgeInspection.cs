using System.IO;
using NPEduTools.Contracts;

namespace NPEduTools.App;

internal static class ClassIslandBridgeInspection
{
    public static string Describe(string? executable, SchoolClockFrame? frame, double roundTripMs = 0,
        string? installedDataRoot = null)
    {
        // Only a compatible runtime handshake establishes that this plugin was loaded.
        if (frame is { BridgeInstanceId: var id, SchoolNow: not null } && id != Guid.Empty &&
            frame.State is "Advancing" or "WarmingUp" or "Discontinuous" or "FrozenSuspected" &&
            double.IsFinite(frame.AgeMs) && frame.AgeMs >= 0 && double.IsFinite(roundTripMs) && roundTripMs >= 0 &&
            frame.AgeMs + roundTripMs < SchoolClockFrame.MaxAgeMs)
            return "时间桥接插件：已加载并连接 · " + (frame.State == "Advancing" ? "学校时间正在更新" : "时间仍需校验：" + frame.State);
        if (string.IsNullOrWhiteSpace(executable)) return "时间桥接插件：安装状态未确认，请先保存 ClassIsland 程序路径。";
        try
        {
            string root = Path.GetDirectoryName(Path.GetFullPath(executable))!;
            var roots = new[] { root, Path.Combine(root, "data"), installedDataRoot ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClassIsland", "Data") };
            var findings = new List<string>();
            foreach (string dataRoot in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string plugin = Path.Combine(dataRoot, "Plugins", "npedutools.recordingbridge");
                string manifest = Path.Combine(plugin, "manifest.yml");
                if (!File.Exists(manifest)) continue;
                if (new FileInfo(manifest).Length > 65536) { findings.Add("插件清单过大，未解析"); continue; }
                var lines = File.ReadAllLines(manifest);
                if (!lines.Any(line => line.Trim() is "id: npedutools.recordingbridge" or "id: 'npedutools.recordingbridge'" or "id: \"npedutools.recordingbridge\""))
                { findings.Add("插件清单身份不符"); continue; }
                string state = File.Exists(Path.Combine(plugin, ".uninstall")) ? "待卸载" :
                    File.Exists(Path.Combine(plugin, ".disabled")) ? "已禁用" :
                    !File.Exists(Path.Combine(plugin, "NPEduTools.ClassIsland.Bridge.dll")) ? "文件不完整" : "文件已安装，运行连接未确认";
                findings.Add(state + " · " + plugin);
            }
            return "时间桥接插件：" + (findings.Count == 0
                ? "常见安装目录未找到。请在 ClassIsland 插件页确认；自定义数据目录或外部插件路径需在本体检查。"
                : string.Join("\n", findings)) + "\n启用“NPEduTools 时间与课表桥接”后重启 ClassIsland；课程连接成功不代表此插件已连接。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { return "时间桥接插件：目录无法读取，安装和运行状态均未确认；请在 ClassIsland 插件页检查。"; }
    }
}
