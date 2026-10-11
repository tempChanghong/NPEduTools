using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NPEduTools.App;

public sealed record NpepDiagnosticFinding(string Code, string Message);
public sealed record NpepDiagnosticComparisonResult(string Title, IReadOnlyList<NpepDiagnosticFinding> Findings)
{
    public string DisplayText => Title + "\n" + string.Join("\n", Findings.Select(item => "• " + item.Message));
}

/// <summary>Local comparison of existing observations. Never echoes imported free text or performs a control action.</summary>
public static class NpepDiagnosticComparison
{
    public const int MaximumInputBytes = 2 * 1024 * 1024;

    public static async Task<JsonObject> ReadSchoolSummaryAsync(Stream input, CancellationToken token = default)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[32768];
        while (true)
        {
            int count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaximumInputBytes + 1 - (int)bytes.Length)), token);
            if (count == 0) break;
            bytes.Write(buffer, 0, count);
            if (bytes.Length > MaximumInputBytes) throw new InvalidDataException("Diagnostic file exceeds the size limit.");
        }
        token.ThrowIfCancellationRequested();
        return ParseSchoolSummary(bytes.ToArray());
    }

    public static JsonObject ParseSchoolSummary(byte[] bytes)
    {
        if (bytes.Length > MaximumInputBytes) throw new InvalidDataException("Diagnostic file exceeds the size limit.");
        ReadOnlyMemory<byte> content = bytes;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) content = content[3..];
        try
        {
            using var document = JsonDocument.Parse(content, new() { MaxDepth = 32 });
            CheckDuplicateProperties(document.RootElement);
            var root = JsonNode.Parse(content.Span, documentOptions: new() { MaxDepth = 32 }) as JsonObject;
            ValidateSchoolSummary(root);
            return root!;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        { throw new InvalidDataException("Invalid school diagnostic summary."); }
    }

    public static NpepDiagnosticComparisonResult Compare(JsonObject desktop, JsonObject school, DateTimeOffset now)
    {
        ValidateSchoolSummary(school);
        if (Text(desktop, "kind") != "npep-desktop-diagnostics" || Integer(desktop, "schemaVersion") != 1)
            throw new InvalidDataException("Invalid desktop diagnostic summary.");
        var findings = new List<NpepDiagnosticFinding>();
        void Add(string code, string message) => findings.Add(new(code, message));
        NpepDiagnosticComparisonResult Finish(string title) => new(title, findings);
        Add("OBSERVATION_ONLY", "对照的是本机缓存和学校最近观测，不能证明此刻软件状态或远程操作已完成。");
        var connection = desktop["connection"] as JsonObject;
        if (Boolean(connection, "available") != true)
        {
            Add("LOCAL_CONNECTION_UNAVAILABLE", "未读到本机学校互联状态。确认 NPEduTools 后台正在运行，再重新对照。");
            return Finish("资料不足，暂时无法核对归属");
        }
        string? localOrigin = Origin(Text(connection, "origin")), schoolOrigin = Origin(Text(school, "backendOrigin"));
        if (localOrigin is null || schoolOrigin is null)
        {
            Add("ORIGIN_UNAVAILABLE", "缺少有效的服务地址。请检查当前服务提供商，并让管理员重新导出学校摘要。");
            return Finish("资料不足，暂时无法核对服务");
        }
        if (!string.Equals(localOrigin, schoolOrigin, StringComparison.OrdinalIgnoreCase))
        {
            Add("ORIGIN_MISMATCH", "两端服务地址不同（包括协议或端口）。核对分离部署的 API 地址后重新导出；不要直接重新配对。");
            return Finish("无法对照：服务地址不一致");
        }
        var binding = connection?["binding"] as JsonObject;
        var schoolId = Id(binding, "schoolId");
        var classId = Id(binding, "administrativeClassId");
        var screenId = Id(binding, "screenBindingId");
        if (schoolId is null || classId is null || screenId is null || Id(school, "schoolId") is null)
        {
            Add("BINDING_UNAVAILABLE", "归属编号不完整。先在学校互联页面核对已确认的学校、班级和大屏。");
            return Finish("资料不足，暂时无法核对归属");
        }
        if (schoolId != Id(school, "schoolId"))
        {
            Add("SCHOOL_MISMATCH", "摘要来自另一所学校。请学校管理员导出当前学校的文件。");
            return Finish("无法对照：学校归属不一致");
        }
        AddFreshnessFindings(desktop, school, connection!, now, Add);
        var matches = ((JsonArray)school["items"]!).OfType<JsonObject>()
            .Where(item => Id(item, "schoolId") == schoolId && Id(item, "screenBindingId") == screenId).ToArray();
        if (matches.Length == 0)
        {
            Add("RECORD_NOT_INCLUDED", "当前摘要没有对应大屏登记。请在网页选择全部登记、搜索并加载该设备后重新导出；分页或筛选遗漏不能证明未配对。");
            return Finish("当前文件未包含对应登记");
        }
        if (matches.Length > 1)
        {
            Add("AMBIGUOUS_REGISTRATION", "同一大屏绑定有多份登记，摘要无法确认哪份属于本机。请管理员按设备详情缩小范围后重新导出；不会自动选择历史或在线登记。");
            return Finish("存在多份登记，暂不比较运行状态");
        }
        var item = matches[0];
        if (Id(item, "administrativeClassId") != classId)
        {
            Add("CLASS_MISMATCH", "同一大屏编号的班级归属不同。请管理员核对大屏绑定和历史登记。");
            return Finish("无法对照：班级归属不一致");
        }
        Add("BINDING_MATCHED", "服务、学校、班级和大屏编号匹配；这不等于核实了物理电脑、部署代际或当前授权。");
        if (At(item, "lastSeenAt") is { } received)
            Add("SCHOOL_RECEIVED_AT", "学校最近收到该登记的上报：" + received.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) + "。");
        string? state = Text(item, "state");
        if (Boolean(item, "bindingChanged") == true || state is "REVOKED" or "EXPIRED" or "INVALIDATED")
        {
            Add("REGISTRATION_INACTIVE", "学校摘要显示撤销、过期或绑定失效。先让管理员核对登记与授权，保留现有配置；不自动重新配对。");
            return Finish("找到对应绑定，但学校登记需要核对");
        }
        if (state != "ACTIVE") Add("AUTHORIZATION_UNKNOWN", "学校登记状态未知，不能据此确认授权仍有效。");
        if (Text(connection, "transport") is "OFFLINE" or "UNAVAILABLE")
            Add("LOCAL_TRANSPORT_UNAVAILABLE", "本机当前未确认连接。检查网络及服务状态，等待重试后重新对照。");
        if (Boolean(connection, "reportingPaused") == true || Text(connection, "transport") is "PAUSED" or "STOPPED" or "DISABLED")
            Add("LOCAL_REPORTING_PAUSED", "本机上报已暂停或停止，学校保留的观测不会代表后续变化。核对暂停原因后再恢复。");
        if (Text(connection, "error") == "TLS_VALIDATION_FAILED")
            Add("TLS_VALIDATION_FAILED", "本机记录了 TLS 验证失败。核对 Windows 时间、证书和服务地址；后台仍会重试，不应关闭证书校验。");
        if (Text(item, "connectivity") != "ONLINE")
            Add("SCHOOL_NOT_RECENTLY_ONLINE", "学校没有近期在线观测。失联不代表软件已经退出；检查后台和网络后刷新学校概览。");
        var local = desktop["localCache"] as JsonObject;
        var status = item["status"] as JsonObject;
        if (Boolean(local, "available") != true || status is null)
            Add("RUNTIME_DATA_UNAVAILABLE", "至少一端缺少运行观测，暂不比较模式与录制状态。");
        else
        {
            string? mode = Text(local, "mode") switch { "Daily" => "DAILY", "Exam" => "EXAM", "Unconfigured" => "UNCONFIGURED", _ => null };
            string? remoteMode = Allowed(Text(status, "mode"), "DAILY EXAM UNCONFIGURED");
            if (mode is null || remoteMode is null) Add("MODE_UNKNOWN", "至少一端模式未知。先在大屏核实模式，再刷新学校概览。");
            else if (mode != remoteMode) Add("MODE_OBSERVATIONS_DIFFER", "两端观测到的模式不同。可能是缓存或上报延迟；核实当前模式并重新导出，不能据此判定切换失败。");
            else Add("MODE_OBSERVATIONS_AGREE", "两端模式观测一致，仅表示这两份记录一致。");
            if (remoteMode == "DAILY" && Text(status["classIsland"] as JsonObject, "connection") is "DISCONNECTED" or "UNKNOWN")
                Add("CLASSISLAND_NEEDS_REVIEW", "学校观测到日常模式下课程桥接未就绪。到大屏核对 ClassIsland 与时间桥接；考试看板断开在日常模式下不单独算异常。");
            if (remoteMode == "EXAM" && Text(status["examAware"] as JsonObject, "connection") is "DISCONNECTED" or "UNKNOWN")
                Add("EXAMAWARE_NEEDS_REVIEW", "学校观测到考试模式下看板桥接未就绪。到大屏核对 ExamAware2 与桥接插件；ClassIsland 退出在考试模式下不单独算异常。");
            if (Text(local, "modePhase") is "Incomplete" or "Unavailable" || Text(status, "modePhase") is "INCOMPLETE" or "UNAVAILABLE")
                Add("MODE_NEEDS_REVIEW", "至少一份观测记录了切换未完成或不可用。到大屏查看具体步骤和恢复入口，勿重复强制切换。");
            string? recording = Text(local, "recordingPhase") switch { "Idle" or "Saved" => "IDLE", "Recording" => "RECORDING", "Paused" => "PAUSED", "Saving" => "FINALIZING", _ => null };
            string? remoteRecording = Allowed(Text(status, "recording"), "IDLE RECORDING PAUSED FINALIZING");
            if (recording is null || remoteRecording is null) Add("RECORDING_UNKNOWN", "至少一端录制状态未知或处于过渡，不能确认是否正在录制。");
            else if (recording != remoteRecording) Add("RECORDING_OBSERVATIONS_DIFFER", "两端录制观测不同。请在大屏核对录课状态；不要为了排查而强行停止采集。");
        }
        return Finish("已找到同一大屏绑定的登记");
    }

    private static void AddFreshnessFindings(JsonObject desktop, JsonObject school, JsonObject connection, DateTimeOffset now,
        Action<string, string> add)
    {
        var generated = At(school, "generatedAt");
        var asOf = At(school, "asOf");
        var observed = At(connection, "observedAt");
        if (observed is { } localAt && asOf is { } schoolAt)
            add("OBSERVED_AT", "本机互联读取：" + localAt.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) +
                "；学校概览观测：" + schoolAt.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) + "。");
        if (generated is null || asOf is null || observed is null || asOf > generated || generated > now.AddSeconds(60) ||
            observed > now.AddSeconds(60) || (generated - observed)?.Duration() > TimeSpan.FromMinutes(5))
            add("TIME_REFERENCE_UNCERTAIN", "观测时间缺失或两端时间基准差异较大。先核对 Windows／服务器时间，再采集接近时刻的摘要；不以学校铃声时间判断上报新鲜度。");
        else if (Boolean(school, "stale") == true || now - asOf > TimeSpan.FromMinutes(1) || now - observed > TimeSpan.FromMinutes(1))
            add("OBSERVATION_STALE", "至少一份观测已过时。请刷新学校概览并立即重新导出；本次差异只作排查线索。");
        if (Boolean(school, "hasMore") == true || Integer(school, "omittedLoadedCount") > 0 || Text(school, "filter") != "ALL")
            add("PARTIAL_SCHOOL_EXPORT", "学校文件包含筛选、分页或导出数量限制，不能当作全校完整设备清单。");
        var cache = desktop["localCache"] as JsonObject;
        if (Boolean(cache, "available") == true && (At(cache, "modeCheckedAt") is not { } checkedAt || now - checkedAt > TimeSpan.FromMinutes(1)))
            add("LOCAL_MODE_CACHE_OLD", "本机模式核查时间缺失或较早。这次读取没有重新探测软件；需要时在大屏主动核实当前模式。");
    }

    private static void ValidateSchoolSummary(JsonObject? root)
    {
        if (root is null || Integer(root, "schemaVersion") != 1 || Text(root, "kind") != "npep-school-overview-diagnostics" ||
            root["items"] is not JsonArray items || items.Count > 200 || items.Any(item => item is not JsonObject) ||
            Boolean(root, "stale") is null || Boolean(root, "hasMore") is null ||
            Integer(root, "exportedCount") != items.Count || Integer(root, "loadedCount") is not { } loaded || loaded < items.Count ||
            Integer(root, "omittedLoadedCount") != loaded - items.Count || Integer(root, "matchedTotal") is not { } matched || matched < loaded)
            throw new InvalidDataException("Unsupported or malformed school diagnostic summary.");
    }

    private static void CheckDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new InvalidDataException("Duplicate diagnostic properties are not supported.");
                CheckDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) CheckDuplicateProperties(item);
    }
    private static string? Text(JsonObject? value, string key) => value?[key] is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;
    private static bool? Boolean(JsonObject? value, string key) => value?[key] is JsonValue scalar && scalar.TryGetValue<bool>(out var flag) ? flag : null;
    private static long? Integer(JsonObject? value, string key)
    {
        if (value?[key] is not JsonValue scalar) return null;
        if (scalar.TryGetValue<long>(out var number) && number >= 0) return number;
        return scalar.TryGetValue<int>(out var small) && small >= 0 ? small : null;
    }
    private static Guid? Id(JsonObject? value, string key) => Guid.TryParseExact(Text(value, key), "D", out var id) && id != Guid.Empty ? id : null;
    private static DateTimeOffset? At(JsonObject? value, string key) => DateTimeOffset.TryParse(Text(value, key), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at.ToUniversalTime() : null;
    private static string? Allowed(string? value, string choices) => value is not null && choices.Split(' ').Contains(value) ? value : null;
    private static string? Origin(string? value)
    {
        try
        {
            return value is { Length: <= 2048 } && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                uri.Scheme is "http" or "https" && uri.UserInfo == "" && uri.AbsolutePath == "/" && uri.Query == "" && uri.Fragment == "" ?
                new UriBuilder(uri.Scheme, uri.IdnHost, uri.IsDefaultPort ? -1 : uri.Port).Uri.GetLeftPart(UriPartial.Authority) : null;
        }
        catch (Exception error) when (error is UriFormatException or ArgumentException) { return null; }
    }
}
