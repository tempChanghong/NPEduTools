using System.IO.Pipes;
using System.Text.Json;
using NPEduTools.Contracts;

if (!OperatingSystem.IsWindows()) return 1;
if (args.Length == 0 || args.Contains("--help"))
{
    Console.WriteLine("NPEduTools.Cli <ping|status|watch|stop> [--pipe NAME] [--timeout-ms 3000] [--observe-ms 0]\nwatch streams JSON until Ctrl+C. Exit: 0 success, 2 invalid arguments, 3 Host unavailable, 4 query failed, 130 cancelled.");
    return args.Length == 0 ? 2 : 0;
}
var options = new Dictionary<string, string>();
for (int i = 1; i < args.Length; i += 2)
{
    if (i + 1 >= args.Length || args[i] is not ("--pipe" or "--timeout-ms" or "--observe-ms") ||
        !options.TryAdd(args[i], args[i + 1])) return 2;
}
if (args[0] is not ("ping" or "status" or "watch" or "stop") ||
    !int.TryParse(options.GetValueOrDefault("--timeout-ms", "3000"), out int timeout) ||
    !int.TryParse(options.GetValueOrDefault("--observe-ms", "0"), out int observe)) return 2;
string pipeName = options.GetValueOrDefault("--pipe", PipeEndpoint.DefaultName);
if (pipeName.Length is 0 or > 200 || pipeName.IndexOfAny(['/', '\\', ':']) >= 0) return 2;
string capability = args[0] switch { "ping" => "host.ping", "stop" => "host.stop", "watch" => "classisland.watch", _ => "classisland.status" };
var request = new HostRequest(Protocol.Version, Guid.NewGuid(), capability, timeout, observe);
if (Protocol.Validate(request) is { } validation)
{
    Console.Error.WriteLine(validation);
    return 2;
}
using var cancelled = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancelled.Cancel(); };
using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancelled.Token);
deadline.CancelAfter(TimeSpan.FromMilliseconds(timeout + 5000));
try
{
    if (args[0] == "watch")
    {
        await foreach (var snapshot in HostClient.WatchAsync(pipeName, cancelled.Token))
        {
            Console.WriteLine(JsonSerializer.Serialize(snapshot, Protocol.Json));
            if (snapshot.Outcome == "Rejected") return 4;
        }
        return 0;
    }
    await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await pipe.ConnectAsync(1500, deadline.Token);
    await Protocol.WriteAsync(pipe, request, deadline.Token);
    var response = await Protocol.ReadAsync<HostResponse>(pipe, deadline.Token);
    if (response.Version != Protocol.Version || response.RequestId != request.RequestId)
        throw new InvalidDataException("Response correlation or protocol mismatch.");
    Console.WriteLine(JsonSerializer.Serialize(response, new JsonSerializerOptions(Protocol.Json) { WriteIndented = true }));
    return response.Outcome == "Succeeded" ? 0 : 4;
}
catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { return 130; }
catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Host 连接或响应失败：{ex.GetType().Name}。请先启动 Host，再重试查询。");
    return 3;
}
