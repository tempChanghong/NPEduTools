using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;
namespace NPEduTools.Npep.Tests;
public sealed class SeatingProtocolTests
{
    public static IEnumerable<object[]> Cases => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"seating-cases.json")))!.AsArray().Select(v=>new object[] {v!["name"]!.GetValue<string>(),v["definition"]!.GetValue<string>(),v["value"]!.ToJsonString(),v["expected"]!.GetValue<bool>()});
    [Theory, MemberData(nameof(Cases))]
    public void Shared_KV_and_native_cases(string name,string definition,string json,bool expected)
    {
        var exception=Record.Exception(()=>NpepSeatingProtocol.Validate(definition,JsonNode.Parse(json)!.AsObject()));
        if(expected)Assert.Null(exception);else Assert.IsType<NpepException>(exception);Assert.NotEmpty(name);
    }
}
