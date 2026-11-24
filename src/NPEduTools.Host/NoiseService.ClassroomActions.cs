using NPEduTools.Contracts;

namespace NPEduTools.Host;

public sealed partial class NoiseService
{
    private ClassroomActionPolicyState? _classroomPolicy;
    private string? _classroomCredentialScope;
    private Func<bool>? _registeredUsbPresent;
    private sealed record UsbObservation(ClassroomActionPolicyState Policy, bool Present);

    internal void SetClassroomActionPolicy(string? version, string? stopMode, string? exitMode, Func<bool>? usbPresent,
        string? credentialScope = null)
    {
        lock (_sync)
        {
            var policy = version is null ? null : new ClassroomActionPolicyState(version, stopMode ?? "deny", exitMode ?? "deny");
            _registeredUsbPresent = usbPresent;
            if (policy == _classroomPolicy && credentialScope == _classroomCredentialScope) return;
            _classroomPolicy = policy;
            _classroomCredentialScope = credentialScope;
            management?.InvalidateTickets();
            _exitReservation = null;
        }
    }

    internal NoiseProtectionState ClassroomProtection()
    {
        lock (_sync)
        {
            var status = Protection();
            return _classroomPolicy is null ? status : status with
                { ClassroomPolicy = _classroomPolicy, Configured = ClassroomPasswordReady };
        }
    }

    private bool ClassroomPasswordReady => management is { ClassroomPasswordReady: true } &&
        (_classroomCredentialScope is null || management.MatchesSchool(_classroomCredentialScope));

    private string? ClassroomRule(string purpose) => purpose switch
    {
        "stop" when ScheduledLive => _classroomPolicy?.StopScheduled,
        "maintenance" => _classroomPolicy?.ExitHost,
        _ => null
    };

    private static bool NeedsUsb(string rule, string? secret) =>
        rule is "usb" or "both" || rule == "either" && secret is null;

    // Opening a removable drive may stall. Only probe for a request with matching context,
    // outside the capture lock; validate its snapshot again in the action's critical section.
    private UsbObservation? ReadActionUsb(HostRequest request)
    {
        if (request.NoiseAuthorization is null && request.NoiseManagement is not { Action: "authorize", ClassroomPolicyVersion: not null })
            return null;
        ClassroomActionPolicyState policy;
        Func<bool> probe;
        lock (_sync)
        {
            if (_classroomPolicy is not { } current || _registeredUsbPresent is not { } read) return null;
            if (request.NoiseManagement is { Action: "authorize" } c)
            {
                if (c.ClassroomPolicyVersion != current.Version || c.InstanceId != _instance || c.SessionId != _session?.Id ||
                    ClassroomRule(c.Purpose!) is not { } rule || !NeedsUsb(rule, c.Secret)) return null;
            }
            else if (management?.RequiresUsb(request.NoiseAuthorization, request.RequestId, current.Version) != true)
                return null;
            policy = current;
            probe = read;
        }
        try { return new(policy, probe()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return new(policy, false); }
    }

    private bool UsbConfirmed(UsbObservation? usb) => usb is { Present: true } && ReferenceEquals(usb.Policy, _classroomPolicy);

    private HostResponse IssueClassroomTicket(HostRequest request, NoiseManagementCommand c, string rule, UsbObservation? usb)
    {
        if (rule == "deny") return ManagementReply(request, "CLASSROOM_ACTION_DENIED");
        if (_classroomPolicy is null || c.ClassroomPolicyVersion != _classroomPolicy.Version)
            return ManagementReply(request, "CLASSROOM_POLICY_CHANGED");
        if (c.InstanceId != _instance || c.SessionId != _session?.Id)
            return ManagementReply(request, "NoiseStateChanged");
        if (management is null) return ManagementReply(request, "MANAGEMENT_UNSUPPORTED");
        if (rule is not ("allow" or "usb" or "password" or "either" or "both"))
            return ManagementReply(request, "CLASSROOM_ACTION_DENIED");

        bool usbRequired = NeedsUsb(rule, c.Secret);
        bool passwordRequired = rule is "password" or "both" || rule == "either" && c.Secret is not null;
        if (usbRequired && !UsbConfirmed(usb)) return ManagementReply(request, "CLASSROOM_USB_REQUIRED");
        if (passwordRequired)
        {
            if (management.Source != "School") return ManagementReply(request, "SCHOOL_MANAGEMENT_REQUIRED");
            if (!ClassroomPasswordReady) return ManagementReply(request, "MANAGEMENT_NOT_CONFIGURED");
            if (c.Secret is null) return ManagementReply(request, "MANAGEMENT_REQUIRED");
            if (management.VerifyCalibration(c.Secret) is { } error) return ManagementReply(request, error);
        }
        var grant = management.IssueAfterVerification(_instance, _session?.Id, c.Purpose!, c.TargetRequestId!.Value,
            _classroomPolicy.Version, usbRequired, _revision, passwordRequired);
        return ManagementReply(request, grant.Error, grant.Ticket);
    }

    private string? CheckClassroomTicket(HostRequest request, string purpose, string rule, UsbObservation? usb)
    {
        if (rule == "deny") return "CLASSROOM_ACTION_DENIED";
        if (management is null || _classroomPolicy is null) return "MANAGEMENT_REQUIRED";
        string version = _classroomPolicy.Version;
        if (management.RequiresUsb(request.NoiseAuthorization, request.RequestId, version) && !UsbConfirmed(usb))
            return "CLASSROOM_USB_REQUIRED";
        return management.CheckTicket(request.NoiseAuthorization, _instance, _session?.Id, purpose, request.RequestId, version, _revision,
            consume: request.Capability != "noise.management.prepare-exit") ? null : "MANAGEMENT_REQUIRED";
    }
}
