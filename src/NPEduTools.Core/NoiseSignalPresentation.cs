using System.Globalization;

namespace NPEduTools.Core;

/// <summary>Signal display only: the trend's lower bound is not an acoustic noise threshold.</summary>
public static class NoiseSignalPresentation
{
    public static string Estimated(double? value) => value is { } v && double.IsFinite(v) ? $"{v:F1} 估算 dB(A)" : "— 估算 dB(A)";
    public static string EstimateReason(NPEduTools.Contracts.NoiseEstimate? e) => e?.Reason switch
    {
        "DEVICE_CHANGED" => "麦克风已更换，参考失效，须重新校准",
        "INPUT_SETTINGS_CHANGED" => "输入设置已变化，参考失效，须重新校准",
        "INPUT_SETTINGS_UNAVAILABLE" => "无法核对输入设置，参考失效，须重新校准",
        "REFERENCE_STORE_UNAVAILABLE" => "参考文件不可用",
        "A_WEIGHTING_UNAVAILABLE" => "当前采集不支持 A 加权估算",
        _ => e?.State switch { "Active" => $"估算 dB(A) · A/{e.TimeWeighting}", "WarmingUp" => "A 加权预热中", "Stopped" => "估算采集已结束", "NoData" => "等待有效估算（30–130 dB(A)，削波 ≤ 0.1%）", _ => "尚无有效外部参考，当前为 dBFS" }
    };
    public const double TrendMinimumDbfs = -100;

    public static bool IsWeak(double? value) => value is { } d && double.IsFinite(d) && d <= TrendMinimumDbfs;

    public static string Level(double? value)
    {
        if (value is not { } d || !double.IsFinite(d)) return "—";
        return d <= NoiseMeter.NumericalFloorDbfs ? "≤ -160.0" : d.ToString("F1", CultureInfo.InvariantCulture);
    }

    public static string Statistic(double? value)
    {
        var text = Level(value);
        return text == "—" ? text : text + " dBFS" + (value <= NoiseMeter.NumericalFloorDbfs ? "（数值下限）" : "");
    }

    public static string Quality(string quality, double? value) => quality switch
    {
        "Good" => IsWeak(value) ? "输入接近静音" : "采样有效",
        "DigitalSilence" => "全零信号，检查静音",
        "Clipping" => "输入削波",
        "Invalid" => "采样无效",
        "NoData" => "没有新数据",
        _ => "等待采样"
    };

    public static string Hint(double? value)
    {
        if (!IsWeak(value)) return "";
        string floor = value <= NoiseMeter.NumericalFloorDbfs ? "已达到数值下限；更低的输入不再显示精确读数。" : "";
        return floor + "当前输入接近静音，可能受到静音或降噪处理影响；请通过说话确认输入响应。";
    }
}
