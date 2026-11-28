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
    private readonly object _updateBindingLock = new();
    public SoftwareUpdateStatus? UpdateStatus()
    {
        if (_updates is not { } update) return null;
        string runningVersion = update.Status.RunningVersion;
        try { ObserveUpdateBinding(update, runningVersion); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { return new("UNMANAGED", null, false, runningVersion, Reason: "STATE_UNKNOWN"); }
        return update.Status;
    }
    public bool AcceptUpdateInvitation(Guid id)
    {
        if (Snapshot() is not { State: "ACTIVE", Connection: "ONLINE", Busy: false, ReportingPaused: false } || _updates?.Accept(id) != true)
            return false;
        try { _updateWake.Release(); } catch (SemaphoreFullException) { }
        return true;
    }

    private string? ObserveUpdateBinding(ManagedUpdateSession update, string runningVersion)
    {
        lock (_updateBindingLock)
        {
            var view = _device!.View();
            var stage = view.Text("state");
            bool suspended = view["suspended"]?.GetValue<bool>() == true;
            if (stage != "ACTIVE" || suspended || view["registration"] is not JsonObject registration ||
                view["origin"] is not JsonValue origin)
            {
                update.HideBinding(runningVersion, suspended || stage is "SUSPENDED" or "UNPAIRED" or "UNPAIRING");
                return null;
            }
            var identity = NpepDevice.RegistrationIdentity(registration);
            var approval = view["approval"];
            string? Label(string field) => approval?[field]?.GetValue<string>();
            string? screen = registration["screenBindingId"]?.GetValue<string>();
            string originText = origin.GetValue<string>();
            string scope = new JsonArray(originText, identity, screen, Label("approvalId")).ToJsonString();
            update.Bind(scope, Label("schoolName") ?? Label("schoolId") ?? originText,
                Label("administrativeClassName") ?? Label("administrativeClassId") ?? screen,
                Label("screenBindingName") ?? screen ?? identity.Text("deviceId"), identity.Text("deviceId"));
            return scope;
        }
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
                string? scope = null;
                try
                {
                    var state = Snapshot();
                    scope = ObserveUpdateBinding(_updates, host.RunningVersion);
                    if (scope is not null && state is { State: "ACTIVE", Connection: "ONLINE", Busy: false, ReportingPaused: false })
                    {
                        var context = await StudyContextAsync(token);
                        async Task RequireOriginalUpdateConnection()
                        {
                            await RequireStudyContextAsync(context, token);
                            if (Snapshot().Revision != state.Revision || ObserveUpdateBinding(_updates, host.RunningVersion) != scope)
                                throw new NpepException("SESSION_SUPERSEDED");
                        }
                        async Task<JsonObject> Exchange(Guid? consent, ManagedUpdateReceipt? receipt)
                        {
                            await RequireOriginalUpdateConnection();
                            var body = NpepProtocol.Body();
                            body["context"] = context.Copy(); body["sequence"] = checked(++sequence);
                            body["runningVersion"] = host.RunningVersion;
                            body["localConsent"] = consent.HasValue;
                            body["consentEnrollmentId"] = consent?.ToString("D");
                            body["receipt"] = JsonSerializer.SerializeToNode(receipt, Protocol.Json);
                            try { return await _device.UpdateExchangeAsync(body, token); }
                            // Late errors must not revoke receipts for the connection that replaced this one.
                            finally { await RequireOriginalUpdateConnection(); }
                        }
                        await _updates.CycleAsync(scope, Exchange, token);
                    }
                    else { _updates.Disconnect(expectedScope: scope); delay = TimeSpan.FromSeconds(4); }
                }
                catch (NpepException e) when (e.Code == "SOFTWARE_UPDATE_UNSUPPORTED")
                { _updates.Disconnect("SOFTWARE_UPDATE_UNSUPPORTED", scope); delay = TimeSpan.FromMinutes(2); }
                catch (NpepException e) when (e.Status is 401 or 403 || e.Code == "UPDATE_ENROLLMENT_CHANGED")
                { _updates.ForgetReceipt(scope); _updates.Disconnect(expectedScope: scope); }
                catch (Exception e) when (Handled(e) || e is InvalidOperationException or FormatException or ArgumentException)
                { _updates.Disconnect(expectedScope: scope); }
                try { await _updateWake.WaitAsync(delay, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
        });
    }
}
