using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using NPEduTools.Contracts;

namespace NPEduTools.Host;

public sealed partial class ExamAwareService
{
    // Remote requests use the same command queue and process validation as the local UI.
    // Recheck consent/hash under the queue immediately before dispatch; retain the lease until the ack.
    internal async Task<HostResponse> RemotePlanAsync(HostRequest request, string expectedHash, Action authorize, CancellationToken token)
    {
        using var lease = _runtimeGate.TryEnterMutation() ?? throw new RemoteExamException("OPERATION_BUSY");
        await _commands.WaitAsync(token);
        HostResponse reply;
        try
        {
            authorize();
            if (_failure is not null || !_planTask.IsCompleted || !_quitTask.IsCompleted || !_autoStartTask.IsCompleted)
                throw new RemoteExamException("OPERATION_BUSY");
            if (_saved.Receipts.Any(x => x.Id == request.RequestId)) throw new RemoteExamException("ALREADY_ACCEPTED");
            if (request.ExamPlan!.Action == "start" && Snapshot().PreparedPlan?.Sha256 != expectedHash)
                throw new RemoteExamException("PLAN_EXPIRED");
            reply = BeginPlan(request, authorize);
        }
        finally { _commands.Release(); }
        // Once dispatched, cancellation cannot make releasing the recording/mutation reservation safe.
        await _planTask;
        return reply with { ExamAware = Snapshot() };
    }

    private Task _planTask = Task.CompletedTask;
    private Session? _planSession;
    private ExamAwarePlanSummary? _preparedPlan;
    private ExamAwarePlanState? _planOperation;
    private ExamAwarePlanAck? _planAck;

    // Called under _commands and the same runtime mutation lease as quit/startup operations.
    private HostResponse BeginPlan(HostRequest request, Action? beforeDispatch = null)
    {
        HostResponse Reply(string code, string message) => new(Protocol.Version, request.RequestId, "Rejected", code, message, ExamAware: Snapshot());
        if (request.ExamPlan is not { } input || !ExamAwarePlanContract.Valid(input)) return Reply("InvalidExamPlan", "请选择有效的 UTF-8 考试方案 JSON（不超过 24 KiB）。");
        if (!_quitTask.IsCompleted) return Reply("QuitBusy", "正在退出考试看板，请稍后再试。");
        if (_saved.Path is null) return Reply("PathMissing", "请先保存 ExamAware 程序位置。");
        if (_saved.Revision != request.ExpectedRevision) return Reply("RevisionConflict", "程序位置或配对已变化，请刷新并重新校验方案。");
        string path = _target.Validate(_saved.Path);
        Session session;
        int pid;
        lock (_sync)
        {
            if (Snapshot().BridgeState != "Connected" || _session is null) return Reply("BridgeDisconnected", "请先连接 ExamAware 桥接。");
            if (!_sample!.CanPresent) return Reply("BridgeUpgradeRequired", "方案放映需要桥接 0.4.0，并授权放映与观察权限。");
            session = _session; pid = _sample.ProcessId;
            if (input.Action == "start")
            {
                if (!ReferenceEquals(_planSession, session) || _preparedPlan?.PreparationId != input.PreparationId)
                    return Reply("PlanExpired", "方案校验已失效，请重新选择并校验文件。");
                if (_sample.Player is not { Known: true }) return Reply("PlayerUnknown", "无法确认当前放映状态，请检查插件权限。");
                if (_sample.Player.Sessions.Length != 0) return Reply("PlayerBusy", "已有放映，请在 ExamAware 中结束后再试；不会替换。");
            }
        }
        var process = _target.Capture(pid, path);
        try { Save(_saved with { Receipts = Receipts(request) }); }
        catch { process.Dispose(); throw; }
        lock (_sync)
        {
            _preparedPlan = null; _planAck = null; _planSession = session;
            _planOperation = new(request.RequestId, "Sending", input.Action == "prepare" ? "正在由 ExamAware 校验考试方案…" : "已发送放映请求，正在确认受理结果…");
        }
        _planTask = ExecutePlanAsync(request.RequestId, input, session, process, beforeDispatch);
        return new(Protocol.Version, request.RequestId, "Accepted", null, "请求已受理，请查看方案及放映状态。", ExamAware: Snapshot());
    }

    private async Task ExecutePlanAsync(Guid id, ExamAwarePlanInput input, Session session, IExamAwareProcess process, Action? beforeDispatch = null)
    {
        void Unconfirmed()
        {
            lock (_sync) { _preparedPlan = null; _planOperation = new(id, "Unconfirmed", "未能确认结果。请检查 ExamAware 实际窗口；没有自动重试，重新连接后需重新校验方案。"); }
        }
        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                // Target validation/storage can take time: recheck remote consent/deadline at dispatch.
                beforeDispatch?.Invoke();
                long issued = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string payload = JsonSerializer.Serialize(new ExamAwarePlanWireCommand(id, "plan." + input.Action, issued,
                    issued + 3000, input.DataBase64, input.PreparationId), Protocol.Json);
                long sequence = ++session.OutSequence;
                await Protocol.WriteAsync(session.Client.GetStream(), new ExamAwareFrame(2, "command", sequence, payload,
                    Sign(session.Key, FrameText("host", session.ServerNonce, session.ClientNonce, sequence, "command", payload))), timeout.Token);
                while (true)
                {
                    ExamAwarePlanAck? ack;
                    bool same;
                    lock (_sync) { ack = _planAck; same = ReferenceEquals(_session, session); }
                    if (!same || process.HasExited) { Unconfirmed(); return; }
                    if (ack is not null)
                    {
                        if ((ack.State == "Prepared" && (input.Action != "prepare" || ack.Summary!.Sha256 !=
                            Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(input.DataBase64!))).ToLowerInvariant())) ||
                            (ack.State == "Started" && input.Action != "start")) throw new InvalidDataException();
                        lock (_sync)
                        {
                            if (!ReferenceEquals(_session, session)) { Unconfirmed(); return; }
                            _preparedPlan = ack.State == "Prepared" ? ack.Summary : null;
                            _planOperation = new(id, ack.State, ack.State switch
                            {
                                "Prepared" => "方案校验通过。请核对摘要，再手动开始放映。",
                                "Started" => "ExamAware 已创建放映会话；是否就绪以实时状态为准。",
                                "Busy" => "已有放映或操作正在进行，未替换现有放映。请结束后重新校验。",
                                "Invalid" => "方案格式或字段无效，或超过限制（32 场、名称 160 字、提示语 2000 字、摘要共 6000 字）。请用 ExamAware 编辑器检查文件。",
                                "Denied" => "插件权限不足，请授权 player.start 与 player.observe 后重试。",
                                "Expired" => "请求或已校验方案已过期，请重新校验文件。",
                                "Unsupported" => "当前桥接接口不支持方案放映，请更新插件。",
                                "LimitReached" => "本次桥接激活已达到 64 条命令上限。请在 ExamAware 中停用并重新启用桥接，再重新校验方案。",
                                _ => "结果未确认，请检查实际放映窗口；没有自动重试。"
                            }, ack.SessionId);
                        }
                        return;
                    }
                    await Task.Delay(50, timeout.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or SocketException or ObjectDisposedException or InvalidOperationException or System.ComponentModel.Win32Exception or RemoteExamException or NPEduTools.Integrations.Npep.NpepException)
            {
                // Invalidate plugin-side prepared data as well; a delayed response must not be reused.
                session.Client.Dispose();
                Unconfirmed();
            }
        }
    }
}
