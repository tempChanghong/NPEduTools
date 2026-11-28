using System.IO;
using System.Text.Json.Nodes;
using NPEduTools.App;
using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed class SchoolAccessPresetTests
{
    private static readonly SchoolAccessPreset Draft = new("https://school.example", "联验学校", "SCH01");

    [Fact]
    public async Task OobeDraftWaitsForHostAndDoesNotSendPairingCommands()
    {
        var requests = new List<HostRequest>();
        NpepState? state = null;
        var session = new NpepConnectionSession((request, _) =>
        {
            requests.Add(request);
            if (state is null) throw new IOException();
            return Task.FromResult(new HostResponse(Protocol.Version, request.RequestId, "Succeeded", null, "", Npep: state));
        });
        session.OfferSchoolAccessPreset(Draft);
        Assert.False(await session.RefreshAsync());
        Assert.Equal("", session.Origin);
        state = new NpepState(5, "UNPAIRED", "OFFLINE", "");
        Assert.True(await session.RefreshAsync());
        Assert.Equal(Draft.BackendUrl, session.Origin);
        Assert.Contains(Draft.SchoolName, session.SchoolAccessPresetLabel);
        Assert.Contains(Draft.SchoolCode!, session.SchoolAccessPresetLabel);
        Assert.False(session.ServerConfirmed);
        Assert.False(session.BindingConfirmed);
        Assert.False(await session.PairAsync());
        Assert.All(requests, request => Assert.Equal("npep.status", request.Capability));

        state = state with
        {
            Origin = Draft.BackendUrl, Server = new JsonObject
            {
                ["serverInstanceId"] = Guid.NewGuid().ToString("D"),
                ["deploymentEpoch"] = Guid.NewGuid().ToString("D")
            },
            Catalogue = new JsonObject { ["supportsLookup"] = true, ["items"] = new JsonArray() }
        };
        Assert.True(await session.InspectAsync());
        Assert.Equal("inspect", requests.Last().Npep!.Action);
        Assert.Equal("", session.SchoolQuery);
        session.ServerConfirmed = true;
        Assert.Equal(Draft.SchoolCode, session.SchoolQuery);
        Assert.False(session.CanFindClass);
        Assert.False(session.CanConfirm);
        session.Origin = "https://other.example";
        Assert.Empty(session.SchoolAccessPresetLabel);
        Assert.Empty(session.SchoolQuery);
        Assert.False(session.BindingConfirmed);
    }

    [Theory]
    [InlineData("ACTIVE")]
    [InlineData("PENDING")]
    [InlineData("APPROVED")]
    [InlineData("SUSPENDED")]
    [InlineData("UNPAIRED")]
    public async Task SavedOriginAndExistingPairingRemainAuthoritative(string stage)
    {
        const string existing = "https://existing.example";
        var session = new NpepConnectionSession((request, _) => Task.FromResult(new HostResponse(
            Protocol.Version, request.RequestId, "Succeeded", null, "", Npep: new(6, stage, "OFFLINE", "", Origin: existing))));
        session.OfferSchoolAccessPreset(Draft);
        await session.RefreshAsync();
        Assert.Equal(existing, session.Origin);
        Assert.Empty(session.SchoolAccessPresetLabel);
        Assert.False(session.ServerConfirmed);
        Assert.False(session.BindingConfirmed);
    }

    [Fact]
    public async Task LocalEditMadeWhileAwaitingHostWinsOverPreset()
    {
        var reply = new TaskCompletionSource<NpepState>();
        var session = new NpepConnectionSession(async (request, _) => new HostResponse(
            Protocol.Version, request.RequestId, "Succeeded", null, "", Npep: await reply.Task));
        session.OfferSchoolAccessPreset(Draft);
        var read = session.RefreshAsync();
        session.Origin = "https://typed.example";
        reply.SetResult(new(5, "UNPAIRED", "OFFLINE", ""));
        await read;
        Assert.Equal("https://typed.example", session.Origin);
        Assert.Empty(session.SchoolAccessPresetLabel);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"pin\":\"1234\"")]
    [InlineData(",\"backendUrl\":\"https://other.example\"")]
    [InlineData(",\"schoolCode\":42")]
    public void DraftReaderAcceptsOnlyTheHandoffFields(string extra)
    {
        string file = Path.Combine(Path.GetTempPath(), $"np-school-access-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(file, "{\"schemaVersion\":1,\"backendUrl\":\"https://school.example\",\"schoolName\":\"联验学校\"" + extra + "}");
            var draft = SchoolAccessPreset.Read(file);
            if (extra.Length == 0) Assert.Equal(Draft with { SchoolCode = null }, draft);
            else Assert.Null(draft);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(",\"schoolCode\":\"SCH01\"", true)]
    [InlineData(",\"schoolCode\":[\"SCH01\"]", false)]
    [InlineData(",\"pin\":\"1234\"", false)]
    [InlineData(",\"backendUrl\":\"https://second.example\"", false)]
    public void InstallerEnvelopeUsesTheExactSamePresetContract(string extra, bool valid)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"backendUrl\":\"https://school.example\",\"schoolName\":\"联验学校\"" + extra + "}");
        string value = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var preset = SchoolAccessPreset.ReadEnvelope(value);
        Assert.Equal(valid, preset is not null);
        if (valid) Assert.Equal(Draft.BackendUrl, preset!.BackendUrl);
        Assert.Null(SchoolAccessPreset.ReadEnvelope(value + " /DIR=other"));
        Assert.Null(SchoolAccessPreset.ReadEnvelope(new string('A', 10924)));
    }
}
