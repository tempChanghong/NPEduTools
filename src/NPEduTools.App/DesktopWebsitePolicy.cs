using System.IO;
using NPEduTools.Contracts;
using NPEduTools.EdgeRestrictionPrototype;

namespace NPEduTools.App;

internal sealed class DesktopWebsitePolicy(string pipe) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, ForegroundPolicyGate> _gates = new(StringComparer.Ordinal);
    private RestrictionWarning? _warning;
    private string? _warningHost;
    private string? _warningSource;
    private Task? _work;

    internal void Start() => _work = WatchAsync(_lifetime.Token);

    private async Task WatchAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(900));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                try { await CheckAsync(token); }
                catch (Exception error) when (!token.IsCancellationRequested && error is IOException or TimeoutException or
                    UnauthorizedAccessException or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException) { }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task CheckAsync(CancellationToken token)
    {
        var response = await CallAsync("website.policy.status", null, token);
        if (response.Outcome != "Succeeded") return;
        var state = response.WebsitePolicy;
        if (_warning is { } showing)
        {
            if (state is null || state.Source != _warningSource || !state.BlockedHosts.Contains(_warningHost, StringComparer.Ordinal))
                showing.DismissForInactivePolicy();
            return;
        }
        if (state is null || state.ObservationId == Guid.Empty || state.Source is not ("GRADE" or "CLASS") ||
            state.BlockedHosts is not { Length: > 0 and <= 24 }) return;
        // UI Automation performs cross-process calls. A slow renderer must never
        // block the teacher's window or start multiple simultaneous tree reads.
        var observation = await Task.Run(() => EdgeForegroundUia.Read(state.BlockedHosts.ToHashSet(StringComparer.Ordinal)), token);
        if (observation.Proof is not { } proof || !Uri.TryCreate(proof.Url, UriKind.Absolute, out var url) ||
            observation.Window != EdgeForegroundUia.GetForegroundWindow()) return;

        var evidence = new WebsitePolicyBrowserEvidence(state.ObservationId, url.IdnHost);
        if ((await CallAsync("website.policy.observed", evidence, token)).Outcome != "Succeeded" ||
            token.IsCancellationRequested || observation.Window != EdgeForegroundUia.GetForegroundWindow()) return;
        string origin = url.GetLeftPart(UriPartial.Authority);
        if (!_gates.TryGetValue(origin, out var gate))
        {
            if (_gates.Count >= 48) _gates.Clear();
            gate = _gates[origin] = new ForegroundPolicyGate(new Uri(origin));
        }
        if (gate.HasShown(proof)) return;
        var current = await Task.Run(() => EdgeForegroundUia.Read(state.BlockedHosts.ToHashSet(StringComparer.Ordinal)), token);
        if (current.Window != observation.Window || current.Proof != proof) return;
        var confirmed = await CallAsync("website.policy.status", null, token);
        if (token.IsCancellationRequested || confirmed.Outcome != "Succeeded" || confirmed.WebsitePolicy?.ObservationId != state.ObservationId ||
            !gate.TryOpen(proof, current.Window, EdgeForegroundUia.GetForegroundWindow())) return;

        string source = state.Source == "GRADE" ? "年级网站访问规则" : "本班网站访问规则";
        _warningHost = url.IdnHost;
        _warningSource = state.Source;
        _warning = new RestrictionWarning(observation.Window, url.IdnHost, "当前生效：" + source, playAudio: true);
        _warning.Closed += (_, _) => _warning = null;
        _warning.Show();
    }

    private async Task<HostResponse> CallAsync(string capability, WebsitePolicyBrowserEvidence? evidence, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        return await HostClient.RequestAsync(pipe, new HostRequest(Protocol.Version, Guid.NewGuid(), capability,
            WebsitePolicyEvidence: evidence), deadline.Token, StartupElevation.VerifyMaintenanceHost);
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        if (_work is { } work) _ = work.ContinueWith(_ => _lifetime.Dispose(), TaskScheduler.Default);
        else _lifetime.Dispose();
    }
}
