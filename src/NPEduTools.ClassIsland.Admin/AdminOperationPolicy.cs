namespace NPEduTools.ClassIsland.Admin;

/// <summary>Fixed internal commands dispatched by a Host that already owns the runtime switch lease.</summary>
public static class AdminOperationPolicy
{
    public static string StartupAction(bool enabled) => enabled ? "runtime-startup-enable" : "runtime-startup-disable";

    public static bool RequiresReservation(string action) => action is not
        ("status" or "inspect" or "runtime-status" or "runtime-close" or "runtime-startup-enable" or "runtime-startup-disable" or "runtime-launch-mode");

    // This only selects enable/disable. ScheduledStartup still checks administrator identity,
    // session, task fingerprint, ownership and the ClassIsland operation mutex.
    public static string ScheduledAction(string action) => action switch
    {
        "runtime-startup-enable" => "enable",
        "runtime-startup-disable" => "disable",
        "runtime-launch-mode" => "launch-mode",
        _ => action
    };
}
