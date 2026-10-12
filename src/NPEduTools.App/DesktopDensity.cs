using System.IO;
using System.Text.Json;

namespace NPEduTools.App;

internal static class DesktopDensity
{
    public static double Resolve(double preference, double width, double height, double designWidth = 1040, double designHeight = 760)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return .9;
        double desired = preference is .8 or .9 or 1 ? preference : .9;
        // Work area is in WPF units, after Windows DPI scaling. Never multiply DPI twice.
        return Math.Clamp(Math.Min(desired, Math.Min((width - 32) / designWidth, (height - 32) / designHeight)), .5, 1);
    }
    private static string PathFor(string endpoint) => StartupPreferencesStore.PathFor(endpoint) + ".density";
    public static double Read(string endpoint)
    {
        string path = PathFor(endpoint);
        if (!File.Exists(path)) return 0;
        if (new FileInfo(path).Length > 128) throw new InvalidDataException("界面大小配置过大。");
        double value = JsonSerializer.Deserialize<double>(File.ReadAllText(path));
        if (value is not (0 or .8 or .9 or 1)) throw new InvalidDataException("界面大小配置无效。");
        return value;
    }
    public static void Save(string endpoint, double value)
    {
        if (value is not (0 or .8 or .9 or 1)) throw new ArgumentOutOfRangeException(nameof(value));
        string path = PathFor(endpoint), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
