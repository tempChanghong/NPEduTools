using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepApi
{
    // Gateways can return HTML, empty or oversized error bodies. Preserve a
    // transient HTTP failure without accepting any of that body as protocol data.
    // Successful responses and all correlated-envelope/schema checks remain strict.
    private static async Task<JsonObject> ReadEnvelopeAsync(HttpResponseMessage response, int limit, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > limit) throw UnreadableResponse(response);
        using var buffer = new MemoryStream();
        var block = new byte[4096];
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(token);
            int read;
            while ((read = await stream.ReadAsync(block, token)) > 0)
            {
                if (buffer.Length + read > limit) throw UnreadableResponse(response);
                buffer.Write(block, 0, read);
            }
        }
        // This I/O is HTTP content, not the credential store. In particular,
        // HttpIOException from a truncated response must not latch STOPPED.
        catch (IOException error)
        {
            int status = (int)response.StatusCode;
            if (status is 401 or 403 or 429 or >= 500 and <= 599) throw UnreadableResponse(response);
            throw new HttpRequestException("NPEP response body could not be received.", error);
        }
        try { return NpepProtocol.Parse(buffer.ToArray()); }
        catch (NpepException error) when (error.Code == "INVALID_RESPONSE")
        { throw UnreadableResponse(response); }
    }

    private static NpepException UnreadableResponse(HttpResponseMessage response)
    {
        int status = (int)response.StatusCode;
        // A received authorization refusal stays terminal even if its body is lost.
        if (status is 401 or 403) return new("AUTH_INVALID", status);
        if (status is not (429 or >= 500 and <= 599)) return new("INVALID_RESPONSE");
        var header = response.Headers.RetryAfter;
        double? seconds = header?.Delta?.TotalSeconds ?? (header?.Date - DateTimeOffset.UtcNow)?.TotalSeconds;
        int? retry = seconds is { } value ? (int)Math.Clamp(Math.Ceiling(value), 1, 3600) : null;
        return new(status == 429 ? "RATE_LIMITED" : "TEMPORARILY_UNAVAILABLE", status, retry);
    }
}
