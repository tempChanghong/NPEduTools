#nullable enable
using ClassIsland.Shared.Enums;
using ClassIsland.Shared.IPC.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;
using dotnetCampus.Ipc.CompilerServices.Attributes;

namespace NPEduTools.Integrations.ClassIsland;

// IpcProxyConfigs has LOWER precedence than IpcPublic(IgnoresIpcException = true).
// A generated shape is required to override the upstream contract safely.
// No method in this declaration is executed locally.
[IpcShape(typeof(IPublicLessonsService), IgnoresIpcException = false, Timeout = 2000)]
public class StrictLessonsShape : IPublicLessonsService
{
    [IpcProperty(IgnoresIpcException = false, Timeout = 2000)]
    public bool IsTimerRunning => throw new NotSupportedException();
    public ClassPlan? CurrentClassPlan { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    [IpcProperty(IgnoresIpcException = false, Timeout = 2000)]
    public int CurrentSelectedIndex { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public Subject NextClassSubject { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public TimeLayoutItem NextBreakingTimeLayoutItem { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public TimeLayoutItem NextClassTimeLayoutItem { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public TimeSpan OnClassLeftTime { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public TimeSpan OnBreakingTimeLeftTime { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    [IpcProperty(IgnoresIpcException = false, Timeout = 2000)]
    public TimeState CurrentState { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public TimeLayoutItem CurrentTimeLayoutItem { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    [IpcProperty(IgnoresIpcException = false, Timeout = 2000)]
    public Subject? CurrentSubject { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    [IpcProperty(IgnoresIpcException = false, Timeout = 2000)]
    public bool IsClassPlanEnabled { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    [IpcProperty(IgnoresIpcException = false, Timeout = 2000)]
    public bool IsClassPlanLoaded { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public bool IsLessonConfirmed { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public ClassPlan? GetClassPlanByDate(DateTime date) => throw new NotSupportedException();
}
