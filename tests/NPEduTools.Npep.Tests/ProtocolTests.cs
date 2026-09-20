using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed class ProtocolTests
{
    public static IEnumerable<object[]> Examples => Fixtures.Cases.Select(c => new object[] { c!["name"]!.GetValue<string>(), c["definition"]!.GetValue<string>(), c["valid"]!.GetValue<bool>(), c["value"]!.ToJsonString() });
    [Theory, MemberData(nameof(Examples))]
    public void MatchesReviewedWireExamples(string name, string definition, bool valid, string json)
    {
        var value = NpepProtocol.Parse(Encoding.UTF8.GetBytes(json));
        var error = Record.Exception(() => NpepProtocol.Validate(definition, value));
        if (valid) Assert.Null(error); else Assert.IsType<NpepException>(error);
        Assert.NotEmpty(name);
    }
    [Theory]
    [InlineData("http://npep.test")]
    [InlineData("https://user:password@npep.test")]
    [InlineData("https://npep.test/api")]
    [InlineData("https://npep.test/?secret=x")]
    [InlineData("https://npep.test/#fragment")]
    public void RejectsAmbiguousOrInsecureOrigins(string origin) => Assert.Throws<NpepException>(() => new NpepApi(origin));
    [Fact]
    public void RejectsDuplicateKeysBeforeDeserializing() => Assert.Throws<NpepException>(() => NpepProtocol.Parse("{\"data\":{\"mode\":\"DAILY\",\"mode\":\"EXAM\"}}"u8));
    [Theory]
    [InlineData("redirect")]
    [InlineData("oversized")]
    [InlineData("wrong-id")]
    [InlineData("wrong-type")]
    public async Task RejectsUntrustedResponses(string fault)
    {
        using var api = new NpepApi("https://npep.test", new Handler(request =>
        {
            if (fault == "redirect") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect));
            if (fault == "oversized") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 65537)) });
            var response = Fixtures.Value("infoResponse");
            response["requestId"] = fault == "wrong-type" ? JsonValue.Create(12) : JsonValue.Create(NpepProtocol.Id());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString()) });
        }));
        await Assert.ThrowsAsync<NpepException>(() => api.SendAsync("info", "infoResponse"));
    }
    [Fact]
    public async Task DeniesOutOfScopeEndpointBeforeSending()
    {
        using var api = new NpepApi("https://npep.test", new Handler(_ => throw new InvalidOperationException()));
        await Assert.ThrowsAsync<NpepException>(() => api.SendAsync("device/commands", "infoResponse"));
    }
}
