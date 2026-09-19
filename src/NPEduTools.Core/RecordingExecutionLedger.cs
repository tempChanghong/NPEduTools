using System.Text.Json;
using NPEduTools.Contracts;

namespace NPEduTools.Core;

public sealed record RecordingLedgerDocument(int Version, RecordingExecution[] Entries, DateOnly? SkipDate = null);

/// <summary>Single Host writer. Flush + atomic replacement commits each start intent before any recorder command.</summary>
public sealed class RecordingExecutionLedger(string path)
{
    public RecordingLedgerDocument Document { get; private set; } = new(1, []);
    public void Load()
    {
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("执行账本过大，自动录制已停用。");
        var doc = JsonSerializer.Deserialize<RecordingLedgerDocument>(File.ReadAllBytes(path));
        if (doc is not { Version: 1, Entries.Length: <= 4096 } || doc.Entries.Any(e => e is null || string.IsNullOrEmpty(e.Key) ||
            e.Key.Length > 256 || e.Subject is null or { Length: > 256 } || e.End <= e.Start || e.SessionId == Guid.Empty ||
            e.Start.Offset != TimeSpan.Zero || e.End.Offset != TimeSpan.Zero || DateOnly.FromDateTime(e.Start.Date) != e.Date ||
            DateOnly.FromDateTime(e.End.Date) != e.Date || (e.Fixed ? e.ProfileId != Guid.Empty : e.ProfileId == Guid.Empty) ||
            e.Reason is null or { Length: > 2000 } || e.OutputFile is { Length: > 2048 } || e.RecoveryDirectory is { Length: > 2048 } ||
            e.Phase is not ("Starting" or "Recording" or "Paused" or "Finalizing" or "Recorded" or "Skipped" or "Missed" or "Conflict" or "Failed" or "Interrupted")) ||
            doc.Entries.Select(e => e.SessionId).Distinct().Count() != doc.Entries.Length || doc.Entries.Select(e => e.Key).Distinct().Count() != doc.Entries.Length)
            throw new InvalidDataException("执行账本损坏或版本未知，原文件已保留。");
        Document = doc;
        var recovered = doc.Entries.Select(e => e.Phase is "Starting" or "Recording" or "Paused" or "Finalizing"
            ? e with { Phase = "Interrupted", Reason = "后台上次未确认完成；原会话由租约结束。保留片段，不自动重试。" } : e).ToArray();
        if (!recovered.SequenceEqual(doc.Entries)) Commit(doc with { Entries = recovered });
    }
    public bool Contains(Guid profile, PlannedRecording p) => Document.Entries.Any(e => p.Fixed ? e.Key == p.Key :
        !e.Fixed && e.ProfileId == profile && e.Start < p.Lesson.End && p.Lesson.Start < e.End);
    public RecordingExecution Add(Guid profile, DateOnly date, PlannedRecording p, string phase, string reason)
    {
        if (Contains(profile, p)) throw new InvalidDataException("本次课程已处理。");
        if (Document.Entries.Length >= 4096) throw new InvalidDataException("执行账本已达到保留上限；请先归档，不能丢弃旧标记后重复录制。");
        var entry = new RecordingExecution(p.Key, p.Fixed ? Guid.Empty : profile, date, p.Fixed, p.Lesson.Start, p.Lesson.End,
            p.Lesson.Subject, Guid.NewGuid(), phase, reason);
        Commit(Document with { Entries = Document.Entries.Append(entry).ToArray() });
        return entry;
    }
    public void Update(Guid session, string phase, string? reason = null, RecordingState? state = null)
    {
        var old = Document.Entries.Single(e => e.SessionId == session);
        var next = old with { Phase = phase, Reason = reason ?? old.Reason, OutputFile = state?.OutputFile ?? old.OutputFile,
            RecoveryDirectory = state?.RecoveryDirectory ?? old.RecoveryDirectory };
        if (old == next) return;
        Commit(Document with { Entries = Document.Entries.Select(e => e.SessionId == session ? next : e).ToArray() });
    }
    public void SkipDate(DateOnly? date) => Commit(Document with { SkipDate = date });
    private void Commit(RecordingLedgerDocument next)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next);
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("执行账本超过大小限制。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { file.Write(bytes); file.Flush(true); }
        File.Move(path + ".tmp", path, true);
        Document = next;
    }
}
