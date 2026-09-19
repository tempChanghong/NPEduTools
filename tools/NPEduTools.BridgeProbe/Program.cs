using System.Text.Json;
using ClassIsland.Shared.IPC;
using dotnetCampus.Ipc.CompilerServices.GeneratedProxies;
using NPEduTools.ClassIsland.Bridge.Contracts;

if (args.Length > 1 || args.Length == 1 && args[0] != "hello") return 2;
// Hard bound even if the third-party connect/property implementation ignores cancellation.
_ = Task.Run(async () => { await Task.Delay(8000); Environment.Exit(4); });
var stdout = Console.Out; Console.SetOut(Console.Error);
var client = new IpcClient(); using var provider = client.Provider;
try
{
    await client.Connect().WaitAsync(TimeSpan.FromSeconds(4));
    var service = provider.CreateIpcProxy<IRecordingBridgeP0>(client.PeerProxy!);
    string json = await (args.Length == 1 ? service.GetHelloAsync() : service.GetSnapshotAsync()).WaitAsync(TimeSpan.FromSeconds(3));
    if (System.Text.Encoding.UTF8.GetByteCount(json) > BridgeProtocol.MaxBytes) throw new InvalidDataException("Oversize");
    using var document = JsonDocument.Parse(json);
    if (document.RootElement.GetProperty("protocolVersion").GetInt32() != BridgeProtocol.Version) throw new InvalidDataException("VersionMismatch");
    stdout.WriteLine(json); return 0;
}
catch (Exception error)
{ stdout.WriteLine(JsonSerializer.Serialize(new { error = error.GetBaseException().GetType().Name })); return 3; }
