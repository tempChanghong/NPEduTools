using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace NPEduTools.Core;

public sealed record ManualStudyPreferences(int Version = 1, int DisplayLeadSeconds = 120);
public sealed class ManualStudyPreferencesStore(string path)
{
    public static string PathFor(string endpoint) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NPEduTools", "ui", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)))[..24] + ".manual-study.json");
    public ManualStudyPreferences Read()
    {
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 4096) throw new InvalidDataException("手动自习偏好文件过大。");
        var value = JsonSerializer.Deserialize<ManualStudyPreferences>(File.ReadAllText(path));
        if (value is null || value.Version != 1 || value.DisplayLeadSeconds is < 0 or > 1800)
            throw new InvalidDataException("手动自习展示提前时间无效。");
        return value;
    }
    public void Save(ManualStudyPreferences value)
    {
        if (value.Version != 1 || value.DisplayLeadSeconds is < 0 or > 1800) throw new InvalidDataException("手动自习展示提前时间无效。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(value)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
