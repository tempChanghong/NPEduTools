using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NPEduTools.App;

public sealed record ShortcutEntry(Guid Id, string Name, string Kind, string Target)
{
    [JsonIgnore] public string KindLabel => Kind switch { "app" => "应用", "file" => "文件", _ => "网址" };
    [JsonIgnore] public string Glyph => Kind switch { "app" => "\uECAA", "file" => "\uE8A5", _ => "\uE774" };
    [JsonIgnore] public string Tint => Kind switch { "app" => "#EEEAF8", "file" => "#FFF0E8", _ => "#E7F4F0" };
    [JsonIgnore] public string Ink => Kind switch { "app" => "#7968A3", "file" => "#BC724C", _ => "#147D68" };
    [JsonIgnore] public string OpenId => "ShortcutOpen_" + Id.ToString("N");
    [JsonIgnore] public string Tip => Name + "\n" + Target;
}

public sealed record ShortcutDocument(int Version, ShortcutEntry[] Items);

public sealed class ShortcutCatalog(string path)
{
    public const int MaximumItems = 24;
    public static string PathFor(string endpoint) => StartupPreferencesStore.PathFor(endpoint).Replace(".startup.json", ".shortcuts.json", StringComparison.Ordinal);

    public ShortcutEntry[] Read()
    {
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > 128 * 1024) throw new InvalidDataException("快捷启动配置过大。");
        var document = JsonSerializer.Deserialize<ShortcutDocument>(File.ReadAllText(path));
        if (document is null || document.Version != 1 || document.Items is null)
            throw new InvalidDataException("无法识别快捷启动配置版本。");
        ValidateList(document.Items);
        return document.Items;
    }

    public void Save(ShortcutEntry[] items)
    {
        ValidateList(items);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new ShortcutDocument(1, items)));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ValidateList(ShortcutEntry[] items)
    {
        if (items.Length > MaximumItems || items.Any(item => item is null) || items.Select(item => item.Id).Distinct().Count() != items.Length)
            throw new InvalidDataException("快捷启动配置包含重复项目或项目过多。");
        foreach (var item in items) Normalize(item);
    }

    public static ShortcutEntry Normalize(ShortcutEntry entry)
    {
        string name = entry.Name?.Trim() ?? "", target = entry.Target?.Trim() ?? "";
        if (entry.Id == Guid.Empty || name.Length is < 1 or > 40 || name.Any(char.IsControl))
            throw new InvalidDataException("请填写 1–40 个字符的名称。");
        if (target.Length is < 1 or > 2048 || target.Any(char.IsControl)) throw new InvalidDataException("请填写有效的路径或网址。");
        if (entry.Kind == "url")
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || string.IsNullOrEmpty(uri.Host))
                throw new InvalidDataException("网址需要以 https:// 或 http:// 开头。");
            target = uri.AbsoluteUri;
        }
        else if (entry.Kind is "app" or "file")
        {
            if (!Path.IsPathFullyQualified(target) || target.IndexOfAny(['"', '<', '>', '|', '*', '?']) >= 0)
                throw new InvalidDataException("请选择文件，或填写完整的文件路径。");
            target = Path.GetFullPath(target);
            if (entry.Kind == "app" && Path.GetExtension(target).ToLowerInvariant() is not (".exe" or ".lnk"))
                throw new InvalidDataException("应用请选择 .exe 程序或 .lnk 快捷方式。");
        }
        else throw new InvalidDataException("请选择应用、文件或网址。");
        return entry with { Name = name, Target = target };
    }

    public static ProcessStartInfo PrepareLaunch(ShortcutEntry entry)
    {
        entry = Normalize(entry);
        if (entry.Kind != "url" && !File.Exists(entry.Target))
            throw new FileNotFoundException("文件不存在或无法访问，请点击“编辑”重新选择位置。", entry.Target);
        return new ProcessStartInfo(entry.Target)
        {
            UseShellExecute = true,
            WorkingDirectory = entry.Kind == "url" ? "" : Path.GetDirectoryName(entry.Target)!,
            Verb = "open"
        };
    }
}
