using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NPEduTools.App;

public sealed record StartupPreferences(int Version = 1, bool EdgeOnlyAtLogin = true, bool EnableTouchOnLaunch = false);

public sealed class StartupPreferencesStore(string path)
{
    public static string PathFor(string endpoint) => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NPEduTools", "ui",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)))[..24] + ".startup.json");

    public StartupPreferences Read()
    {
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 4096) throw new InvalidDataException("启动偏好文件过大。");
        var settings = JsonSerializer.Deserialize<StartupPreferences>(File.ReadAllText(path));
        if (settings is null || settings.Version != 1) throw new InvalidDataException("启动偏好版本不受支持。");
        return settings;
    }

    public void Save(StartupPreferences settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
