using System.Text.Json.Serialization;

namespace NPEduTools.Contracts;

// N4.3 domain contracts. No HTTP route or automatic capture is enabled by these records.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SchoolNoiseRule([property: JsonRequired] IReadOnlyList<int> Days,
    [property: JsonRequired] string Start, [property: JsonRequired] string End);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SchoolNoisePolicy([property: JsonRequired] string Mode,
    [property: JsonRequired] IReadOnlyList<SchoolNoiseRule> Rules);
public sealed record EffectiveNoiseSchedule(string Source, IReadOnlyList<SchoolNoiseRule> Rules);
public sealed record SchoolNoiseWindow(DateTimeOffset Start, DateTimeOffset End);
public sealed record SchoolNoiseWindows(SchoolNoiseWindow? Current, SchoolNoiseWindow? Next);
public sealed record NoiseWindowBlock(SchoolNoiseWindow Window, string Kind, string? MicrophoneKey = null);
public sealed record NoiseScheduleDecision(string Action, string Reason, SchoolNoiseWindow? Window);
