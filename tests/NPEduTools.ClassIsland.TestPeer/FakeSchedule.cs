using ClassIsland.Shared.Enums;
using ClassIsland.Shared.IPC.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;

internal sealed class ScheduleFixture(string mode)
{
    private readonly DateTime _created = DateTime.Now;
    internal static readonly Guid PlanId = Guid.Parse("cc40d876-b600-4b64-924c-d845fd598cb9");
    internal static readonly Guid SubjectId = Guid.Parse("605d7089-fd1c-4c1f-a332-c50abeb5bf31");
    internal static readonly Guid OtherSubject = Guid.Parse("1fbe215f-1ee5-46fc-b727-726e19c66d60");
    internal static readonly Guid LayoutId = Guid.Parse("cdf0587b-b100-4c73-9659-7cb2b32bde98");
    internal Profile Data { get; } = new() { Id = Guid.Parse("409c367a-9b2c-4c29-9eb8-81c08b30a8a2") };
    // Keep the daily profile valid at any wall-clock hour; the clock probe has its own live anchor.
    internal TimeSpan Start => TimeSpan.FromHours(8);
    internal TimeSpan End => Start.Add(TimeSpan.FromMinutes(40));
    internal TimeSpan ClockStart => _created.TimeOfDay.Add(TimeSpan.FromMinutes(1));
    internal ClassPlan Plan
    {
        get
        {
            if (mode == "schedule-switch" && DateTime.Now - _created > TimeSpan.FromSeconds(3))
                Data.ClassPlans[PlanId].Classes[0].SubjectId = OtherSubject;
            return Data.ClassPlans[PlanId];
        }
    }
    internal void Initialize()
    {
        Data.Subjects[SubjectId] = new() { Name = "预演数学" };
        Data.Subjects[OtherSubject] = new() { Name = "临时英语" };
        Data.TimeLayouts[LayoutId] = new() { Name = "预演时间表", Layouts =
        [new() { StartTime = Start, EndTime = End, TimeType = 0 },
         new() { StartTime = End, EndTime = End.Add(TimeSpan.FromMinutes(10)), TimeType = 1 },
         new() { StartTime = End.Add(TimeSpan.FromMinutes(10)), EndTime = End.Add(TimeSpan.FromMinutes(50)), TimeType = 0 }] };
        Data.ClassPlans[PlanId] = new() { Name = "测试生效课表", TimeLayoutId = LayoutId,
            Classes = [new() { SubjectId = SubjectId }, new() { SubjectId = OtherSubject, IsEnabled = false }] };
        if (mode == "schedule-undefined") Data.Subjects.Remove(SubjectId);
    }
    internal TimeSpan Remaining => ClockStart - DateTime.Now.TimeOfDay + (mode == "schedule-clock" ? TimeSpan.FromMinutes(1) : TimeSpan.Zero);
}

internal sealed class ScheduledLessons(ScheduleFixture fixture) : IPublicLessonsService
{
    public bool IsTimerRunning => true;
    public ClassPlan? CurrentClassPlan { get => fixture.Plan; set => throw new NotSupportedException(); }
    public int CurrentSelectedIndex { get; set; } = -1;
    public Subject NextClassSubject { get; set; } = new() { Name = "预演数学" };
    public TimeLayoutItem NextBreakingTimeLayoutItem { get; set; } = new();
    public TimeLayoutItem NextClassTimeLayoutItem { get => new() { StartTime = fixture.ClockStart, EndTime = fixture.ClockStart.Add(TimeSpan.FromMinutes(40)) }; set => throw new NotSupportedException(); }
    public TimeSpan OnClassLeftTime { get => fixture.Remaining; set => throw new NotSupportedException(); }
    public TimeSpan OnBreakingTimeLeftTime { get; set; }
    public TimeState CurrentState { get; set; } = TimeState.None;
    public TimeLayoutItem CurrentTimeLayoutItem { get; set; } = new();
    public Subject? CurrentSubject { get; set; }
    public bool IsClassPlanEnabled { get; set; } = true;
    public bool IsClassPlanLoaded { get; set; } = true;
    public bool IsLessonConfirmed { get; set; }
    public ClassPlan? GetClassPlanByDate(DateTime date) => fixture.Plan;
}

internal sealed class ScheduledProfile(ScheduleFixture fixture) : IPublicProfileService
{
    public Profile Profile { get => fixture.Data; set => throw new NotSupportedException(); }
    public string CurrentProfilePath { get; set; } = "NPEduTools.Test.json";
    public bool IsCurrentProfileTrusted => true;
    public void SaveProfile() => throw new NotSupportedException();
    public void SaveProfile(string filename) => throw new NotSupportedException();
    public Guid? CreateTempClassPlan(Guid id, Guid? timeLayoutId = null, DateTime? enableDateTime = null) => throw new NotSupportedException();
    public Guid? CreateTempClassPlan(Guid id, Guid? timeLayoutId, DateTime? enableDateTime, bool createTempTimeLayout) => throw new NotSupportedException();
    public void ClearTempClassPlan() => throw new NotSupportedException();
    public void ConvertToStdClassPlan() => throw new NotSupportedException();
    public void ConvertToStdClassPlan(Guid id) => throw new NotSupportedException();
    public void SetupTempClassPlanGroup(Guid key, DateTime? expireTime = null) => throw new NotSupportedException();
    public void ClearTempClassPlanGroup() => throw new NotSupportedException();
}
