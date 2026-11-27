using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;

namespace NPEduTools.Integrations.Npep;

internal sealed record NpepNotificationSnapshot(DateTimeOffset ServerTime, int PollAfterSeconds, IReadOnlyList<NpepNotice> Items);

public sealed partial class NpepDevice
{
    internal string NotificationScope() => NotificationScope(Required());

    internal string? ActiveNotificationScope()
    {
        var saved = _saved;
        return !_suspended && saved?.Text("stage") == "ACTIVE" && saved["reportingPaused"]?.GetValue<bool>() != true
            ? NotificationScope(saved) : null;
    }

    private static string NotificationScope(JsonObject saved)
    {
        var registration = saved["registration"] as JsonObject ?? throw new NpepException("NOT_PAIRED");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|',
            saved.Text("origin"), registration.Text("serverInstanceId"), registration.Text("deploymentEpoch"),
            registration.Text("deviceId"), registration.Text("screenBindingId"), registration.Number("bindingRevision"),
            registration.Number("credentialGeneration")))));
    }

    private void RequireNotificationConnection()
    {
        if (_suspended || Required().Text("stage") != "ACTIVE" || Required()["reportingPaused"]?.GetValue<bool>() == true)
            throw new NpepException("DEVICE_SUSPENDED");
    }

    private async Task<T> WithNotificationConnectionAsync<T>(string? scope, CancellationToken token,
        Func<NpepApi, string, Action, Task<T>> exchange)
    {
        JsonObject pairing;
        NpepApi api;
        string bearer;
        await _gate.WaitAsync(token);
        try
        {
            RequireNotificationConnection();
            token.ThrowIfCancellationRequested();
            if (scope is not null && NotificationScope() != scope) throw new NpepException("SESSION_SUPERSEDED");
            pairing = Required();
            bearer = DeviceBearer;
            api = Api();
        }
        finally { _gate.Release(); }

        void RequireCurrent()
        {
            token.ThrowIfCancellationRequested();
            if (_suspended || !ReferenceEquals(_saved, pairing)) throw new NpepException("SESSION_SUPERSEDED");
        }

        // The captured origin and credential are fixed for this exchange. HTTP must not own the device gate.
        using (api)
        {
            T result;
            try
            {
                RequireCurrent();
                result = await exchange(api, bearer, RequireCurrent);
            }
            catch (NpepException error) when (error.Status is 401 or 403)
            {
                await _gate.WaitAsync(token);
                try
                {
                    RequireCurrent();
                    SuspendIfTerminal(error);
                }
                finally { _gate.Release(); }
                throw;
            }
            await _gate.WaitAsync(token);
            try
            {
                RequireCurrent();
                return result;
            }
            finally { _gate.Release(); }
        }
    }

    internal Task<NpepNotificationSnapshot> FetchNotificationsAsync(CancellationToken token, string? scope = null) =>
        WithNotificationConnectionAsync<NpepNotificationSnapshot>(scope, token, async (api, bearer, requireCurrent) =>
        {
            try { return await ReadNotificationsAsync(api, bearer, requireCurrent, token); }
            catch (NpepException error) when (error.Code is "SNAPSHOT_INVALIDATED" or "SNAPSHOT_EXPIRED")
            {
                // Start over once with a new cursor chain instead of showing or completing the old partial list.
                return await ReadNotificationsAsync(api, bearer, requireCurrent, token);
            }
        });

    private static async Task<NpepNotificationSnapshot> ReadNotificationsAsync(NpepApi api, string bearer, Action requireCurrent, CancellationToken token)
    {
        var items = new List<NpepNotice>(); var cursors = new HashSet<string>(); var ids = new HashSet<string>();
        string? cursor = null, snapshotId = null; DateTimeOffset serverTime = default; int poll = 10; long clockAt = 0;
        for (int page = 0; page < 26; page++)
        {
            requireCurrent();
            var reply = await api.NotificationsAsync(bearer, cursor, null, token);
            if (!Guid.TryParseExact(reply.Text("snapshotId"), "D", out _) ||
                snapshotId is not null && snapshotId != reply.Text("snapshotId")) throw new NpepException("INVALID_RESPONSE");
            snapshotId = reply.Text("snapshotId");
            if (!DateTimeOffset.TryParse(reply.Text("serverTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) throw new NpepException("INVALID_RESPONSE");
            if (page == 0) { serverTime = time; clockAt = Stopwatch.GetTimestamp(); }
            poll = (int)reply.Number("pollAfterSeconds");
            if (poll is < 1 or > 300 || reply["items"] is not JsonArray { Count: <= 20 } rows) throw new NpepException("INVALID_RESPONSE");
            foreach (var row in rows)
            {
                string[] fields = ["publicationId", "revision", "title", "content", "priority", "publishAt", "expiresAt", "source", "popupEnabled"];
                if (row is not JsonObject item || item.Count != fields.Length || fields.Any(f => !item.ContainsKey(f))) throw new NpepException("INVALID_RESPONSE");
                NpepNotice notice;
                try { notice = row!.Deserialize<NpepNotice>(Protocol.Json) ?? throw new JsonException(); }
                catch (JsonException) { throw new NpepException("INVALID_RESPONSE"); }
                ValidateNotice(notice);
                if (!ids.Add(notice.PublicationId) || notice.PublishAt > time || notice.ExpiresAt <= time) throw new NpepException("INVALID_RESPONSE");
                items.Add(notice);
                if (items.Count > 500) throw new NpepException("SNAPSHOT_LIMIT_EXCEEDED");
            }
            cursor = reply["nextCursor"]?.GetValue<string>();
            if (cursor is null) return new(serverTime + Stopwatch.GetElapsedTime(clockAt), Math.Max(10, poll), items);
            if (cursor.Length is 0 or > 200 || !cursors.Add(cursor)) throw new NpepException("INVALID_RESPONSE");
        }
        throw new NpepException("SNAPSHOT_LIMIT_EXCEEDED");
    }

    internal static void ValidateNotice(NpepNotice n)
    {
        if (n.PublicationId is not { Length: > 0 and <= 191 } || n.PublicationId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ':' or '-')) ||
            n.Revision is <= 0 or > int.MaxValue || n.Title is null or { Length: > 160 } ||
            n.Content is null or { Length: > 8000 } || n.Source is null or { Length: > 120 } ||
            n.Priority is not ("MINOR" or "NORMAL" or "IMPORTANT" or "URGENT") ||
            (n.Priority != "MINOR" && !n.PopupEnabled) || n.ExpiresAt <= n.PublishAt) throw new NpepException("INVALID_RESPONSE");
    }

    internal Task<JsonObject> SendNotificationReceiptsAsync(JsonObject body, CancellationToken token, string? scope = null) =>
        WithNotificationConnectionAsync(scope, token, (api, bearer, _) => api.NotificationsAsync(bearer, null, body, token));
}
