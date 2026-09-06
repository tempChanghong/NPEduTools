namespace NPEduTools.ClassIsland.Admin;

public enum LaunchChoice { Existing, OfferAdministratorRestart, ScheduledTask, Ordinary, Reject }

public static class ClassIslandLaunchPolicy
{
    public static LaunchChoice Choose(string processState, string taskState) => (processState, taskState) switch
    {
        ("Administrator", _) => LaunchChoice.Existing,
        ("Standard", "Enabled") => LaunchChoice.OfferAdministratorRestart,
        ("Standard", _) => LaunchChoice.Existing,
        ("Stopped", "Enabled") => LaunchChoice.ScheduledTask,
        ("Stopped", "Missing" or "Disabled") => LaunchChoice.Ordinary,
        _ => LaunchChoice.Reject
    };
}
