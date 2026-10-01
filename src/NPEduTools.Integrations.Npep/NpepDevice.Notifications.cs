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
    internal string NotificationScope()
    {
        var saved = Required();
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

    internal async Task<NpepNotificationSnapshot> FetchNotificationsAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            RequireNotificationConnection();
            using var api = Api();
            var items = new List<NpepNotice>(); var cursors = new HashSet<string>(); var ids = new HashSet<string>();
            string? cursor = null, snapshotId = null; DateTimeOffset serverTime = default; int poll = 10; long clockAt = 0;
            for (int page = 0; page < 26; page++)
            {
                var reply = await api.NotificationsAsync(DeviceBearer, cursor, null, token);
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
        catch (NpepException error) { if (error.Status is 401 or 403) SuspendIfTerminal(error); throw; }
        finally { _gate.Release(); }
    }

    internal static void ValidateNotice(NpepNotice n)
    {
        if (n.PublicationId is not { Length: > 0 and <= 191 } || n.PublicationId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ':' or '-')) ||
            n.Revision is <= 0 or > int.MaxValue || n.Title is null or { Length: > 160 } ||
            n.Content is null or { Length: > 8000 } || n.Source is null or { Length: > 120 } ||
            n.Priority is not ("MINOR" or "NORMAL" or "IMPORTANT" or "URGENT") ||
            (n.Priority != "MINOR" && !n.PopupEnabled) || n.ExpiresAt <= n.PublishAt) throw new NpepException("INVALID_RESPONSE");
    }

    internal async Task<JsonObject> SendNotificationReceiptsAsync(JsonObject body, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { RequireNotificationConnection(); using var api = Api(); return await api.NotificationsAsync(DeviceBearer, null, body, token); }
        catch (NpepException error) { if (error.Status is 401 or 403) SuspendIfTerminal(error); throw; }
        finally { _gate.Release(); }
    }
}
