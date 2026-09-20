using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

const string appVersion = "NPEP N1 development probe";
const string usage = """
NPEP N1 隔离验收工具（不会启动 Host 或外部软件）
每个命令须显式指定 --data-dir <独立测试目录>。
info    --server https://server             查看服务端身份
pair    --server https://server --instance <UUID> --epoch <UUID> --name <设备名>
                                             核对身份后创建配对申请
resume-create                                原申请创建响应丢失后恢复
poll                                         读取管理员审批及学校/班级/大屏
confirm --approval-id <UUID>                  本机核对审批信息后明确确认
recover                                      激活响应丢失后恢复原凭据
view                                         查看本地状态（不输出 secret）
run     --seconds <1..3600> [--pipe <Host管道>] 只读上报；无管道时状态为 UNKNOWN
unpair                                       尝试远端撤销并删除本机凭据

服务端须有正常可信的 HTTPS 证书；没有跳过证书验证的选项。
pair、confirm 不自动代替本机确认。不要将配对码贴入公共日志。
""";
if (args.Length == 0 || args[0] is "--help" or "help") { Console.WriteLine(usage); return 0; }
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 1; i < args.Length; i += 2)
        if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(args[i], args[i + 1]))
            throw new NpepException("INVALID_ARGUMENTS");
    string command = args[0];
    string[] allowed = command switch
    {
        "info" => ["--server"], "pair" => ["--server", "--instance", "--epoch", "--name"],
        "confirm" => ["--approval-id"], "run" => ["--seconds", "--pipe"],
        "resume-create" or "poll" or "recover" or "view" or "unpair" => [],
        _ => throw new NpepException("INVALID_ARGUMENTS")
    };
    if (options.Keys.Any(k => k != "--data-dir" && !allowed.Contains(k))) throw new NpepException("INVALID_ARGUMENTS");
    string Require(string name) => options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new NpepException("INVALID_ARGUMENTS");
    using var device = new NpepDevice(Require("--data-dir"));
    void Show(JsonObject value) => Console.WriteLine(value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    switch (command)
    {
        case "info": Show(await device.InspectServerAsync(Require("--server"), cancellation.Token)); break;
        case "pair":
            Show(await device.BeginAsync(Require("--server"), new JsonObject
            {
                ["serverInstanceId"] = Require("--instance"), ["deploymentEpoch"] = Require("--epoch"),
                ["supportedCapabilities"] = new JsonArray("device.status")
            }, Require("--name"), appVersion, cancellation.Token)); break;
        case "resume-create": Show(await device.ResumeCreateAsync(cancellation.Token)); break;
        case "poll": Show(await device.PollApprovalAsync(cancellation.Token)); break;
        case "confirm": Show(await device.ConfirmAsync(Require("--approval-id"), cancellation.Token)); break;
        case "recover": Show(await device.RecoverConfirmationAsync(cancellation.Token)); break;
        case "view": Show(device.View()); break;
        case "unpair":
            string outcome = await device.UnpairAsync(cancellation.Token);
            Console.WriteLine(outcome);
            if (outcome == "LOCAL_ONLY")
            {
                Console.WriteLine("本机已解绑，但无法确认服务端已撤销。请由学校管理员检查并撤销该设备。");
                return 2;
            }
            break;
        case "run":
            if (!int.TryParse(Require("--seconds"), out int seconds) || seconds is < 1 or > 3600) throw new NpepException("INVALID_ARGUMENTS");
            using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
            {
                lifetime.CancelAfter(TimeSpan.FromSeconds(seconds));
                int failures = 0;
                while (!lifetime.IsCancellationRequested)
                {
                    int delay;
                    try
                    {
                        await device.OpenSessionAsync(lifetime.Token);
                        // Every attempt reads a new sample. A failed/uncertain report is never replayed.
                        var sample = await NpepStatusReader.ReadAsync(options.GetValueOrDefault("--pipe"), appVersion, lifetime.Token);
                        if (sample.AgeMs > 5000) throw new NpepException("SAMPLE_TOO_OLD");
                        var receipt = await device.ReportAsync(sample, lifetime.Token);
                        Show(receipt); failures = 0; delay = (int)receipt.Number("nextPollSeconds");
                    }
                    catch (Exception e) when (Retryable(e, lifetime.Token))
                    {
                        int backoff = Math.Min(60, 5 * (1 << Math.Min(failures++, 4)));
                        delay = Math.Max(backoff, (e as NpepException)?.RetryAfterSeconds ?? 0);
                        delay += Random.Shared.Next(0, Math.Max(1, delay / 5 + 1));
                        Console.WriteLine($"上报暂不可用；{delay} 秒后重新采样。错误类别：{(e is NpepException n ? n.Code : "NETWORK_UNAVAILABLE")}");
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                    try { await Task.Delay(TimeSpan.FromSeconds(delay), lifetime.Token); }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                }
            }
            break;
    }
    return 0;
}
catch (NpepException e) { Console.Error.WriteLine($"NPEP：{e.Code}（HTTP {e.Status}）"); return 1; }
catch (OperationCanceledException) { Console.Error.WriteLine("操作已取消或超时；请查看本地状态，必要时使用恢复命令。"); return 2; }
catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or System.Security.Cryptography.CryptographicException)
{ Console.Error.WriteLine("无法完成网络或凭据存储操作；请检查连接、当前 Windows 用户及测试目录占用情况。已有凭据未自动重建。"); return 1; }

static bool Retryable(Exception e, CancellationToken token) => !token.IsCancellationRequested &&
    (e is HttpRequestException or OperationCanceledException || e is NpepException n && (n.Status == 429 || n.Status >= 500));
