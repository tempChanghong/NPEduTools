using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
namespace NPEduTools.Integrations.Npep;
public sealed partial class NpepApi
{
    internal Task<JsonObject> PhotoLeaseAsync(string bearer, JsonObject body, CancellationToken token)
    {
        NpepPhotoProtocol.Validate("leaseRequest", body);
        return PhotoAsync("lease", "lease", bearer, body.Text("requestId"), Encoding.UTF8.GetBytes(body.ToJsonString()), null, token);
    }
    internal Task<JsonObject> PhotoUploadAsync(string bearer, StudyPhotoPacket packet, CancellationToken token)
    {
        NpepPhotoProtocol.Validate("metadata", packet.Metadata);
        if (packet.Bytes.Length is <= 0 or > 262144) throw new NpepException("INVALID_PHOTO");
        string header = Convert.ToBase64String(Encoding.UTF8.GetBytes(packet.Metadata.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (header.Length > 4096) throw new NpepException("INVALID_PHOTO");
        return PhotoAsync("upload", "uploadReply", bearer, NpepProtocol.Id(), packet.Bytes, header, token);
    }
    private async Task<JsonObject> PhotoAsync(string path, string definition, string bearer, string id, byte[] bytes, string? header, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "device/study-settings/photos/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-NPEP-Version", "0.15"); request.Headers.Add("X-Request-Id", id);
        if (header is not null) request.Headers.Add("X-NPEP-Photo", header);
        request.Content = new ByteArrayContent(bytes); request.Content.Headers.ContentType = new(header is null ? "application/json" : "image/jpeg");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(2));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        int status = (int)response.StatusCode;
        if (status is >= 300 and < 400) throw new NpepException("REDIRECT_REJECTED");
        if (status is 404 or 426) throw new NpepException("STUDY_PHOTOS_UNSUPPORTED", status);
        var envelope = await ReadEnvelopeAsync(response, 16384, deadline.Token);
        if (envelope.Text("protocolVersion") != "0.15" || envelope.Text("requestId") != id) throw new NpepException("INVALID_RESPONSE");
        if (!response.IsSuccessStatusCode)
        {
            string code = envelope["error"]?["code"]?.GetValue<string>() ?? "INVALID_RESPONSE";
            throw new NpepException(System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Z0-9_]{1,100}$") ? code : "INVALID_RESPONSE", status);
        }
        if (envelope.Count != 4 || !envelope.ContainsKey("serverTime") || envelope["data"] is not JsonObject data) throw new NpepException("INVALID_RESPONSE");
        NpepPhotoProtocol.Validate(definition, data); return data.Copy();
    }
}
public sealed partial class NpepDevice
{
    internal async Task<JsonObject> StudyPhotoAsync(JsonObject? lease, StudyPhotoPacket? packet, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            RequireNotificationConnection(); using var api = Api();
            return lease is not null ? await api.PhotoLeaseAsync(DeviceBearer, lease, token)
                : await api.PhotoUploadAsync(DeviceBearer, packet ?? throw new InvalidDataException(), token);
        }
        catch (NpepException e) { if (e.Status == 401) SuspendIfTerminal(e); throw; }
        finally { _gate.Release(); }
    }
}
