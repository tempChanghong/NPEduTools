using System.Globalization;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Host;

public sealed partial class StudySettingsService
{
    private byte[]? _homeworkBytes;
    private string? _homeworkId, _homeworkDate;
    private TimeSpan? _homeworkAt;
    private string _homeworkError = "WAITING";
    private void ClearHomework(string reason = "WAITING")
    { _homeworkBytes = null; _homeworkId = _homeworkDate = null; _homeworkAt = null; _homeworkError = reason; }
    private StudyHomeworkState HomeworkState()
    {
        string? date = _state.SchoolNow?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        bool eligible = _state is { PolicyState: "APPLIED", ClockReady: true, Phase: "Active" or "Preparing" };
        if (!eligible || _settings?.Homework.Enabled != true || _homeworkDate is not null && _homeworkDate != date) ClearHomework();
        if (_homeworkAt is { } at && (Elapsed < at || Elapsed - at >= TimeSpan.FromSeconds(60))) ClearHomework("UNAVAILABLE");
        return new(!eligible ? "WAITING" : _settings?.Homework.Enabled != true ? "DISABLED" : _homeworkId is not null ? "READY" : _homeworkError,
            _homeworkId, _homeworkBytes is null ? 0 : (_homeworkBytes.Length + 16383) / 16384, date, _version);
    }
    public JsonObject? StudyHomeworkRequest()
    {
        var state = Snapshot();
        return state is { PolicyState: "APPLIED", ClockReady: true, Phase: "Preparing" or "Active", Settings.Homework.Enabled: true, SchoolNow: { } date, Version: { } version }
            ? new() { ["version"] = version, ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) } : null;
    }
    public void ApplyStudyHomework(JsonObject payload, byte[] bytes, string snapshotId)
    {
        NpepStudyProtocol.Validate("homeworkSnapshot", payload);
        if (bytes.Length is <= 0 or > 2097152 || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) != snapshotId)
            throw new NpepException("INVALID_RESPONSE");
        lock (_sync)
        {
            Tick();
            if (_state is not { PolicyState: "APPLIED", ClockReady: true, Phase: "Preparing" or "Active", Settings.Homework.Enabled: true } ||
                _version != payload.Text("version") || _state.SchoolNow?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) != payload.Text("date"))
                throw new NpepException("POLICY_CHANGED");
            _homeworkBytes = bytes.ToArray(); _homeworkId = snapshotId; _homeworkDate = payload.Text("date"); _homeworkAt = Elapsed; _homeworkError = "WAITING";
        }
    }
    public void StudyHomeworkFailed(string code) { lock (_sync) ClearHomework(code == "STUDY_HOMEWORK_UNSUPPORTED" ? "UNSUPPORTED" : "UNAVAILABLE"); }
    public HostResponse ReadHomework(HostRequest request)
    {
        lock (_sync)
        {
            Snapshot();
            if (request.StudyHomework is not { } read || read.SnapshotId != _homeworkId || _homeworkBytes is null || read.Index * 16384 >= _homeworkBytes.Length)
                return new(1, request.RequestId, "Rejected", "HOMEWORK_CHANGED", "作业已更新，请重新读取。");
            int offset = read.Index * 16384;
            return new(1, request.RequestId, "Succeeded", null, "今日作业片段。", HomeworkChunk: new(_homeworkId, read.Index,
                (_homeworkBytes.Length + 16383) / 16384, Convert.ToBase64String(_homeworkBytes, offset, Math.Min(16384, _homeworkBytes.Length - offset))));
        }
    }
}
