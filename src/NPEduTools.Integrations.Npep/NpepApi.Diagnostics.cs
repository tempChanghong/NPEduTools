using System.Diagnostics;
using NPEduTools.Contracts;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepApi
{
    internal Action<NpepConnectionEvent>? Observation { get; set; }
    private sealed class ObserveHandler(HttpMessageHandler handler, Action<NpepConnectionEvent> observe) : DelegatingHandler(handler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            long at = Stopwatch.GetTimestamp();
            Guid? id = Guid.TryParse(request.Headers.TryGetValues("X-Request-Id", out var values) ? values.SingleOrDefault() : null, out var parsed) ? parsed : null;
            string endpoint = Endpoint(request.RequestUri?.AbsolutePath ?? "");
            void Record(string kind, string? code, int? status) {
                try { observe(new(Guid.NewGuid(), DateTimeOffset.UtcNow, kind, endpoint, code, status, id, (long)Stopwatch.GetElapsedTime(at).TotalMilliseconds, null)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            }
            try {
                var response = await base.SendAsync(request, token);
                if (!response.IsSuccessStatusCode) Record("TransportFailure", null, (int)response.StatusCode);
                else if (Stopwatch.GetElapsedTime(at).TotalMilliseconds >= 2000) Record("SlowRequest", null, (int)response.StatusCode);
                return response;
            }
            catch (OperationCanceledException) { Record("RequestCancelled", null, null); throw; }
            catch (Exception e) when (e is HttpRequestException or IOException or System.Security.Authentication.AuthenticationException)
            { Record("TransportFailure", "NETWORK_UNAVAILABLE", null); throw; }
        }
    }
    private static string Endpoint(string path)
    {
        path = path.Replace("/api/v2/npep/", "", StringComparison.Ordinal).Trim('/');
        if (path == "info") return "Info";
        if (path.StartsWith("pairings", StringComparison.Ordinal)) return "Pairing";
        if (path == "device/me") return "Registration";
        if (path == "device/sessions") return "Session";
        if (path == "device/status") return "Status";
        if (path.Contains("school-management", StringComparison.Ordinal) || path == "admission/catalog") return "SchoolManagement";
        if (path.Contains("study-settings/photos", StringComparison.Ordinal)) return "StudyPhotos";
        if (path.Contains("study-settings/seating", StringComparison.Ordinal)) return "StudySeating";
        if (path.EndsWith("study-settings/homework", StringComparison.Ordinal)) return "StudyHomework";
        if (path.EndsWith("study-settings/execution", StringComparison.Ordinal)) return "StudyExecution";
        if (path.EndsWith("study-settings/presentation", StringComparison.Ordinal)) return "StudyPresentation";
        if (path.EndsWith("study-settings/resume-exchange", StringComparison.Ordinal)) return "StudyResume";
        if (path.Contains("study-settings", StringComparison.Ordinal)) return "StudySettings";
        if (path.Contains("notification", StringComparison.Ordinal)) return "Notifications";
        if (path.Contains("runtime-", StringComparison.Ordinal)) return "RuntimeControl";
        if (path.Contains("exam-plan", StringComparison.Ordinal)) return "ExamPlans";
        return path.Contains("noise", StringComparison.Ordinal) ? "Noise" : "Unknown";
    }
}
