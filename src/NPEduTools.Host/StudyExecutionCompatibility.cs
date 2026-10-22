using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;

namespace NPEduTools.Host;

// Configuration revisions also cover homework, presentation, photos and thresholds.
// A surviving execution grant applies only to the same effective capture schedule.
internal static class StudyExecutionCompatibility
{
    internal static bool SameSchedule(StudySettings? previous, StudySettings? next, string mode) =>
        previous?.Noise.Enabled == true && next?.Noise.Enabled == true &&
        JsonNode.DeepEquals(JsonSerializer.SerializeToNode(previous.ForMode(mode).Schedule, Protocol.Json),
            JsonSerializer.SerializeToNode(next.ForMode(mode).Schedule, Protocol.Json));
}
