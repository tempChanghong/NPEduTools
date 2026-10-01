using NPEduTools.Contracts;

namespace NPEduTools.Host;

/// <summary>Shared by local and remote mode changes. Caller persists the recovery baseline first.</summary>
public static class ClassroomStartupCoordinator
{
    public static async Task<ClassroomStartupSnapshot> ApplyAsync(ClassroomStartupSnapshot before,
        bool classIsland, bool examAware, Func<bool, Task> setClassIsland, Func<bool, Task> setExamAware,
        Func<Task<ClassroomStartupSnapshot>> observe, Func<Task> beforeDispatch)
    {
        // Enable the destination first. A failure keeps the caller's durable recovery point.
        if (classIsland && !before.ClassIslandEnabled) { await beforeDispatch(); await setClassIsland(true); }
        if (examAware && !before.ExamAwareEnabled) { await beforeDispatch(); await setExamAware(true); }
        if (!classIsland && before.ClassIslandEnabled) { await beforeDispatch(); await setClassIsland(false); }
        if (!examAware && before.ExamAwareEnabled) { await beforeDispatch(); await setExamAware(false); }
        await beforeDispatch();
        var after = await observe();
        ClassroomModeService.RequireSamePrograms(before, after);
        if (after.ClassIslandEnabled != classIsland || after.ExamAwareEnabled != examAware)
            throw new InvalidOperationException("读回结果与目标不一致，请核实自启动设置。");
        return after;
    }
}
