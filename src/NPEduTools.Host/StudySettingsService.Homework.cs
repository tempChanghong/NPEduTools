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
    // This callback reads only the manual owner's phase and Windows clock. It must
    // stay lock-free: manual display sampling already calls this service under its lock.
    public Func<DateTimeOffset?>? ManualHomeworkNow { get; set; }
    private (bool Eligible, string? Date, bool Manual) HomeworkContext()
    {
        if (ManualHomeworkNow?.Invoke() is { Year: >= 2000 and <= 9998 } manualNow)
            return (_scope is not null && ConfigurationFresh && _state.PolicyState == "APPLIED",
                manualNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), true);
        return (_state is { PolicyState: "APPLIED", ClockReady: true, Phase: "Active" or "Preparing" },
            _state.SchoolNow?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), false);
    }
    private void ClearHomework(string reason = "WAITING")
    { _homeworkBytes = null; _homeworkId = _homeworkDate = null; _homeworkAt = null; _homeworkError = reason; }
    private StudyHomeworkState HomeworkState()
    {
        var (eligible, date, _) = HomeworkContext();
        if (!eligible || _settings?.Homework.Enabled != true || _homeworkDate is not null && _homeworkDate != date) ClearHomework();
        if (!ContinuingOffline && _homeworkAt is { } at && (Elapsed < at || Elapsed - at >= TimeSpan.FromSeconds(60))) ClearHomework("UNAVAILABLE");
        return new(!eligible ? "WAITING" : _settings?.Homework.Enabled != true ? "DISABLED" : _homeworkId is not null ? "READY" : _homeworkError,
            _homeworkId, _homeworkBytes is null ? 0 : (_homeworkBytes.Length + 16383) / 16384, date, _version);
    }
    public JsonObject? StudyHomeworkRequest()
    {
        Snapshot();
        lock (_sync)
        {
            var context = HomeworkContext();
            if (!context.Eligible || context.Date is null || _settings?.Homework.Enabled != true || _version is null) return null;
            var request = new JsonObject { ["version"] = _version, ["date"] = context.Date };
            if (context.Manual) request["purpose"] = "Manual";
            return request;
        }
    }
    public void ApplyStudyHomework(JsonObject payload, byte[] bytes, string snapshotId)
    {
        NpepStudyProtocol.Validate("homeworkSnapshot", payload);
        if (bytes.Length is <= 0 or > 2097152 || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) != snapshotId)
            throw new NpepException("INVALID_RESPONSE");
        lock (_sync)
        {
            Tick();
            var context = HomeworkContext();
            if (!context.Eligible || _settings?.Homework.Enabled != true ||
                _version != payload.Text("version") || context.Date != payload.Text("date"))
                throw new NpepException("POLICY_CHANGED");
            _homeworkBytes = bytes.ToArray(); _homeworkId = snapshotId; _homeworkDate = payload.Text("date"); _homeworkAt = Elapsed; _homeworkError = "WAITING";
        }
    }
    public void StudyHomeworkFailed(string code)
    {
        lock (_sync)
        {
            Tick();
            if (ContinuingOffline && code is "HOMEWORK_OFFLINE" or "TEMPORARILY_UNAVAILABLE" or "RATE_LIMITED") return;
            ClearHomework(code == "STUDY_HOMEWORK_UNSUPPORTED" ? "UNSUPPORTED" : "UNAVAILABLE");
        }
    }
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
