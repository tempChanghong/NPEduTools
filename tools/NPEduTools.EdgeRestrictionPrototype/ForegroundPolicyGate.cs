namespace NPEduTools.EdgeRestrictionPrototype;

internal sealed record BrowserObservation(string TargetId, string LoaderId, string Url, string CommittedUrl, string BrowserFailure,
    bool MainDocument, bool ActiveTab);

// Only accepts browser-owned evidence for the committed document. Page text,
// typed addresses, HTTP error pages and subresource failures are insufficient.
internal sealed class ForegroundPolicyGate(Uri deniedUrl)
{
    private const int RememberedNavigations = 256;
    private readonly HashSet<(string Target, string Loader)> _shown = [];
    private readonly Queue<(string Target, string Loader)> _shownOrder = new();

    internal bool TryOpen(BrowserObservation observation, nint browserWindow, nint foregroundWindow)
    {
        if (browserWindow == 0 || browserWindow != foregroundWindow || !observation.ActiveTab || !observation.MainDocument ||
            observation.BrowserFailure != "net::ERR_BLOCKED_BY_ADMINISTRATOR" ||
            observation.CommittedUrl != "chrome-error://chromewebdata/" ||
            string.IsNullOrWhiteSpace(observation.TargetId) || string.IsNullOrWhiteSpace(observation.LoaderId) ||
            !Uri.TryCreate(observation.Url, UriKind.Absolute, out var attempted) || !MatchesWatchTarget(attempted, deniedUrl))
            return false;
        var navigation = (observation.TargetId, observation.LoaderId);
        if (!_shown.Add(navigation)) return false;
        _shownOrder.Enqueue(navigation);
        if (_shownOrder.Count > RememberedNavigations)
            _shown.Remove(_shownOrder.Dequeue());
        return true;
    }

    internal static bool IsSiteScope(Uri target) => target.PathAndQuery == "/" && target.Fragment.Length == 0;

    internal static bool MatchesWatchTarget(Uri attempted, Uri target) =>
        attempted.UserInfo.Length == 0 && (IsSiteScope(target)
            ? attempted.Scheme == target.Scheme && attempted.Port == target.Port &&
                string.Equals(attempted.IdnHost, target.IdnHost, StringComparison.OrdinalIgnoreCase)
            : attempted == target && attempted.Fragment == target.Fragment);
}
