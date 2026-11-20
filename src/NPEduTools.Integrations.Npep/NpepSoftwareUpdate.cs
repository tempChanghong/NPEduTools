using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;

namespace NPEduTools.Integrations.Npep;

public sealed record ManagedRelease(string Version, string Url, string Sha256, long SizeBytes);
public sealed record ManagedUpdatePlan(Guid Id, string TargetVersion, string State, ManagedRelease Release, ManagedRelease PreviousRelease);
public sealed record ManagedUpdateReceipt(Guid PlanId, string State, string? Reason);

public interface IPreparedSchoolUpdate : IAsyncDisposable
{
    Task ApplyAsync(CancellationToken token);
}
public interface ISchoolUpdateHost
{
    string RunningVersion { get; }
    string? Deferral();
    ManagedUpdateReceipt? Result(Guid planId);
    Task<IPreparedSchoolUpdate> PrepareAsync(ManagedUpdatePlan plan, CancellationToken token);
    Task<bool> HandoffAsync(IPreparedSchoolUpdate worker, Func<Task<bool>> authorize, CancellationToken token);
}

public sealed partial class NpepApi
{
    internal async Task<JsonObject> SoftwareUpdateAsync(string bearer, JsonObject body, CancellationToken token)
    {
        string requestId = body.Text("requestId");
        using var request = new HttpRequestMessage(HttpMethod.Post, "device/software-update/exchange");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-NPEP-Version", "0.18");
        request.Headers.Add("X-Request-Id", requestId);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        int status = (int)response.StatusCode;
        if (status is 404 or 426) throw new NpepException("SOFTWARE_UPDATE_UNSUPPORTED", status);
        if (status is >= 300 and < 400) throw new NpepException("REDIRECT_REJECTED", status);
        var envelope = await ReadEnvelopeAsync(response, 16384, deadline.Token, request);
        if (envelope.Text("protocolVersion") != "0.18" || envelope.Text("requestId") != requestId)
            throw new NpepException("INVALID_RESPONSE");
        if (!response.IsSuccessStatusCode)
            throw new NpepException(envelope["error"]?["code"]?.GetValue<string>() == "UPDATE_ENROLLMENT_CHANGED" && status == 409
                ? "UPDATE_ENROLLMENT_CHANGED" : "UPDATE_EXCHANGE_REJECTED", status);
        return envelope["data"]?.AsObject() ?? throw new NpepException("INVALID_RESPONSE");
    }
}

public sealed partial class NpepDevice
{
    internal async Task<JsonObject> UpdateExchangeAsync(JsonObject body, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        NpepApi api;
        string bearer;
        try { RequireNotificationConnection(); api = Api(); bearer = DeviceBearer; }
        finally { _gate.Release(); }
        using (api) return await api.SoftwareUpdateAsync(bearer, body, token);
    }
}

public sealed partial class NpepRuntime
{
    private Task _softwareUpdateLoop = Task.CompletedTask;
    private ManagedUpdateSession? _updates;
    private readonly SemaphoreSlim _updateWake = new(0, 1);
    public SoftwareUpdateStatus? UpdateStatus() => _updates?.Status;
    public bool AcceptUpdateInvitation(Guid id)
    {
        if (Snapshot() is not { State: "ACTIVE", Connection: "ONLINE", Busy: false, ReportingPaused: false } || _updates?.Accept(id) != true)
            return false;
        try { _updateWake.Release(); } catch (SemaphoreFullException) { }
        return true;
    }

    private void InitializeUpdates(ISchoolUpdateHost? host)
    {
        if (host is null || _device is null) return;
        _updates = new(host, _device.DataDirectory);
        _softwareUpdateLoop = Task.Run(async () =>
        {
            var token = _lifetime.Token;
            long sequence = 0;
            while (!token.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(20);
                try
                {
                    var state = Snapshot();
                    if (state is { State: "ACTIVE", Connection: "ONLINE", Busy: false, ReportingPaused: false })
                    {
                        var context = await StudyContextAsync(token);
                        var registration = _device.View()["registration"]?.AsObject();
                        string? binding = registration?["screenBindingId"]?.GetValue<string>();
                        string scope = new JsonArray(state.Origin, context["identity"]!.DeepClone(),
                            binding, state.Approval?["approvalId"]?.GetValue<string>()).ToJsonString();
                        var identity = context["identity"]!.AsObject();
                        string? Label(string field) => state.Approval?[field]?.GetValue<string>();
                        _updates.Bind(scope, Label("schoolName") ?? Label("schoolId") ?? state.Origin,
                            Label("administrativeClassName") ?? Label("administrativeClassId") ?? binding,
                            Label("screenBindingName") ?? binding ?? identity.Text("deviceId"));
                        async Task<JsonObject> Exchange(Guid? consent, ManagedUpdateReceipt? receipt)
                        {
                            await RequireStudyContextAsync(context, token);
                            var body = NpepProtocol.Body();
                            body["context"] = context.Copy(); body["sequence"] = checked(++sequence);
                            body["runningVersion"] = host.RunningVersion;
                            body["localConsent"] = consent.HasValue;
                            body["consentEnrollmentId"] = consent?.ToString("D");
                            body["receipt"] = JsonSerializer.SerializeToNode(receipt, Protocol.Json);
                            var result = await _device.UpdateExchangeAsync(body, token);
                            await RequireStudyContextAsync(context, token);
                            return result;
                        }
                        await _updates.CycleAsync(scope, Exchange, token);
                    }
                    else { _updates.Disconnect(); delay = TimeSpan.FromSeconds(4); }
                }
                catch (NpepException e) when (e.Code == "SOFTWARE_UPDATE_UNSUPPORTED")
                { _updates.Disconnect("SOFTWARE_UPDATE_UNSUPPORTED"); delay = TimeSpan.FromMinutes(2); }
                catch (Exception e) when (Handled(e) || e is InvalidOperationException or FormatException or ArgumentException)
                { _updates.Disconnect(); }
                try { await _updateWake.WaitAsync(delay, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
        });
    }
}
