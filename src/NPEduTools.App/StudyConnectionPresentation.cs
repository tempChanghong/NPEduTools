using System.Globalization;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public sealed record StudyConnectionLabel(string Text, string Tone, string Detail);

// Connection receipts and study execution are separate facts. Never infer uploads from either.
public static class StudyConnectionPresentation
{
    public static StudyConnectionLabel Describe(StudyHostState study, NpepState? connection, bool fresh)
    {
        if (!fresh) return new("后台状态待确认", "Muted", "尚未收到新的本机后台状态，不能确认学校连接是否仍然正常。");
        bool continuing = study.PolicyState == "APPLIED" && study.Phase is "Preparing" or "Active" && study.Reason == "OFFLINE_CONTINUING";
        StudyConnectionLabel Warning(string text, string detail) => new(continuing ? text + " · 本次继续" : text, "Warning",
            detail + (continuing ? " 本次自习按原安排继续，到点结束。" : "") + " 报告与照片的上传结果请在监测管理中核实。");
        if (connection is null) return continuing ? Warning("连接中断", "学校连接状态尚未确认。") :
            new("学校互联待确认", "Muted", "后台尚未提供学校连接状态；本机自习状态不代表服务器已收到数据。");
        if (connection.State == "STORE_UNAVAILABLE") return Warning("互联暂不可用", "学校互联凭据暂不可用，请到学校互联页核实。");
        if (connection.State == "SUSPENDED") return Warning("学校连接已停用", "请联系学校管理员核实授权。");
        if (connection.State == "UNPAIRING") return Warning("解绑结果待确认", "学校解绑结果尚未确认。");
        if (connection.ReportingPaused || connection.Connection == "PAUSED")
            return new("学校互联已暂停", "Muted", "互联被主动暂停，不会自行解除暂停。");
        if (connection.State == "UNPAIRED") return new(study.Manual is not null ? "仅本机自习" : "学校尚未连接", "Muted", "尚未完成学校配对，不能确认服务器收到本次数据。");
        if (connection.Connection == "STOPPED") return Warning("学校连接已停止", "请在学校互联页查看原因并重新连接；当前未在自动重连。");
        if (connection.State == "ACTIVE" && connection.Connection == "OFFLINE")
            return Warning("学校正在重连", "暂未收到新的学校服务回执，后台会自动重试。");
        if (connection.Busy) return new("学校互联处理中", "Muted", "学校互联操作尚在处理，等待新的状态确认。");
        if (connection.State == "ACTIVE" && connection.Connection == "ONLINE")
        {
            if (continuing) return Warning("安排待同步", "学校连接已恢复，本次安排仍在等待同步确认。");
            if (!DateTimeOffset.TryParse(connection.LastReceivedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var received))
                return new("学校互联待确认", "Muted", "没有可核实的学校服务回执；不能认定同步正常。");
            return new("学校互联正常", "Normal", $"最近回执：{received.ToLocalTime():MM-dd HH:mm:ss}（本机时间）。\n学校服务最近收到设备状态；这不代表本次报告或照片已上传。");
        }
        if (connection.State == "ACTIVE" && connection.Connection is "CONNECTING" or "WAITING")
            return new("学校正在连接", "Muted", "正在等待学校服务的新回执。");
        return new("学校互联待确认", "Muted", "请在学校互联页核实连接状态，未知状态不能认定同步正常。");
    }
}
