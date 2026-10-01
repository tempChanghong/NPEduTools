using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;

namespace NPEduTools.Integrations.Npep;

internal sealed record NoticeReceipt(string EventId, string PublicationId, long Revision, string Stage, DateTimeOffset OccurredAt);
internal sealed record NoticeProgress(bool Displayed = false, bool Dismissed = false);
internal sealed class InboxData
{
    public string Scope { get; set; } = "";
    public List<NpepNotice> Items { get; set; } = [];
    public Dictionary<string, NoticeProgress> Progress { get; set; } = [];
    public List<NoticeReceipt> Outbox { get; set; } = [];
    public string? ReceiptWarning { get; set; }
}

// Host credential lease also owns this file. Every mutation commits before becoming visible to WPF.
internal sealed class NpepInbox
{
    private readonly object _sync = new();
    private readonly string _path;
    private InboxData _data;
    private DateTimeOffset _serverTime;
    private long _clockAt;
    private bool _fresh;
    private string _connection = "WAITING", _message = "等待首次通知同步。";
    internal NpepInbox(string directory)
    {
        _path = Path.Combine(directory, "npep.notifications.dpapi");
        _data = new();
        if (!File.Exists(_path)) return;
        if (new FileInfo(_path).Length > 16 * 1024 * 1024) throw new IOException("Notification store exceeds limit.");
        byte[] clear = NpepVault.Protect(File.ReadAllBytes(_path), false);
        try { _data = JsonSerializer.Deserialize<InboxData>(clear, Protocol.Json) ?? throw new JsonException(); }
        finally { CryptographicOperations.ZeroMemory(clear); }
        if (_data.Scope is not { Length: 64 } || _data.Items is null || _data.Progress is null || _data.Outbox is null ||
            _data.Items.Count > 500 || _data.Progress.Count > 10000 || _data.Outbox.Count > 5000) throw new JsonException();
        foreach (var item in _data.Items)
        {
            NpepDevice.ValidateNotice(item);
            if (!_data.Progress.ContainsKey(Key(item.PublicationId, item.Revision))) throw new JsonException();
        }
    }
    private static string Key(string id, long revision) => id + ":" + revision;
    private InboxData Copy() => JsonSerializer.Deserialize<InboxData>(JsonSerializer.SerializeToUtf8Bytes(_data, Protocol.Json), Protocol.Json)!;
    private void Commit(InboxData next)
    {
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(next, Protocol.Json), encrypted;
        try
        {
            if (clear.Length > 16 * 1024 * 1024 || next.Progress.Count > 10000 || next.Outbox.Count > 5000) throw new IOException("Notification store full.");
            encrypted = NpepVault.Protect(clear, true);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(encrypted); file.Flush(true); }
            File.Move(temporary, _path, true); _data = next;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal void Prepare(string scope)
    {
        lock (_sync)
        {
            if (_data.Scope == scope) return;
            Commit(new() { Scope = scope }); _clockAt = 0; _fresh = false;
        }
    }
    internal void Apply(string scope, NpepNotificationSnapshot snapshot)
    {
        lock (_sync)
        {
            if (_data.Scope != scope || snapshot.Items.Count > 500 || snapshot.Items.Select(n => n.PublicationId).Distinct().Count() != snapshot.Items.Count)
                throw new NpepException("INVALID_RESPONSE");
            var next = Copy(); next.Items = snapshot.Items.ToList();
            foreach (var notice in next.Items)
            {
                NpepDevice.ValidateNotice(notice);
                if (next.Progress.TryAdd(Key(notice.PublicationId, notice.Revision), new()))
                    next.Outbox.Add(new(NpepProtocol.Id(), notice.PublicationId, notice.Revision, "RECEIVED", snapshot.ServerTime));
            }
            // Unchanged polling snapshots only refresh the clock, not the encrypted file.
            if (!_data.Items.SequenceEqual(next.Items) || _data.Progress.Count != next.Progress.Count) Commit(next);
            _serverTime = snapshot.ServerTime; _clockAt = Stopwatch.GetTimestamp(); _fresh = true;
            _connection = "ONLINE"; _message = "通知已同步；关闭弹窗的次要通知可从列表主动查看。";
        }
    }
    internal void Status(string connection, string message)
    { lock (_sync) { _connection = connection; _message = message; _fresh = false; } }
    internal void PendingReceiptMessage()
    { lock (_sync) _message = "通知已同步，回执暂未上传，将自动重试。"; }
    private DateTimeOffset Now => _clockAt == 0 ? DateTimeOffset.MaxValue : _serverTime + Stopwatch.GetElapsedTime(_clockAt);
    private bool Valid(NpepNotice item) => _clockAt != 0 && item.PublishAt <= Now && (item.ExpiresAt is null || item.ExpiresAt > Now);
    internal NpepInboxState Snapshot(bool allowed, NpepNotificationCommand command)
    {
        lock (_sync)
        {
            if (!allowed) return new(null, "DISABLED", "互联未启用、已暂停或授权已停用。", 0, []);
            var live = _data.Items.Where(Valid).OrderByDescending(x => Rank(x.Priority)).ThenBy(x => x.PublishAt).ThenBy(x => x.PublicationId, StringComparer.Ordinal).ToList();
            bool fresh = _fresh && Stopwatch.GetElapsedTime(_clockAt).TotalSeconds < 60;
            NpepNotice? current = command.Action == "get"
                ? command.Scope == _data.Scope ? live.FirstOrDefault(n => n.PublicationId == command.PublicationId && n.Revision == command.Revision) : null
                : fresh ? live.FirstOrDefault(n => n.PopupEnabled && !_data.Progress[Key(n.PublicationId, n.Revision)].Dismissed) : null;
            var list = live.Skip(command.Offset).Take(10).Select(n => new NpepNoticeSummary(n.PublicationId, n.Revision,
                n.Title[..Math.Min(n.Title.Length, 64)], n.Priority, n.PopupEnabled, _data.Progress[Key(n.PublicationId, n.Revision)].Dismissed)).ToArray();
            return new(_data.Scope, fresh ? "ONLINE" : _connection == "ONLINE" ? "STALE" : _connection,
                _message + (_data.ReceiptWarning is null ? "" : " 回执提示：" + _data.ReceiptWarning), live.Count, list,
                command.Offset + list.Length < live.Count ? command.Offset + list.Length : null, current, fresh);
        }
    }
    private static int Rank(string priority) => priority switch { "URGENT" => 3, "IMPORTANT" => 2, "NORMAL" => 1, _ => 0 };
    internal bool Mark(NpepNotificationCommand command)
    {
        lock (_sync)
        {
            if (command.Scope != _data.Scope) return false;
            var notice = _data.Items.FirstOrDefault(n => n.PublicationId == command.PublicationId && n.Revision == command.Revision);
            if (notice is null || !Valid(notice)) return false;
            var key = Key(notice.PublicationId, notice.Revision); var progress = _data.Progress[key];
            string stage = command.Action == "displayed" ? "DISPLAYED" : "DISMISSED";
            if (stage == "DISPLAYED" ? progress.Displayed : progress.Dismissed) return true;
            // A close must refer to a version which actually reached the local display path.
            if (stage == "DISMISSED" && !progress.Displayed) return false;
            var next = Copy(); next.Progress[key] = stage == "DISPLAYED" ? progress with { Displayed = true } : progress with { Dismissed = true };
            next.Outbox.Add(new(NpepProtocol.Id(), notice.PublicationId, notice.Revision, stage, Now));
            Commit(next); return true;
        }
    }
    internal JsonObject? Pending()
    {
        lock (_sync)
        {
            if (_data.Outbox.Count == 0) return null;
            return new() { ["requestId"] = NpepProtocol.Id(), ["events"] = JsonSerializer.SerializeToNode(_data.Outbox.Take(20).ToArray(), Protocol.Json) };
        }
    }
    internal void Complete(JsonObject sent, JsonObject reply)
    {
        lock (_sync)
        {
            var expected = ((JsonArray)sent["events"]!).Select(x => x!["eventId"]!.GetValue<string>()).ToHashSet();
            if (reply["results"] is not JsonArray results || results.Count != expected.Count) throw new NpepException("INVALID_RESPONSE");
            var next = Copy(); var completed = new HashSet<string>();
            foreach (var node in results)
            {
                var row = node as JsonObject ?? throw new NpepException("INVALID_RESPONSE");
                string id = row.Text("eventId"), status = row.Text("status");
                if (!expected.Contains(id) || !completed.Add(id) || status is not ("ACCEPTED" or "DUPLICATE" or "REJECTED")) throw new NpepException("INVALID_RESPONSE");
                if (status == "REJECTED")
                {
                    string reason = row["code"]?.GetValue<string>() ?? "REJECTED";
                    if (reason.Length > 120) throw new NpepException("INVALID_RESPONSE");
                    next.ReceiptWarning = reason;
                }
            }
            next.Outbox.RemoveAll(e => completed.Contains(e.EventId)); Commit(next);
        }
    }
}
