using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
namespace NPEduTools.Integrations.Npep;
public static class NpepPhotoProtocol
{
    private static readonly JsonDocument Schema = JsonDocument.Parse(Assembly.GetExecutingAssembly().GetManifestResourceStream("npep.photos.schema.json")!);
    public static void Validate(string name, JsonObject value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString());
        if (!NpepProtocol.Check(Schema.RootElement, Schema.RootElement.GetProperty("definitions").GetProperty(name), document.RootElement)) throw new NpepException("INVALID_RESPONSE");
        if (name == "lease" && (!DateTimeOffset.TryParse(value.Text("createdAt"), out var start) ||
            !DateTimeOffset.TryParse(value.Text("expiresAt"), out var end) || end - start != TimeSpan.FromSeconds(60))) throw new NpepException("INVALID_RESPONSE");
    }
}
public interface INpepStudyPhotos
{
    bool SupportsPhotos => true;
    void BindPhotos(string? scope);
    JsonObject? PhotoLeaseRequest();
    void ConfirmPhotoLease(JsonObject response, TimeSpan roundTrip);
    StudyPhotoPacket? PendingPhoto();
    void PhotoStored(Guid eventId);
    void PhotoFailed(string code, Guid? eventId = null);
}
public static class NpepPhotoProtection
{
    public static byte[] Protect(byte[] bytes, bool encrypt)
    {
        if (!OperatingSystem.IsWindows() || bytes.Length > 524288) throw new InvalidDataException("PHOTO_QUEUE_UNAVAILABLE");
        return NpepVault.Protect(bytes, encrypt);
    }
}
