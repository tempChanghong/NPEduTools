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
    [InlineData("ftp://npep.test")]
    [InlineData("http://user:password@npep.test")]
    [InlineData("http://npep.test/api")]
    [InlineData("http://npep.test/?secret=x")]
    [InlineData("https://user:password@npep.test")]
    [InlineData("https://npep.test/api")]
    [InlineData("https://npep.test/?secret=x")]
    [InlineData("https://npep.test/#fragment")]
    public void RejectsAmbiguousOrUnsupportedOrigins(string origin) => Assert.Throws<NpepException>(() => new NpepApi(origin));
    [Theory]
    [InlineData("http://localhost:3000", "http://localhost:3000")]
    [InlineData("http://127.0.0.1:3031/", "http://127.0.0.1:3031")]
    [InlineData("http://192.168.1.20:3000", "http://192.168.1.20:3000")]
    [InlineData("http://school.test", "http://school.test")]
    [InlineData("http://[::1]:3000", "http://[::1]:3000")]
    [InlineData("https://school.test/", "https://school.test")]
    public void AcceptsHttpAndHttpsWithoutHostRestrictions(string input, string expected)
    {
        using var api = new NpepApi(input);
        Assert.Equal(expected, api.Origin);
        Assert.NotEqual(NpepApi.ValidateOrigin("http://school.test"), NpepApi.ValidateOrigin("https://school.test"));
    }
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
