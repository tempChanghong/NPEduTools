using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Diagnostics;
using NPEduTools.Contracts;

namespace NPEduTools.Integrations.Npep;

internal sealed class ManagedUpdateSession(ISchoolUpdateHost host, string directory)
{
    private readonly object _sync = new();
    private readonly string _path = Path.Combine(directory, "software-update-consent.json");
    private readonly string _grantPath = Path.Combine(directory, "software-update-handoff.json");
    private sealed record Consent(string Scope, Guid EnrollmentId);
    private sealed record Grant(string Scope, Guid PlanId, bool ApplySent, bool Unconfirmed = false);
    private string? _scope;
    private bool _online;
    private long _observedAt;
    private SoftwareUpdateStatus _status = new("UNMANAGED", null, false, host.RunningVersion);
    public SoftwareUpdateStatus Status { get { lock (_sync) return _status; } }
    internal void Bind(string scope, string? school, string? classroom, string? device)
    {
        lock (_sync)
        {
            if (scope != _scope)
            {
                if (File.Exists(_path) && ConsentId(scope) is null) File.Delete(_path);
                _scope = scope; _status = new("UNMANAGED", null, false, host.RunningVersion); _online = false;
            }
            _status = _status with { SchoolName = school, ClassName = classroom, DeviceName = device };
        }
    }
    public void Disconnect(string reason = "STATE_UNKNOWN")
    {
        lock (_sync)
        {
            _online = false;
            _status = _status with { Reason = reason };
        }
    }
    public void Unpair()
    {
        lock (_sync)
        {
            _online = false; _scope = null;
            _status = new("UNMANAGED", null, false, host.RunningVersion);
            try { File.Delete(_path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { _status = _status with { Reason = "CONSENT_STORE_UNAVAILABLE" }; }
        }
    }
    private Guid? ConsentId(string scope)
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length > 8192) return null;
        var saved = JsonSerializer.Deserialize<Consent>(File.ReadAllText(_path));
        return saved?.Scope == scope ? saved.EnrollmentId : null;
    }
    public bool Accept(Guid id)
    {
        lock (_sync)
        {
            if (!_online || Stopwatch.GetElapsedTime(_observedAt) > TimeSpan.FromSeconds(45) ||
                _scope is null || _status.Enrollment != "INVITED" || _status.EnrollmentId != id) return false;
            Directory.CreateDirectory(directory);
            Save(_path, new Consent(_scope, id));
            _status = _status with { LocalConsent = true };
            return true;
        }
    }
    private static void Save<T>(string path, T value)
    {
        using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, value); file.Flush(true); }
        File.Move(path + ".tmp", path, true);
    }
    private sealed class Dispatch(IPreparedSchoolUpdate worker, Action<bool> mark) : IPreparedSchoolUpdate
    {
        public async Task ApplyAsync(CancellationToken token)
        {
            mark(false);
            try { await worker.ApplyAsync(token); }
            catch { mark(true); throw; }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static bool Canonical(string value) => Version.TryParse(value, out var version) &&
        version.Revision >= 0 && version.Major <= 65535 && version.Minor <= 65535 && version.Build <= 65535 &&
        version.Revision <= 65535 && version.ToString(4) == value;
    private static ManagedRelease Release(JsonObject plan, string field)
    {
        var r = plan[field]?.Deserialize<ManagedRelease>(Protocol.Json) ?? throw new NpepException("INVALID_RESPONSE");
        if (r.Version is null || !Canonical(r.Version) || r.Sha256 is not { Length: 64 } ||
            !r.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            r.SizeBytes is <= 0 or > 4L * 1024 * 1024 * 1024 || r.Url is not { Length: > 0 and <= 2048 } ||
            !Uri.TryCreate(r.Url, UriKind.Absolute, out var url) || url.Scheme != "https" ||
            url.UserInfo.Length != 0 || url.Fragment.Length != 0)
            throw new NpepException("INVALID_RESPONSE");
        return r;
    }
    private static ManagedUpdatePlan ParsePlan(JsonObject p)
    {
        if (!Guid.TryParseExact(p.Text("id"), "D", out var id) || id == Guid.Empty) throw new NpepException("INVALID_RESPONSE");
        var target = Release(p, "release");
        if (target.Version != p.Text("targetVersion")) throw new NpepException("INVALID_RESPONSE");
        return new(id, target.Version, p.Text("state"), target, Release(p, "previousRelease"));
    }
    internal static string SafeReason(string? reason) => reason is { Length: <= 64 } &&
        Regex.IsMatch(reason, @"\A[A-Z][A-Z0-9_]*\z", RegexOptions.CultureInvariant) ? reason : "UPDATE_FAILED";

    internal async Task CycleAsync(string scope, Func<Guid?, ManagedUpdateReceipt?, Task<JsonObject>> exchange, CancellationToken token)
    {
        Guid? consent;
        lock (_sync) consent = ConsentId(scope);
        JsonObject snapshot;
        try { snapshot = await exchange(consent, null); }
        catch (NpepException e) when (e.Code == "UPDATE_ENROLLMENT_CHANGED" && consent.HasValue)
        {
            lock (_sync) { if (ConsentId(scope) == consent) File.Delete(_path); }
            consent = null;
            snapshot = await exchange(null, null);
        }
        string enrollment = snapshot.Text("enrollment");
        Guid? invited = EnrollmentId(snapshot);
        if (snapshot["managed"] is not JsonValue managedValue || !managedValue.TryGetValue<bool>(out bool managed))
            throw new NpepException("INVALID_RESPONSE");
        if (enrollment is not ("UNMANAGED" or "INVITED" or "MANAGED") || invited == Guid.Empty ||
            (invited is null) != (enrollment == "UNMANAGED") || managed != (enrollment == "MANAGED"))
            throw new NpepException("INVALID_RESPONSE");
        ManagedUpdatePlan? plan = snapshot["plan"] is JsonObject p ? ParsePlan(p) : null;
        lock (_sync)
        {
            if (_scope != scope) throw new NpepException("SESSION_SUPERSEDED");
            _online = true; _observedAt = Stopwatch.GetTimestamp();
            _status = _status with { Enrollment = enrollment, EnrollmentId = invited, LocalConsent = consent.HasValue && consent == invited,
                State = plan?.State, Reason = (snapshot["plan"] as JsonObject)?["reason"]?.GetValue<string>(),
                TargetVersion = plan?.TargetVersion, PlanId = plan?.Id };
        }
        if (!managed || consent is null || consent != invited || plan is null) return;
        if (plan.State is "INSTALLING" or "AWAITING_RESTART")
        {
            if (host.Result(plan.Id) is { } result) await exchange(consent, result);
            else if (File.Exists(_grantPath) && new FileInfo(_grantPath).Length <= 8192 &&
                JsonSerializer.Deserialize<Grant>(File.ReadAllText(_grantPath)) is {} pending &&
                pending.Scope == scope && pending.PlanId == plan.Id && (!pending.ApplySent || pending.Unconfirmed))
                await exchange(consent, new(plan.Id, "FAILED", pending.Unconfirmed ? "HANDOFF_UNCONFIRMED" : "HANDOFF_NOT_STARTED"));
            return;
        }
        if (plan.State is not ("APPROVED" or "DEFERRED")) return;
        if (plan.PreviousRelease.Version != host.RunningVersion || new Version(plan.TargetVersion) <= new Version(host.RunningVersion))
        { await exchange(consent, new(plan.Id, "FAILED", "VERSION_MISMATCH")); return; }
        if (host.Deferral() is { } wait) { await exchange(consent, new(plan.Id, "DEFERRED", wait)); return; }

        bool requested = false, granted = false;
        async Task<bool> Authorize()
        {
            Save(_grantPath, new Grant(scope, plan.Id, false));
            requested = true;
            var ack = await exchange(consent, new(plan.Id, "INSTALLING", null));
            if (ack["managed"]?.GetValue<bool>() != true || EnrollmentId(ack) != invited ||
                ack["plan"] is not JsonObject accepted) return false;
            var confirmed = ParsePlan(accepted);
            granted = confirmed == (plan with { State = "INSTALLING" });
            return granted;
        }
        try
        {
            await using var prepared = await PrepareWhileAuthorizedAsync(plan, consent.Value, exchange, token);
            if (prepared is null) return;
            if (host.Deferral() is { } busy) { await exchange(consent, new(plan.Id, "DEFERRED", busy)); return; }
            var dispatch = new Dispatch(prepared, uncertain => Save(_grantPath, new Grant(scope, plan.Id, true, uncertain)));
            if (!await host.HandoffAsync(dispatch, Authorize, token) && (!requested || granted))
                await exchange(consent, new(plan.Id, granted ? "FAILED" : "DEFERRED", host.Deferral() ?? "STATE_UNKNOWN"));
        }
        catch (SchoolUpdateException e) { await exchange(consent, new(plan.Id, e.Deferred ? "DEFERRED" : "FAILED", SafeReason(e.Message))); }
    }
    private async Task<IPreparedSchoolUpdate?> PrepareWhileAuthorizedAsync(ManagedUpdatePlan plan, Guid consent,
        Func<Guid?, ManagedUpdateReceipt?, Task<JsonObject>> exchange, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var preparing = host.PrepareAsync(plan, lifetime.Token);
        bool returned = false;
        try
        {
            while (!preparing.IsCompleted)
            {
                var tick = Task.Delay(TimeSpan.FromSeconds(8), lifetime.Token);
                if (await Task.WhenAny(preparing, tick) == preparing) break;
                token.ThrowIfCancellationRequested();
                var reply = await exchange(consent, null);
                if (reply["managed"]?.GetValue<bool>() != true || EnrollmentId(reply) != consent ||
                    reply["plan"] is not JsonObject current || ParsePlan(current) != plan) return null;
            }
            var ready = await preparing; returned = true; return ready;
        }
        finally
        {
            lifetime.Cancel();
            if (!returned)
            {
                try { var unused = await preparing; await unused.DisposeAsync(); }
                catch (Exception e) when (e is OperationCanceledException or IOException or SchoolUpdateException) { }
            }
        }
    }
    private static Guid? EnrollmentId(JsonObject message) => message["enrollmentId"] is { } node
        ? Guid.TryParseExact(node.GetValue<string>(), "D", out var value) && value != Guid.Empty ? value : throw new NpepException("INVALID_RESPONSE")
        : null;
}

public sealed class SchoolUpdateException(string reason, bool deferred = false) : Exception(reason)
{
    public bool Deferred { get; } = deferred;
}
