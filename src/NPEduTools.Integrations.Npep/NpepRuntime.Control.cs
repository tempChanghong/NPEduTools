using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepRuntime
{
    private NpepControlPolicyStore? _controlPolicy;
    private string? _controlBinding;

    public RemoteExamPolicy ControlPolicy()
    {
        lock (_sync)
        {
            var policy = _controlPolicy?.State;
            string? error = _controlPolicy?.Error ?? (_controlPolicy is null ? "POLICY_STORE_UNAVAILABLE" : null);
            bool canEnable = error is null && !_disposed && Snapshot() is
                { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false } && policy?.Scope is not null;
            return new(policy?.Revision ?? 0, error is null && policy?.Allowed == true, policy?.Scope,
                policy?.ConsentId ?? Guid.Empty, policy?.ControlEpoch ?? Guid.Empty, canEnable,
                error is not null ? "本地许可文件不可用，控制已禁用；原文件已保留。" :
                (policy?.Allowed == true ? canEnable ? "已配对，学校可远程切换考试／日常模式并投递方案。" : "配对保留，等待恢复学校连接。"
                : "配对后自动开放学校考试控制；解绑或学校撤销后停止。") + _controlMessage, error, _controlBinding);
        }
    }

    public void SetControlConsent(RemoteExamCommand command)
    {
        lock (_sync)
        {
            if (command.Action != "consent" || !RemoteExamContract.Valid(command)) throw new NpepException("INVALID_REQUEST");
            var policy = ControlPolicy();
            if (policy.Error is not null) throw new NpepException(policy.Error);
            if (command.Allowed == true && !policy.CanEnable) throw new NpepException("CONTROL_OFFLINE");
            if (command.Allowed != true) throw new NpepException("INVALID_REQUEST");
            _controlPolicy!.Synchronize(policy.Scope);
            _controlCancellation?.Cancel();
        }
    }

    private void SynchronizeControlScope(JsonObject view, string connection)
    {
        // Called under _sync at every published lifecycle transition, independently of UI polling.
        string? scope = null;
        _controlBinding = null;
        if (view.Text("state") == "ACTIVE" && connection != "STOPPED" && view["suspended"]?.GetValue<bool>() != true &&
            view["registration"] is JsonObject registration)
        {
            // The extra approval ID also fences a new pairing even if a server reuses a device ID.
            string[] identity = [view.Text("origin"), registration.Text("serverInstanceId"),
                registration.Text("deploymentEpoch"), registration.Text("deviceId"),
                registration.Text("screenBindingId"), registration["bindingRevision"]!.ToJsonString(),
                registration["credentialGeneration"]!.ToJsonString(), (view["approval"] as JsonObject)?.Text("approvalId") ?? ""];
            scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(identity))));
            var approval = view["approval"] as JsonObject;
            _controlBinding = $"{approval?.Text("schoolName")} / {approval?.Text("administrativeClassName")} / {approval?.Text("screenBindingName")}\n{view.Text("origin")}";
        }
        _controlPolicy?.Synchronize(scope);
        _planPolicy?.Synchronize(scope);
    }
}
