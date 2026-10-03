using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace NPEduTools.Integrations.SecRandom;

public enum SecRandomAction { Probe, ShowRollCall, ShowFloat, HideFloat, QuickDraw }
public sealed record SecRandomWinner(string Id, string Name, string Gender);
public sealed record SecRandomReply(bool Succeeded, string Code, string Message, bool Uncertain = false, SecRandomWinner? Winner = null);

/// <summary>V3's line protocol, without loading Avalonia or SecRandom.Core into our Host.</summary>
public sealed class SecRandomIpcClient
{
    public const string PipeName = "SecRandom_IPC_SecRandom_3F2A1B0E";
    public const int MaxResponseBytes = 64 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Action<NamedPipeClientStream, string> _verify;
    private readonly string _pipeName;

    public SecRandomIpcClient() : this(PipeName, WindowsSecRandomTarget.VerifyPeer) { }
    internal SecRandomIpcClient(string pipeName, Action<NamedPipeClientStream, string> verify)
    { _pipeName = pipeName; _verify = verify; }

    public static string Route(SecRandomAction action) => action switch
    {
        // Deliberately unsupported data route: exercises the router without querying a real list or mutating UI.
        SecRandomAction.Probe => "data/npedutools_probe?name=connectivity",
        SecRandomAction.ShowRollCall => "window/main?action=show&page=roll",
        SecRandomAction.ShowFloat => "window/float?action=show",
        SecRandomAction.HideFloat => "window/float?action=hide",
        SecRandomAction.QuickDraw => "roll_call/quick_draw",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    public async Task<SecRandomReply> SendAsync(string executable, SecRandomAction action, CancellationToken token)
    {
        bool sent = false;
        try
        {
            // CurrentUserOnly on the client compares token owners, which can differ across UAC.
            // Keep the upstream server ACL; verify its PID, SID, session and image before sending bytes.
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(3000, token);
            _verify(pipe, executable);
            sent = true; // A partially written mutation is already uncertain.
            return await ExchangeAsync(pipe, action, token);
        }
        catch (OperationCanceledException) { return Failure("timeout", sent, action); }
        catch (TimeoutException) { return Failure("pipe_unavailable", sent, action); }
        catch (UnauthorizedAccessException) { return Failure("access_denied", sent, action); }
        catch (InvalidOperationException) { return Failure("peer_mismatch", sent, action); }
        catch (ArgumentException) { return Failure("peer_unverifiable", sent, action); } // Server PID exited during verification.
        catch (System.ComponentModel.Win32Exception) { return Failure("peer_unverifiable", sent, action); }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or DecoderFallbackException)
        { return Failure("invalid_response", sent, action); }
    }

    private static SecRandomReply Failure(string code, bool sent, SecRandomAction action) =>
        new(false, code, Message(code), sent && action != SecRandomAction.Probe);

    internal static async Task<SecRandomReply> ExchangeAsync(Stream stream, SecRandomAction action, CancellationToken token)
    {
        var frame = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, type = "url", payload = new { url = Route(action) } });
        await stream.WriteAsync(frame, token);
        await stream.WriteAsync(new byte[] { 10 }, token);
        await stream.FlushAsync(token);
        return Decode(await ReadLineAsync(stream, token), action);
    }

    internal static async Task<byte[]> ReadLineAsync(Stream stream, CancellationToken token)
    {
        // Byte reads prevent buffering an unbounded response before checking its size.
        using var body = new MemoryStream();
        byte[] next = new byte[1];
        while (body.Length <= MaxResponseBytes)
        {
            if (await stream.ReadAsync(next, token) == 0) throw new InvalidDataException("Incomplete IPC response.");
            if (next[0] == 10) return body.ToArray();
            body.WriteByte(next[0]);
        }
        throw new InvalidDataException("IPC response too large.");
    }

    internal static SecRandomReply Decode(byte[] bytes, SecRandomAction action)
    {
        using var document = JsonDocument.Parse(Utf8.GetString(bytes), new JsonDocumentOptions { MaxDepth = 12 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out var success) ||
            success.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || Get(root, "type") != "url")
            throw new InvalidDataException("Invalid IPC envelope.");
        if (!success.GetBoolean())
        {
            if (!root.TryGetProperty("error", out var error) || string.IsNullOrWhiteSpace(Get(error, "code")))
                throw new InvalidDataException("Missing transport error.");
            string code = Get(error, "code")!;
            ValidateCode(code);
            return new(false, code, Message(code), code == "internal_error" && action != SecRandomAction.Probe);
        }
        if (!root.TryGetProperty("result", out var result)) throw new InvalidDataException("Missing result.");
        string? status = Get(result, "status"), businessCode = Get(result, "code");
        if (businessCode is not null) ValidateCode(businessCode);
        if (action == SecRandomAction.Probe && status == "error" && businessCode == "invalid_command")
            return new(true, "ready", "SecRandom V3 协议接口已就绪。");
        if (status == "error" && !string.IsNullOrWhiteSpace(businessCode))
            return new(false, businessCode, Message(businessCode));
        if (status != "success") throw new InvalidDataException("Unknown business result.");
        // The probe must hit the expected router, not just any server returning success.
        if (action == SecRandomAction.Probe) throw new InvalidDataException("Unexpected probe response.");
        SecRandomWinner? winner = null;
        if (action == SecRandomAction.QuickDraw)
        {
            if (!result.TryGetProperty("data", out var data) || Get(data, "id") is not { Length: > 0 and <= 256 } id ||
                Get(data, "name") is not { Length: > 0 and <= 256 } name || Get(data, "gender") is not { Length: <= 64 } gender ||
                id.Any(char.IsControl) || name.Any(char.IsControl) || gender.Any(char.IsControl))
                throw new InvalidDataException("Missing or invalid draw result.");
            winner = new(id, name, gender);
        }
        return new(true, "success", action == SecRandomAction.QuickDraw ? "闪抽已完成。" : "SecRandom 已确认操作完成。", Winner: winner);
    }

    private static string? Get(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static void ValidateCode(string code)
    {
        if (code.Length is < 1 or > 80 || code.Any(x => !char.IsAsciiLetterOrDigit(x) && x != '_'))
            throw new InvalidDataException("Invalid error code.");
    }

    public static string Message(string code) => code switch
    {
        "pipe_unavailable" => "未连接到 SecRandom，请先启动软件，再检查接口。",
        "timeout" => "等待 SecRandom 超时；已发送的操作可能仍在进行，请先在其窗口核实。",
        "access_denied" => "Windows 拒绝管道访问，请核对两边用户与管理员权限。",
        "peer_mismatch" or "peer_unverifiable" => "无法确认 SecRandom 属于配置的程序、当前用户和会话，未发送操作。",
        "authorization_denied" => "SecRandom 未允许操作，请处理它的安全验证或课程限制。",
        "oobe_required" => "请先完成 SecRandom 的首次设置。",
        "integrity_confirmation_required" => "请先在 SecRandom 确认设置完整性。",
        "invalid_state" => "SecRandom 当前无法抽取，请检查名单、候选人数及是否已有抽取进行中。",
        "feature_disabled" => "SecRandom 已关闭该功能。",
        "invalid_response" => "SecRandom 回执无效或连接中断；请核实已发送的操作。",
        _ => "SecRandom 未完成操作（" + (code.Length <= 80 ? code : "unknown_error") + "）。"
    };
}
