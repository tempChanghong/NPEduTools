using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NPEduTools.App;

public sealed record AgreementDefinition(string Id, string Title, string Text)
{
    public const string Version = "1.0";
    public const string EffectiveDate = "2026-10-04";
    public string Hash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Text)));
}

public static class AgreementCatalog
{
    public static AgreementDefinition License => Read("gpl-3.0", "GNU General Public License v3.0", "GPL-3.0.txt");
    public static IReadOnlyList<AgreementDefinition> Load() =>
    [
        Read("npedutools-service", "NPEduTools服务协议", "NPEduTools-SERVICE-AGREEMENT.md"),
        Read("npep-service", "NOVARK POWER EDUCATION PLUS计划服务协议", "NPEP-SERVICE-AGREEMENT.md"),
        Read("npep-privacy", "NOVARK POWER EDUCATION PLUS计划隐私协议", "NPEP-PRIVACY-AGREEMENT.md")
    ];

    private static AgreementDefinition Read(string id, string title, string file)
    {
        using var stream = typeof(AgreementCatalog).Assembly.GetManifestResourceStream("NPEduTools.Legal." + file)
            ?? throw new InvalidDataException("协议正文缺失，请使用完整程序包。");
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return new(id, title, reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal));
    }
}

public sealed record AgreementAcceptance(string Id, string Version, string EffectiveDate, string Hash, DateTimeOffset AcceptedAtUtc);
public sealed record AgreementAcceptanceRecord(int SchemaVersion, AgreementAcceptance[] Agreements)
{
    public bool HasAccepted(AgreementDefinition agreement) => Agreements.Any(a => a.Id == agreement.Id &&
        a.Version == AgreementDefinition.Version && a.EffectiveDate == AgreementDefinition.EffectiveDate && a.Hash == agreement.Hash);
    public bool IsCurrent(IReadOnlyList<AgreementDefinition> agreements) => agreements.All(HasAccepted);
    public static AgreementAcceptanceRecord Create(IReadOnlyList<AgreementDefinition> agreements, IEnumerable<string> selected, DateTimeOffset now)
    {
        var ids = selected.ToHashSet(StringComparer.Ordinal);
        if (agreements.Count != 3 || ids.Count != 3 || agreements.Any(a => !ids.Contains(a.Id)))
            throw new InvalidDataException("请分别确认三份协议。");
        return new(1, agreements.Select(a => new AgreementAcceptance(a.Id, AgreementDefinition.Version,
            AgreementDefinition.EffectiveDate, a.Hash, now.ToUniversalTime())).ToArray());
    }
}

public sealed class AgreementAcceptanceStore(string path)
{
    public static string PathFor(string endpoint) => StartupPreferencesStore.PathFor(endpoint)
        .Replace(".startup.json", ".agreements.json", StringComparison.Ordinal);
    public AgreementAcceptanceRecord? Read()
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("协议确认记录过大。");
        var record = JsonSerializer.Deserialize<AgreementAcceptanceRecord>(File.ReadAllText(path));
        Validate(record);
        return record;
    }
    public void Save(AgreementAcceptanceRecord record)
    {
        Validate(record);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(record), new UTF8Encoding(false));
            // Keep invalid historical evidence when the user explicitly makes new confirmations.
            try { _ = Read(); }
            catch (Exception e) when (e is JsonException or InvalidDataException)
            { File.Copy(path, path + ".invalid-" + Guid.NewGuid().ToString("N")); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void Validate(AgreementAcceptanceRecord? record)
    {
        string[] known = ["npedutools-service", "npep-service", "npep-privacy"];
        if (record is null || record.SchemaVersion != 1 || record.Agreements is not { Length: <= 3 } entries ||
            entries.Any(a => a is null || !known.Contains(a.Id) || string.IsNullOrWhiteSpace(a.Version) || a.Version.Length > 32 ||
                !DateOnly.TryParseExact(a.EffectiveDate, "yyyy-MM-dd", out _) || a.Hash is not { Length: 64 } ||
                a.Hash.Any(c => !Uri.IsHexDigit(c)) || a.AcceptedAtUtc == default || a.AcceptedAtUtc.Offset != TimeSpan.Zero) ||
            entries.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new InvalidDataException("协议确认记录无效，原记录已保留。");
    }
}
