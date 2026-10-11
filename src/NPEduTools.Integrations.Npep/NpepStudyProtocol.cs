using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Integrations.Npep;

public static class NpepStudyProtocol
{
    private static readonly JsonDocument Schema = JsonDocument.Parse(Assembly.GetExecutingAssembly().GetManifestResourceStream("npep.study.schema.json")!);
    public static void Validate(string definition, JsonObject value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString());
        if (!NpepProtocol.Check(Schema.RootElement, Schema.RootElement.GetProperty("definitions").GetProperty(definition), document.RootElement, utf16: true, multiline: definition == "homeworkSnapshot"))
            throw new NpepException("INVALID_RESPONSE");
        if (definition == "presentationRequest")
        {
            var s = value["status"]!.AsObject(); string phase = s.Text("phase"); string state = s.Text("state");
            if ((s["appInstanceId"] is null) != (s["pulse"] is null) ||
                state is "Visible" or "Hidden" && (!s["clockReady"]!.GetValue<bool>() || s["appInstanceId"] is null ||
                    s["remainingMs"]!.GetValue<int>() <= 0 || phase is not ("Preparing" or "Active" or "Ended")) ||
                state == "NotRequired" && phase is not ("Disabled" or "Outside") || state == "Blocked" && phase != "Blocked")
                throw new NpepException("INVALID_RESPONSE");
            if (phase == "Ended")
            {
                if (!s["clockReady"]!.GetValue<bool>() || s["window"] is not JsonObject w || s["schoolNow"] is null ||
                    !Calendar(s.Text("schoolNow")) || !Calendar(w.Text("end"))) throw new NpepException("INVALID_RESPONSE");
                var now = DateTime.ParseExact(s.Text("schoolNow"), "yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
                var end = DateTime.ParseExact(w.Text("end"), "yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
                if (now < end || (now - end).TotalSeconds >= 5) throw new NpepException("INVALID_RESPONSE");
            }
            Validate("receiptRequest", new JsonObject { ["requestId"] = value["requestId"]!.DeepClone(), ["context"] = value["context"]!.DeepClone(),
                ["sequence"] = value["sequence"]!.DeepClone(), ["status"] = new JsonObject {
                    ["version"] = value["version"]!.DeepClone(), ["policyState"] = "APPLIED", ["presentationState"] = "NOT_INTEGRATED", ["captureState"] = "NOT_INTEGRATED",
                    ["phase"] = phase == "Ended" ? "Outside" : phase, ["reason"] = s["reason"]!.DeepClone(), ["clockReady"] = s["clockReady"]!.DeepClone(),
                    ["schoolNow"] = s["schoolNow"]?.DeepClone(), ["window"] = s["window"]?.DeepClone() } });
        }
        if (definition is "homeworkRequest" or "homeworkSnapshot")
        {
            if (!DateOnly.TryParseExact(value.Text("date"), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var day) || day.Year is < 2000 or > 9998) throw new NpepException("INVALID_RESPONSE");
            if (definition == "homeworkRequest" && ((value["index"]!.GetValue<int>() == 0) != (value["snapshotId"] is null))) throw new NpepException("INVALID_RESPONSE");
            if (definition == "homeworkSnapshot" && value["items"]!.AsArray().Select(i => i!["id"]!.GetValue<string>()).Distinct().Count() != value["items"]!.AsArray().Count)
                throw new NpepException("INVALID_RESPONSE");
        }
        if (definition == "homeworkChunk" && value["index"]!.GetValue<int>() >= value["count"]!.GetValue<int>()) throw new NpepException("INVALID_RESPONSE");
        if (definition == "homeworkSnapshot")
        {
            try { StudyHomeworkContract.Validate(value.Deserialize<StudyHomeworkSnapshot>(Protocol.Json)!); }
            catch (InvalidDataException) { throw new NpepException("INVALID_RESPONSE"); }
        }
        if (definition is "resumeRequest" or "resumeCommand" or "resumeExchangeResponse")
        {
            var window = definition == "resumeExchangeResponse" ? value["command"]?["window"] as JsonObject : value["window"] as JsonObject;
            if (window is not null)
            {
                if (!Calendar(window.Text("start")) || !Calendar(window.Text("end"))) throw new NpepException("INVALID_RESPONSE");
                var start = DateTime.ParseExact(window.Text("start"), "yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
                var end = DateTime.ParseExact(window.Text("end"), "yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
                if (end <= start || (end - start).TotalMinutes > 180) throw new NpepException("INVALID_RESPONSE");
            }
        }
        if (definition == "configuration")
        {
            var effective = value["effective"]!.AsObject();
            var source = effective.Text("source");
            if ((source is "Grade" or "Class") != (effective["settings"] is JsonObject)) throw new NpepException("INVALID_RESPONSE");
            if (effective["settings"] is JsonObject settings && SchoolStudySchedule.Validate(settings.Deserialize<StudySettings>(Protocol.Json)!) is not null)
                throw new NpepException("INVALID_RESPONSE");
        }
        if (definition == "executionRequest")
        {
            var s = value["status"]!.AsObject();
            bool schedule = s.Text("owner") == "Schedule";
            if (schedule != (s["sessionId"] is not null)) throw new NpepException("INVALID_RESPONSE");
            bool fresh = s["signalFresh"]!.GetValue<bool>();
            if (fresh && (!schedule || s.Text("state") != "Active" || s.Text("reason") != "WINDOW_ACTIVE" ||
                !s["clockReady"]!.GetValue<bool>() || s["window"] is null || s["schoolNow"] is null)) throw new NpepException("INVALID_RESPONSE");
            Validate("receiptRequest", new JsonObject { ["requestId"] = value["requestId"]!.DeepClone(), ["context"] = value["context"]!.DeepClone(),
                ["sequence"] = value["sequence"]!.DeepClone(), ["status"] = new JsonObject {
                    ["version"] = value["version"]!.DeepClone(), ["policyState"] = "APPLIED", ["presentationState"] = "NOT_INTEGRATED",
                    ["captureState"] = "NOT_INTEGRATED", ["phase"] = fresh ? "Active" : "Blocked", ["reason"] = s["reason"]!.DeepClone(),
                    ["clockReady"] = s["clockReady"]!.DeepClone(), ["schoolNow"] = s["schoolNow"]?.DeepClone(), ["window"] = s["window"]?.DeepClone() } });
        }
        if (definition == "receiptRequest")
        {
            var status = value["status"]!.AsObject();
            if (status["schoolNow"] is JsonValue time && !Calendar(time.GetValue<string>())) throw new NpepException("INVALID_RESPONSE");
            if (status["window"] is JsonObject window && (!Calendar(window.Text("start")) || !Calendar(window.Text("end")) ||
                string.CompareOrdinal(window.Text("start"), window.Text("end")) >= 0)) throw new NpepException("INVALID_RESPONSE");
            if (status.Text("phase") is "Active" or "Preparing")
            {
                if (!status["clockReady"]!.GetValue<bool>() || status["schoolNow"] is null || status["window"] is not JsonObject w)
                    throw new NpepException("INVALID_RESPONSE");
                var now = DateTime.ParseExact(status.Text("schoolNow"), "yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
                var start = DateTime.ParseExact(w.Text("start"), "yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
                var end = DateTime.ParseExact(w.Text("end"), "yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
                if (status.Text("phase") == "Active" ? now < start || now >= end : now >= start || (start - now).TotalSeconds > StudyPresentationTiming.ScheduledDisplayLeadSeconds)
                    throw new NpepException("INVALID_RESPONSE");
            }
        }
    }
    private static bool Calendar(string value) => DateTime.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None, out var date) && date.Year is >= 2000 and <= 9998;
}

public interface INpepStudySettings
{
    void BindStudy(string? scope);
    void ApplyStudy(JsonObject configuration);
    JsonObject? ObserveStudy();
    void StudyFailed(string code);
    bool SupportsExecution => false;
    bool SupportsResume => false;
    bool SupportsPresentation => false;
    JsonObject? ObserveStudyPresentation() => null;
    JsonObject? StudySeatingRequest() => null;
    JsonObject? ObserveSeating() => null;
    JsonObject? ObserveSeatingDownload() => null;
    bool HasSeating(string version, string hash, int revision) => false;
    void ApplySeating(JsonObject manifest, byte[]? bytes) { }
    void SeatingFailed(string code) { }
    JsonObject? StudyHomeworkRequest() => null;
    void ApplyStudyHomework(JsonObject payload, byte[] bytes, string snapshotId) { }
    void StudyHomeworkFailed(string code) { }
    JsonObject? ObserveStudyResumeReceipt() => null;
    void ApplyStudyResume(JsonObject configuration, JsonObject command, TimeSpan roundTrip = default) { }
    JsonObject? ObserveStudyExecution() => null;
    void ConfirmStudyExecution(JsonObject configuration, JsonObject response, TimeSpan roundTrip = default) { }
}

public interface INpepStudyCapture
{
    bool SupportsResume => false;
    JsonObject? ObserveStudyResumeReceipt() => null;
    void ApplyStudyResume(JsonObject configuration, JsonObject command, TimeSpan roundTrip = default) { }
    void BindStudyCapture(string? scope);
    void StudyUnavailable(string code);
    void SelectStudy(JsonObject configuration, string scope);
    void InvalidateStudy();
    JsonObject? ObserveStudyExecution();
    void ConfirmStudyExecution(JsonObject configuration, JsonObject response, TimeSpan roundTrip = default);
}
