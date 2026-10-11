using NPEduTools.Contracts;
namespace NPEduTools.Core;

// Display and reminders are presentation only; sensor permission starts at Window.Start.
public static class StudyPresentationTiming
{
    public const int ScheduledDisplayLeadSeconds = 300;
    public const int PreparationLeadSeconds = 120;
    public const int PreparationNoticeSeconds = 10;
    public const int StageNoticeSeconds = 5;
    public const int SeatingDismissAfterStartSeconds = 120;
    public static DateTimeOffset PreparationStart(StudyHostState state) => state.Window!.Start.AddSeconds(
        -Math.Min(PreparationLeadSeconds, state.Manual?.DisplayLeadSeconds ?? PreparationLeadSeconds));
    public static double NoticeRemaining(StudyHostState state)
    {
        if (state.Window is not { } w || state.SchoolNow is not { } now) return 0;
        var begin = state.Phase switch { "Preparing" => PreparationStart(state), "Active" => w.Start, _ => w.End };
        if (now < begin) return 0;
        var end = begin.AddSeconds(state.Phase == "Preparing" ? PreparationNoticeSeconds : StageNoticeSeconds);
        if (state.Phase == "Preparing" && end > w.Start) end = w.Start;
        return Math.Max(0, (end - now).TotalSeconds);
    }
}
