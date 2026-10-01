using System.Text.Json.Nodes;
using System.Net.Http.Headers;
using System.Text;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

// Calls the real isolated HTTPS API. Display/close calls here probe the receipt contract;
// actual WPF rendering is independently verified by NotificationPreview, not asserted here.
internal static class NotificationAcceptance
{
    internal static async Task RunAsync(NpepDevice device, string directory, JsonObject fixture, Func<HttpMessageHandler> handler, Action<bool, string> check)
    {
        using var controls = new HttpClient(handler()) { BaseAddress = new Uri(fixture.Text("origin") + "/__fixture/"), Timeout = TimeSpan.FromSeconds(10) };
        controls.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Text("controlSecret"));
        async Task<JsonNode> Control(string path, JsonObject? body = null)
        {
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await controls.SendAsync(request); response.EnsureSuccessStatusCode();
            return JsonNode.Parse(await response.Content.ReadAsByteArrayAsync())!;
        }
        var expected = fixture["notifications"] as JsonObject ?? throw new NpepException("N2_FIXTURE_REQUIRED");
        int count = (int)expected.Number("expectedCount");
        string silentId = expected.Text("silentPublicationId");
        var snapshot = await device.FetchNotificationsAsync(default);
        check(count > 20 && snapshot.Items.Count == count, "Original N1 credential receives all N2 snapshot pages");
        var silent = snapshot.Items.Single(n => n.PublicationId == silentId);
        check(!silent.PopupEnabled && silent.Priority == "MINOR", "Real publication popupEnabled=false survives delivery");
        string scope = device.NotificationScope();
        var inbox = new NpepInbox(directory); inbox.Prepare(scope); inbox.Apply(scope, snapshot);
        var poll = inbox.Snapshot(true, new("poll"));
        check(poll.Total == count && poll.Current is { PopupEnabled: true } && poll.Current.PublicationId != silentId,
            "Complete snapshot persists and silent notice is excluded from automatic popup selection");
        check(poll.Items.Count == 10 && poll.NextOffset == 10, "Local summaries paginate within unchanged IPC limit");
        var firstBatch = inbox.Pending()!;
        var firstReply = await device.SendNotificationReceiptsAsync(firstBatch, default);
        check(Results(firstReply).All(r => r.Text("status") == "ACCEPTED"), "Durable RECEIVED events accepted by real server");
        var retry = firstBatch.Copy(); retry["requestId"] = NpepProtocol.Id();
        var duplicate = await device.SendNotificationReceiptsAsync(retry, default);
        check(Results(duplicate).All(r => r.Text("status") == "DUPLICATE"), "Stable receipt event IDs deduplicate across HTTP request IDs");
        inbox.Complete(firstBatch, duplicate);
        while (inbox.Pending() is { } batch)
        {
            var response = await device.SendNotificationReceiptsAsync(batch, default);
            check(Results(response).All(r => r.Text("status") == "ACCEPTED"), "Remaining RECEIVED receipt batch accepted");
            inbox.Complete(batch, response);
        }
        var automatic = poll.Current!;
        foreach (var item in new[] { automatic, silent })
        {
            var get = new NpepNotificationCommand("get", scope, item.PublicationId, item.Revision);
            check(inbox.Snapshot(true, get).Current == item, "Exact version available for explicit local open");
            check(inbox.Mark(get with { Action = "displayed" }) && inbox.Mark(get with { Action = "dismissed" }),
                "Display and close contract events persist for explicitly opened version");
        }
        var events = inbox.Pending()!;
        var ack = await device.SendNotificationReceiptsAsync(events, default);
        check(Results(ack).All(r => r.Text("status") == "ACCEPTED"), "Real server accepts DISPLAYED/DISMISSED including explicit silent open");
        inbox.Complete(events, ack);
        var restartedInbox = new NpepInbox(directory); restartedInbox.Prepare(scope);
        check(!restartedInbox.Snapshot(true, new("poll")).CanPresent, "Restart requires a fresh online snapshot before showing content");
        restartedInbox.Apply(scope, await device.FetchNotificationsAsync(default));
        check(restartedInbox.Pending() is null && restartedInbox.Snapshot(true, new("poll")).Current?.PublicationId != automatic.PublicationId,
            "Real refetch does not repeat closed version or regenerate received events");
        string deviceId = ((JsonObject)device.View()["registration"]!).Text("deviceId");
        var stored = ((JsonArray)await Control("receipts")).OfType<JsonObject>().Where(r => r.Text("deviceId") == deviceId).ToArray();
        check(stored.Length == count + 4 && stored.Count(r => r.Text("stage") == "RECEIVED") == count,
            "PostgreSQL stores exactly one event per received/display/close stage despite retries");
        var created = (JsonObject)await Control("notices", new() { ["title"] = "桌面隔离修订", ["content"] = "修订前", ["priority"] = "NORMAL" });
        string id = created.Text("id");
        var before = await device.FetchNotificationsAsync(default); restartedInbox.Apply(scope, before);
        var original = before.Items.Single(n => n.PublicationId == id);
        await Control("notices/" + Uri.EscapeDataString(id) + "/revise", new() { ["content"] = "修订后" });
        var after = await device.FetchNotificationsAsync(default); restartedInbox.Apply(scope, after);
        var revised = after.Items.Single(n => n.PublicationId == id);
        check(revised.Revision > original.Revision && revised.Content == "修订后" &&
            restartedInbox.Snapshot(true, new("get", scope, id, original.Revision)).Current is null,
            "Real server revision replaces old body and invalidates old local version");
        await Control("notices/" + Uri.EscapeDataString(id) + "/expire", new());
        restartedInbox.Apply(scope, await device.FetchNotificationsAsync(default));
        check(restartedInbox.Snapshot(true, new("get", scope, id, revised.Revision)).Current is null,
            "Real expiry removes cached notice from display eligibility");
        var withdrawal = (JsonObject)await Control("notices", new() { ["title"] = "桌面隔离撤回", ["priority"] = "URGENT" });
        string withdrawnId = withdrawal.Text("id");
        var visible = await device.FetchNotificationsAsync(default); restartedInbox.Apply(scope, visible);
        var withdrawing = visible.Items.Single(n => n.PublicationId == withdrawnId);
        await Control("notices/" + Uri.EscapeDataString(withdrawnId) + "/withdraw", new());
        restartedInbox.Apply(scope, await device.FetchNotificationsAsync(default));
        check(restartedInbox.Snapshot(true, new("get", scope, withdrawnId, withdrawing.Revision)).Current is null &&
            restartedInbox.Snapshot(true, new("poll")).Total == count,
            "Real withdrawal removes displayed-version lookup without clearing unrelated notices");
        // Keep only a non-secret summary for the operator; no credential or notice body is emitted.
        await File.WriteAllTextAsync(Path.Combine(directory, "notification-acceptance-summary.json"), new JsonObject
        { ["notificationCount"] = count, ["receiptContractVerified"] = true, ["actualWpfRenderingInThisHarness"] = false }.ToJsonString());
    }

    private static IEnumerable<JsonObject> Results(JsonObject response) => ((JsonArray)response["results"]!).OfType<JsonObject>();
}
