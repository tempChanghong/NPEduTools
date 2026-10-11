using System.Globalization;
using System.Text.Json;

namespace NPEduTools.Contracts;

public static class StudyHomeworkContract
{
    public static StudyHomeworkSnapshot Parse(byte[] bytes)
    {
        if (bytes.Length is <= 0 or > 2097152) throw new InvalidDataException("Homework exceeds the transport limit.");
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 16 });
        RejectDuplicates(document.RootElement);
        var snapshot = JsonSerializer.Deserialize<StudyHomeworkSnapshot>(bytes, Protocol.Json) ?? throw new InvalidDataException();
        Validate(snapshot); return snapshot;
    }
    public static void Validate(StudyHomeworkSnapshot snapshot)
    {
        static bool Text(string? s, int max, bool multiline = false) => s is not null && s.Length <= max &&
            !s.Any(c => char.IsControl(c) && !(multiline && c is '\r' or '\n' or '\t'));
        if (snapshot.Version is not { Length: 64 } || !snapshot.Version.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            !DateOnly.TryParseExact(snapshot.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date.Year is < 2000 or > 9998 ||
            snapshot.Items is null || snapshot.Items.Count > 500 || snapshot.Items.Any(item => item is null) || snapshot.Items.Select(i => i.Id).Distinct().Count() != snapshot.Items.Count ||
            snapshot.Items.Any(item => !Text(item.Id, 191) || item.Id.Length == 0 || item.Revision is < 1 or > 9007199254740991 ||
                !Text(item.Subject, 191) || !Text(item.Title, 191, true) || !Text(item.Content, 2097152, true) || item.Sources is null ||
                item.Sources.Count is < 1 or > 1000 || item.Sources.Any(source => source is null || !Text(source.Id, 191) || !Text(source.Name, 191)))) throw new InvalidDataException("Invalid homework snapshot.");
    }
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!seen.Add(property.Name)) throw new InvalidDataException("Duplicate homework field."); RejectDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
}
