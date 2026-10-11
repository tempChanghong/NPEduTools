using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed class StudyProtocolTests
{
    public static JsonArray Cases => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "study-device-cases.json")))!.AsArray();
    internal static JsonObject Configuration => Cases[0]!["value"]!.DeepClone().AsObject();
    public static IEnumerable<object[]> SharedCases => Cases.Select(c => new object[] {
        c!["name"]!.GetValue<string>(), c["definition"]!.GetValue<string>(), c["expected"]!.GetValue<bool>(), c["value"]!.ToJsonString() });
    [Theory, MemberData(nameof(SharedCases))]
    public void KV_and_native_accept_the_same_wire_cases(string name, string definition, bool expected, string json)
    {
        var error = Record.Exception(() => NpepStudyProtocol.Validate(definition, JsonNode.Parse(json)!.AsObject()));
        if (expected) Assert.Null(error); else Assert.IsType<NpepException>(error);
        Assert.NotEmpty(name);
    }
    [Theory]
    [InlineData("valid", null)] [InlineData("correlation", "INVALID_RESPONSE")]
    [InlineData("oversize", "INVALID_RESPONSE")] [InlineData("redirect", "REDIRECT_REJECTED")]
    [InlineData("old server", "STUDY_UNSUPPORTED")] [InlineData("wrong version", "INVALID_RESPONSE")]
    public async Task Transport_is_bounded_versioned_and_correlated(string fault, string? expected)
    {
        using var api = new NpepApi("https://npep.test", new Handler(request => {
            Assert.EndsWith("/device/study-settings", request.RequestUri!.AbsolutePath);
            Assert.Equal("0.10", request.Headers.GetValues("X-NPEP-Version").Single());
            Assert.Equal("isolated-device-token", request.Headers.Authorization!.Parameter);
            var result = Fixtures.Reply(request, Configuration);
            var envelope = JsonNode.Parse(result.Content.ReadAsStringAsync().Result)!.AsObject();
            envelope["protocolVersion"] = fault == "wrong version" ? "0.7" : "0.10";
            if (fault == "correlation") envelope["requestId"] = NpepProtocol.Id();
            result.Content = new StringContent(fault == "oversize" ? new string('x', 65537) : envelope.ToJsonString());
            if (fault == "redirect") result.StatusCode = System.Net.HttpStatusCode.Redirect;
            if (fault == "old server") result.StatusCode = System.Net.HttpStatusCode.NotFound;
            return Task.FromResult(result);
        }));
        if (expected is null) Assert.Equal(new string('a',64), (await api.StudySettingsAsync("isolated-device-token", CancellationToken.None)).Text("version"));
        else Assert.Equal(expected, (await Assert.ThrowsAsync<NpepException>(() => api.StudySettingsAsync("isolated-device-token", CancellationToken.None))).Code);
    }
}
