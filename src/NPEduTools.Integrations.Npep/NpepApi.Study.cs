using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepApi
{
    internal Task<JsonObject> StudyPresentationAsync(string bearer, JsonObject body, CancellationToken token)
    {
        NpepStudyProtocol.Validate("presentationRequest", body);
        return StudyAsync("device/study-settings/presentation", "presentationResponse", bearer, body, token, "0.13", 1);
    }
    internal Task<JsonObject> StudyHomeworkAsync(string bearer, JsonObject body, CancellationToken token)
    {
        NpepStudyProtocol.Validate("homeworkRequest", body);
        return StudyAsync("device/study-settings/homework", "homeworkChunk", bearer, body, token, "0.12");
    }
    internal Task<JsonObject> StudySettingsAsync(string bearer, CancellationToken token) => StudyAsync("device/study-settings", "configuration", bearer, null, token);
    internal Task<JsonObject> StudyReceiptAsync(string bearer, JsonObject body, CancellationToken token)
    {
        NpepStudyProtocol.Validate("receiptRequest", body);
        return StudyAsync("device/study-settings/receipt", "receiptResponse", bearer, body, token);
    }
    internal Task<JsonObject> StudyExecutionAsync(string bearer, JsonObject body, CancellationToken token)
    {
        NpepStudyProtocol.Validate("executionRequest", body);
        return StudyAsync("device/study-settings/execution", "executionResponse", bearer, body, token);
    }
    internal Task<JsonObject> StudyResumeAsync(string bearer, JsonObject body, CancellationToken token)
    {
        NpepStudyProtocol.Validate("resumeExchangeRequest", body);
        return StudyAsync("device/study-settings/resume-exchange", "resumeExchangeResponse", bearer, body, token, "0.11");
    }
    private async Task<JsonObject> StudyAsync(string path, string definition, string bearer, JsonObject? body, CancellationToken token, string version = "0.10", int deadlineSeconds = 8)
    {
        string requestId = body?.Text("requestId") ?? NpepProtocol.Id();
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-NPEP-Version", version); request.Headers.Add("X-Request-Id", requestId);
        if (body is not null)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(body.ToJsonString());
            if (bytes.Length > 65536) throw new NpepException("PAYLOAD_TOO_LARGE");
            request.Content = new ByteArrayContent(bytes); request.Content.Headers.ContentType = new("application/json");
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(deadlineSeconds));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        int status = (int)response.StatusCode;
        if (status is >= 300 and < 400) throw new NpepException("REDIRECT_REJECTED");
        if (status is 404 or 426) throw new NpepException(version == "0.14" ? "STUDY_SEATING_UNSUPPORTED" : version == "0.13" ? "STUDY_PRESENTATION_UNSUPPORTED" : version == "0.12" ? "STUDY_HOMEWORK_UNSUPPORTED" : version == "0.11" ? "STUDY_RESUME_UNSUPPORTED" : "STUDY_UNSUPPORTED", status);
        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream(); var block = new byte[4096]; int read;
        while ((read = await stream.ReadAsync(block, deadline.Token)) > 0)
        { if (buffer.Length + read > 65536) throw new NpepException("INVALID_RESPONSE"); buffer.Write(block, 0, read); }
        var envelope = NpepProtocol.Parse(buffer.ToArray());
        if (envelope.Text("protocolVersion") != version || envelope.Text("requestId") != requestId) throw new NpepException("INVALID_RESPONSE");
        if (!response.IsSuccessStatusCode)
        {
            string code = envelope["error"]?["code"]?.GetValue<string>() ?? "INVALID_RESPONSE";
            throw new NpepException(System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Z0-9_]{1,100}$") ? code : "INVALID_RESPONSE", status);
        }
        if (envelope.Count != 4 || !envelope.ContainsKey("serverTime") || envelope["data"] is not JsonObject data) throw new NpepException("INVALID_RESPONSE");
        if (version == "0.14") NpepSeatingProtocol.Validate(definition, data); else NpepStudyProtocol.Validate(definition, data); return data.Copy();
    }
}

public sealed partial class NpepDevice
{
    internal async Task<JsonObject?> StudyPresentationAsync(Func<JsonObject?> observe, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            RequireNotificationConnection();
            // Sample after waiting for the network gate, then reserve the entire 1s HTTP budget.
            // A queued old visible sample must not become a fresh server-side claim.
            if (observe() is not { } body) return null;
            var status = body["status"]!.AsObject(); int remaining = Math.Max(0, status["remainingMs"]!.GetValue<int>() - 1000);
            if (remaining == 0 && status.Text("state") is "Visible" or "Hidden")
            { status["state"] = "Unknown"; status["reason"] = "APP_STALE"; status["appInstanceId"] = null; status["pulse"] = null; }
            status["remainingMs"] = remaining;
            using var api = Api(); return await api.StudyPresentationAsync(DeviceBearer, body, token);
        }
        catch (NpepException error) { if (error.Status == 401) SuspendIfTerminal(error); throw; }
        finally { _gate.Release(); }
    }
    internal async Task<JsonObject> StudyHomeworkAsync(JsonObject body, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { RequireNotificationConnection(); using var api = Api(); return await api.StudyHomeworkAsync(DeviceBearer, body, token); }
        catch (NpepException error) { if (error.Status == 401) SuspendIfTerminal(error); throw; }
        finally { _gate.Release(); }
    }
    internal async Task<JsonObject> StudyResumeAsync(JsonObject body, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { RequireNotificationConnection(); using var api = Api(); return await api.StudyResumeAsync(DeviceBearer, body, token); }
        catch (NpepException error) { if (error.Status == 401) SuspendIfTerminal(error); throw; }
        finally { _gate.Release(); }
    }
    internal async Task<JsonObject> StudyExecutionAsync(JsonObject body, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { RequireNotificationConnection(); using var api = Api(); return await api.StudyExecutionAsync(DeviceBearer, body, token); }
        catch (NpepException error) { if (error.Status == 401) SuspendIfTerminal(error); throw; }
        finally { _gate.Release(); }
    }
    internal async Task<JsonObject> StudyAsync(JsonObject? receipt, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            RequireNotificationConnection(); using var api = Api();
            return receipt is null ? await api.StudySettingsAsync(DeviceBearer, token) : await api.StudyReceiptAsync(DeviceBearer, receipt, token);
        }
        catch (NpepException error) { if (error.Status == 401) SuspendIfTerminal(error); throw; }
        finally { _gate.Release(); }
    }
}
