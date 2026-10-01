using System.Globalization;

namespace NPEduTools.Core;

/// <summary>Signal display only: the trend's lower bound is not an acoustic noise threshold.</summary>
public static class NoiseSignalPresentation
{
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
