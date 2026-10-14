using NPEduTools.Contracts;

namespace NPEduTools.App;

internal sealed record NoiseCalibrationLabel(string Text, string Detail, string Tone);

internal static class NoiseCalibrationPresentation
{
    public static string MethodName(string method) => method switch
    {
        "Quick" => "已快速校准",
        "ManualReference" => "已参考校准",
        "UsbReferenceBeta" => "已联机校准 (Beta)",
        _ => "校准状态待确认"
    };
    public static NoiseCalibrationLabel Describe(NoiseCalibrationStatus? status, NoiseState? noise, bool fresh = true)
    {
        if (!fresh || status is null || noise is null || status.InstanceId == Guid.Empty ||
            status.InstanceId != noise.InstanceId || status.NoiseRevision != noise.Revision)
            return new("校准状态待确认", "尚未取得当前后台的校准状态；不沿用旧校准标识。", "Muted");
        string reason = status.Reason switch
        {
            "DEVICE_CHANGED" => "监测麦克风已更换，请重新校准。",
            "INPUT_SETTINGS_CHANGED" => "输入音量、静音或采集格式已变化，请重新校准。",
            "INPUT_SETTINGS_UNAVAILABLE" => "无法核对输入设置，请重新校准。",
            "SCHOOL_CONFIGURATION_CHANGED" => "学校配置已变化，请重新确认快速校准的双阈值。",
            "SCHOOL_CONFIGURATION_UNCONFIRMED" => "学校配置尚未确认，快速阈值暂不应用。",
            "A_WEIGHTING_UNAVAILABLE" => "当前采集不支持 A 加权估算，原始 dBFS 采样仍可继续。",
            _ => "校准文件或当前输入不可用，请在校准页核对。"
        };
        if (status.State == "Invalidated") return new("校准已失效", reason, "Warning");
        if (status.State == "Unavailable") return new("校准不可用", reason, "Warning");
        if (status.State == "Uncalibrated") return new("未校准", "当前使用原始 dBFS。可通过快速校准确认双阈值；声级计参考用于估算 dB(A)。", "Muted");
        if (status.State is not ("Saved" or "Ready") || status.Method is not ("Quick" or "ManualReference" or "UsbReferenceBeta"))
            return new("校准状态待确认", "后台校准状态尚未兼容，请刷新或更新后台。", "Muted");
        string detail = status.Method == "Quick" ? "场景采样用于本机 dBFS 双阈值；没有转换为实际声压级。" :
            "声级计参考用于估算 dB(A)，原始 dBFS 分开保留。";
        detail += status.State == "Ready" ? " 当前采集的输入设置已核对。" :
            " 结果已保存；下次采集核对输入设置后才应用。";
        if (status.Method == "UsbReferenceBeta") detail += " USB 联机校准为 Beta。";
        if (status.Reason is not null) detail += " " + reason;
        if (status.ConfirmedAt is { } at) detail += $"\n确认时间：{at.ToLocalTime():yyyy-MM-dd HH:mm}";
        return new(MethodName(status.Method), detail, status.State == "Ready" ? "Normal" : "Muted");
    }
}
