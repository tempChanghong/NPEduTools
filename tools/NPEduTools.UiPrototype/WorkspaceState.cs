using System;
using System.Globalization;

namespace NPEduTools.UiPrototype;

// Local fixtures only. No Host, HTTP, sensor, persistence or production configuration.
internal sealed class WorkspaceState
{
    internal static readonly DateTime DemoNow = new(2026, 10, 13, 18, 40, 0);
    internal string ManualState { get; private set; } = "Idle";
    internal DateTime? ManualStart { get; private set; }
    internal DateTime? ManualEnd { get; private set; }
    internal bool SchoolActive { get; private set; }
    internal bool ClockReady { get; private set; } = true;
    internal bool ReferenceSaved { get; set; }
    internal bool HasReport { get; private set; }

    internal string? Arrange(bool immediate, DateTime? startDate, string startTime, bool hasEnd, DateTime? endDate, string endTime)
    {
        if (SchoolActive) return "学校定时自习正在进行，暂时不能开始手动自习。";
        if (ManualState != "Idle") return "已有手动自习，请先结束或取消当前安排。";
        if (!immediate && !TryDateTime(startDate, startTime, out _)) return "请选择开始日期，并输入有效时间，例如 19:00。";
        DateTime start = immediate ? DemoNow : Combine(startDate!.Value, startTime);
        if (start < DemoNow) return "开始时间已经过去，请选择之后的时间或立即开始。";
        if (hasEnd && !TryDateTime(endDate, endTime, out _)) return "请选择结束日期，并输入有效时间，例如 21:30。";
        DateTime? end = hasEnd ? Combine(endDate!.Value, endTime) : null;
        if (end <= start) return "结束时间必须晚于开始时间。";
        if (end - start > TimeSpan.FromHours(3)) return "手动自习最长三小时，请调整结束时间。";
        ManualStart = start; ManualEnd = end; ManualState = start == DemoNow ? "Active" : "Scheduled";
        return null;
    }

    internal void FinishManual()
    {
        if (ManualState == "Active") HasReport = true;
        ManualState = "Idle"; ManualStart = null; ManualEnd = null;
    }

    internal void ChangeScene(string scene)
    {
        FinishManual(); HasReport = false; ReferenceSaved = false;
        SchoolActive = scene == "SchoolActive"; ClockReady = scene != "ClockUnavailable";
    }

    internal void StartSchool()
    {
        FinishManual(); SchoolActive = true; ClockReady = true;
    }
    internal void ConfirmClock() => ClockReady = true;

    private static bool TryDateTime(DateTime? date, string text, out DateTime value)
    {
        value = default;
        if (date is null || !TimeSpan.TryParseExact(text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var time) || time >= TimeSpan.FromDays(1)) return false;
        value = date.Value.Date.Add(time); return true;
    }
    private static DateTime Combine(DateTime date, string text) { TryDateTime(date, text, out var value); return value; }
}
