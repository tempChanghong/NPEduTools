using System.Globalization;
using NPEduTools.Core;

namespace NPEduTools.App;

internal sealed record ClassIslandClockPresentation(string Clock, string Detail)
{
    public static ClassIslandClockPresentation From(SchoolClockReading reading) =>
        reading.Fresh && reading.Now is { } now
        ? new(now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            $"ClassIsland 调整后的学校时间 · 样本年龄 {reading.AgeMs / 1000:F1} 秒\n{reading.Message}")
        : new("--:--:--", "学校时间尚未同步；不会用 Windows 时间代替。\n" + reading.Message);
}
