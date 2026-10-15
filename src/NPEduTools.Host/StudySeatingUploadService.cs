using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Integrations.Npep;
namespace NPEduTools.Host;

public sealed class StudySeatingUploadService(string pipe, Func<StudyHostState> policy,
    Func<string, CancellationToken, Task<NpepSeatingUploadScope>> prepare,
    Func<NpepSeatingUploadScope, Guid, int, byte[], CancellationToken, Task<JsonObject>> publish, TimeProvider? time = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private sealed class Ticket(NpepSeatingUploadScope scope, long created)
    {
        public NpepSeatingUploadScope Scope = scope;
        public long Created = created;
        public StudySeatingUploadCommand? Command;
        public StudySeatingUploadState? Saved;
    }
    private readonly Dictionary<Guid, Ticket> _tickets = [];
    private StudySeatingUploadState State(Guid id, JsonObject value, string state) => new(id, value.Text("version"), value.Text("classId"),
        value.Text("className"), value["revision"]!.GetValue<int>(), value["hash"]?.GetValue<string>(), state);
    public async Task<HostResponse> HandleAsync(HostRequest request, CancellationToken token)
    {
        bool acquired = false, publicationAttempted = false;
        try
        {
            // Waiting for another upload is part of this request's deadline too.
            // Reply explicitly if it expires before any publication can begin.
            await _gate.WaitAsync(token); acquired = true;
            foreach (var id in _tickets.Where(p => _time.GetElapsedTime(p.Value.Created) >= TimeSpan.FromMinutes(10)).Select(p => p.Key).ToArray()) _tickets.Remove(id);
            var current = policy();
            if (current is not { PolicyState: "APPLIED", Version: { } version, Settings: not null }) throw new NpepException("STUDY_NOT_APPLIED");
            StudySeatingUploadState result;
            if (request.Capability == "study.seating.upload.status")
            {
                var scope = await prepare(version, token);
                if (_tickets.Count >= 8) _tickets.Remove(_tickets.MinBy(p => p.Value.Created).Key);
                var id = Guid.NewGuid(); _tickets[id] = new(scope, _time.GetTimestamp()); result = State(id, scope.State, "Ready");
            }
            else
            {
                var command = request.SeatingUpload ?? throw new InvalidDataException();
                if (!_tickets.TryGetValue(command.TicketId, out var ticket)) throw new NpepException("UPLOAD_EXPIRED");
                if (version != command.Version || ticket.Scope.State.Text("version") != version) throw new NpepException("POLICY_CHANGED");
                if (command.ExpectedRevision != ticket.Scope.State["revision"]!.GetValue<int>()) throw new NpepException("SEATING_CHANGED");
                if (ticket.Command is not null && ticket.Command != command) throw new NpepException("IDEMPOTENCY_CONFLICT");
                if (ticket.Saved is { } saved) result = saved;
                else
                {
                    var png = StudySeatingTransfer.Read(pipe, command.UploadId, command.ByteCount, command.Hash);
                    if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}) ||
                        System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)) != command.Width ||
                        System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)) != command.Height) throw new InvalidDataException();
                    ticket.Command = command;
                    publicationAttempted = true;
                    var value = await publish(ticket.Scope, command.UploadId, command.ExpectedRevision, png, token);
                    NpepSeatingProtocol.Validate("uploadResult", value);
                    if (value.Text("version") != version || value.Text("classId") != ticket.Scope.State.Text("classId") ||
                        value["revision"]!.GetValue<int>() != command.ExpectedRevision + 1 || value["hash"]?.GetValue<string>() != command.Hash)
                        throw new NpepException("INVALID_ENVELOPE");
                    result = ticket.Saved = State(command.TicketId, value, "Saved");
                }
            }
            return new(1, request.RequestId, "Succeeded", null, result.State == "Saved" ? "自习座位表已保存到服务器。" : "已读取本班当前版本。", SeatingUpload: result);
        }
        catch (Exception e) when (e is NpepException or HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            // HTTP/request deadlines need an explicit reply. Once publish began,
            // KV may have committed; retain the ticket and original upload ID for retry.
            return new(1, request.RequestId, "Rejected", e is NpepException n ? n.Code : publicationAttempted ? "UPLOAD_PENDING" : e is HttpRequestException or OperationCanceledException ? "UPLOAD_UNAVAILABLE" : "INVALID_IMAGE", "未确认上传成功，请保留草稿并核对当前版本。");
        }
        finally { if (acquired) _gate.Release(); }
    }
}
