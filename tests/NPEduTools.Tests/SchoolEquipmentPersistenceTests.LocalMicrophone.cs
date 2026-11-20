using System.Text;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class SchoolEquipmentPersistenceTests
{
    private static JsonObject LocalChangePolicy(Fixture f, long revision, bool allowed)
    {
        var policy = f.Policy(revision);
        var equipment = policy["equipment"]!.AsObject();
        equipment["microphoneId"] = "mic-one";
        equipment["cameraId"] = "cam-one";
        equipment["allowLocalMicrophoneChange"] = allowed;
        return policy;
    }

    private static HostResponse ChangeMicrophone(NoiseService noise, StudyDeviceService cameras, string id,
        string secret = "school-password", Guid? token = null) =>
        noise.ManagementHandle(new(1, Guid.NewGuid(), "noise.management.command", NoiseManagement:
            new("replace-microphone", Secret: secret, DeviceId: id, PolicyToken: token ?? PolicyToken(cameras) ?? Guid.NewGuid())));

    private static Guid? PolicyToken(StudyDeviceService cameras) =>
        cameras.Handle(new(1, Guid.NewGuid(), "study.devices.refresh-microphone-policy")).StudyDevices!.MicrophoneChangeToken;

    [Fact]
    public void HostProtocolRequiresThePasswordTargetAndCurrentPolicyTokenTogether()
    {
        var command = new NoiseManagementCommand("replace-microphone", Secret: "school-password",
            DeviceId: "mic-two", PolicyToken: Guid.NewGuid());
        var request = new HostRequest(1, Guid.NewGuid(), "noise.management.command", NoiseManagement: command);
        Assert.Null(Protocol.Validate(request));
        foreach (var invalid in new[]
        {
            command with { Secret = null }, command with { DeviceId = null }, command with { DeviceId = "invalid\nendpoint" },
            command with { PolicyToken = null }, command with { PolicyToken = Guid.Empty },
            command with { Purpose = "maintenance" }, command with { Action = "verify" }
        })
            Assert.Equal("InvalidNoiseManagementCommand", Protocol.Validate(request with { NoiseManagement = invalid }));
    }

    private static void AssertBothSelectorsLocked(NoiseService noise, StudyDeviceService cameras)
    {
        var mic = noise.Snapshot();
        var otherMic = mic.SelectedDeviceId == "mic-one" ? "mic-two" : "mic-one";
        var micReply = noise.Handle(new(1, Guid.NewGuid(), "noise.command",
            Noise: new("select", mic.InstanceId, mic.Revision, otherMic)));
        Assert.Equal("EQUIPMENT_LOCKED", micReply.ErrorCode);
        var camera = cameras.Snapshot();
        var cameraReply = cameras.Handle(new(1, Guid.NewGuid(), "study.devices.select",
            StudyCamera: new("select", camera.InstanceId, camera.Revision, "cam-two")));
        Assert.Equal("EQUIPMENT_LOCKED", cameraReply.ErrorCode);
        Assert.True(cameras.Snapshot().Locked);
        Assert.Equal("cam-one", cameras.Snapshot().CameraId);
    }

    [Fact]
    public async Task UpgradingAPersistedLegacyUnlockRelocksBothDeviceSelectors()
    {
        await using var f = new Fixture();
        // This is the complete saved shape from the build that persisted a shared unlock.
        var legacy = new JsonObject
        {
            ["Scope"] = f.Identity.ToJsonString(), ["Revision"] = 1, ["Applied"] = 1, ["Locked"] = true,
            ["Microphone"] = "mic-one", ["Camera"] = "cam-one", ["Unlocked"] = true
        };
        File.WriteAllText(f.StorePath, legacy.ToJsonString());
        var migrated = new SchoolManagementService(f.Noise, f.Cameras, f.DirectoryPath);
        AssertBothSelectorsLocked(f.Noise, f.Cameras);
        migrated.BindSchool(f.Identity.ToJsonString());
        migrated.ApplySchool(f.Policy(1));
        Assert.Equal("EQUIPMENT_LOCKED", f.Noise.ManagementHandle(new(1, Guid.NewGuid(),
            "noise.management.command", NoiseManagement: new("unlock", Secret: "school-password"))).ErrorCode);
        Assert.False(migrated.ObserveSchool()["unlocked"]!.GetValue<bool>());
        AssertBothSelectorsLocked(f.Noise, f.Cameras);
    }

    [Fact]
    public async Task OneAuthorizedSelectionUsesTheSchoolCredentialAndLeavesBothSelectorsLocked()
    {
        await using var f = new Fixture();
        var denied = ChangeMicrophone(f.Noise, f.Cameras, "mic-two");
        Assert.Equal("EQUIPMENT_LOCKED", denied.ErrorCode);
        Assert.Equal("mic-one", f.Noise.Snapshot().SelectedDeviceId);
        var legacyReceipt = f.Management.ObserveSchool();
        Assert.False(legacyReceipt.ContainsKey("supportsLocalMicrophoneChange"));
        Assert.False(legacyReceipt.ContainsKey("localMicrophoneOverride"));

        var policy = LocalChangePolicy(f, 2, true);
        f.Management.ApplySchool(policy);
        var wrong = ChangeMicrophone(f.Noise, f.Cameras, "mic-two", "incorrect-password");
        Assert.Equal("MANAGEMENT_SECRET_INVALID", wrong.ErrorCode);
        Assert.Equal("mic-one", f.Noise.Snapshot().SelectedDeviceId);
        var missing = ChangeMicrophone(f.Noise, f.Cameras, "disconnected-mic");
        Assert.Equal("EQUIPMENT_UNAVAILABLE", missing.ErrorCode);
        Assert.Equal("mic-one", f.Noise.Snapshot().SelectedDeviceId);

        var token = Assert.IsType<Guid>(PolicyToken(f.Cameras));
        var oldClient = f.Cameras.Handle(new(1, Guid.NewGuid(), "study.devices.refresh"));
        Assert.Null(oldClient.StudyDevices!.MicrophoneChangeToken);
        var accepted = ChangeMicrophone(f.Noise, f.Cameras, "mic-two", token: token);
        Assert.Equal("Succeeded", accepted.Outcome);
        Assert.Equal("EQUIPMENT_POLICY_CHANGED", ChangeMicrophone(f.Noise, f.Cameras, "mic-one", token: token).ErrorCode);
        AssertBothSelectorsLocked(f.Noise, f.Cameras);
        var observation = f.Management.ObserveSchool();
        Assert.Equal("mic-two", observation["selectedMicrophoneId"]!.GetValue<string>());
        Assert.True(observation["supportsLocalMicrophoneChange"]!.GetValue<bool>());
        Assert.Equal("mic-two", observation["localMicrophoneOverride"]!.GetValue<string>());
        Assert.False(observation["unlocked"]!.GetValue<bool>());
        Assert.Equal(2L, observation["appliedRevision"]!.GetValue<long>());
        Assert.DoesNotContain("school-password", File.ReadAllText(f.StorePath));
    }

    [Fact]
    public async Task LocalMicrophoneChoiceSurvivesRestartUntilAnotherSchoolPolicySupersedesIt()
    {
        await using var f = new Fixture();
        var policy = LocalChangePolicy(f, 2, true);
        f.Management.ApplySchool(policy);
        Assert.Equal("Succeeded", ChangeMicrophone(f.Noise, f.Cameras, "mic-two").Outcome);
        var oldToken = Assert.IsType<Guid>(PolicyToken(f.Cameras));
        await f.Noise.DisposeAsync();
        var captureOpened = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var noise = new NoiseService(id => { captureOpened.TrySetResult(id); return new SilentCapture(); },
            () => [new("mic-one", "one"), new("mic-two", "two")], directory: f.DirectoryPath,
            management: new NoiseManagementStore(f.DirectoryPath));
        var cameras = new StudyDeviceService(() => [new("cam-one", "one"), new("cam-two", "two")], f.DirectoryPath);
        var reloaded = new SchoolManagementService(noise, cameras, f.DirectoryPath);
        // The first scheduled session can start before this host receives any school response.
        var session = Guid.NewGuid();
        Assert.Equal("Accepted", noise.StartScheduled(session).Outcome);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal("mic-two", await captureOpened.Task.WaitAsync(deadline.Token));
        noise.StopScheduled(session);
        while (noise.CaptureBusy) await Task.Delay(10, deadline.Token);
        reloaded.BindSchool(f.Identity.ToJsonString());
        reloaded.ApplySchool(policy);
        Assert.Equal("mic-two", reloaded.ObserveSchool()["selectedMicrophoneId"]!.GetValue<string>());
        Assert.Equal("EQUIPMENT_POLICY_CHANGED", ChangeMicrophone(noise, cameras, "mic-one", token: oldToken).ErrorCode);
        AssertBothSelectorsLocked(noise, cameras);

        reloaded.ApplySchool(LocalChangePolicy(f, 3, false));
        var reset = reloaded.ObserveSchool();
        Assert.Equal("mic-one", reset["selectedMicrophoneId"]!.GetValue<string>());
        Assert.Equal(3L, reset["appliedRevision"]!.GetValue<long>());
        Assert.True(reset["supportsLocalMicrophoneChange"]!.GetValue<bool>());
        Assert.True(reset.ContainsKey("localMicrophoneOverride"));
        Assert.Null(reset["localMicrophoneOverride"]);
        Assert.Equal("EQUIPMENT_LOCKED", ChangeMicrophone(noise, cameras, "mic-two").ErrorCode);
        AssertBothSelectorsLocked(noise, cameras);
    }

    [Fact]
    public async Task AnUnpluggedPolicyTargetCanBeReplacedBeforeTheRevisionIsAcknowledged()
    {
        await using var f = new Fixture();
        var policy = LocalChangePolicy(f, 2, true);
        policy["equipment"]!["microphoneId"] = "unplugged-policy-mic";
        f.Management.ApplySchool(policy);
        var failed = f.Management.ObserveSchool();
        Assert.Equal(1L, failed["appliedRevision"]!.GetValue<long>());
        Assert.Equal("EQUIPMENT_UNAVAILABLE", failed["error"]!.GetValue<string>());

        Assert.Equal("Succeeded", ChangeMicrophone(f.Noise, f.Cameras, "mic-two").Outcome);
        f.Management.ApplySchool(policy);
        var fixedReport = f.Management.ObserveSchool();
        Assert.Null(fixedReport["error"]);
        Assert.Equal(2L, fixedReport["appliedRevision"]!.GetValue<long>());
        Assert.Equal("mic-two", fixedReport["localMicrophoneOverride"]!.GetValue<string>());
        Assert.Equal("mic-two", fixedReport["selectedMicrophoneId"]!.GetValue<string>());
        AssertBothSelectorsLocked(f.Noise, f.Cameras);
    }

    [Fact]
    public async Task CameraOnlyPolicyRevisionsKeepTheLocalMicrophoneUntilSchoolExplicitlyReselectsIt()
    {
        await using var f = new Fixture();
        var initial = LocalChangePolicy(f, 2, true);
        initial["equipment"]!["microphonePolicyRevision"] = 2L;
        f.Management.ApplySchool(initial);
        Assert.Equal("Succeeded", ChangeMicrophone(f.Noise, f.Cameras, "mic-two").Outcome);
        var openDialogToken = Assert.IsType<Guid>(PolicyToken(f.Cameras));

        var cameraOnly = LocalChangePolicy(f, 4, true);
        cameraOnly["equipment"]!["cameraId"] = "cam-two";
        cameraOnly["equipment"]!["microphonePolicyRevision"] = 2L;
        f.Management.ApplySchool(cameraOnly);
        var observed = f.Management.ObserveSchool();
        Assert.Equal(4L, observed["appliedRevision"]!.GetValue<long>());
        Assert.Equal("cam-two", observed["selectedCameraId"]!.GetValue<string>());
        Assert.Equal("mic-two", observed["selectedMicrophoneId"]!.GetValue<string>());
        Assert.Equal("mic-two", observed["localMicrophoneOverride"]!.GetValue<string>());
        Assert.Equal("EQUIPMENT_POLICY_CHANGED",
            ChangeMicrophone(f.Noise, f.Cameras, "mic-one", token: openDialogToken).ErrorCode);

        var reselect = cameraOnly.DeepClone().AsObject();
        reselect["equipment"]!["revision"] = 5L;
        reselect["equipment"]!["microphonePolicyRevision"] = 5L;
        f.Management.ApplySchool(reselect);
        observed = f.Management.ObserveSchool();
        Assert.Equal(5L, observed["appliedRevision"]!.GetValue<long>());
        Assert.Equal("cam-two", observed["selectedCameraId"]!.GetValue<string>());
        Assert.Equal("mic-one", observed["selectedMicrophoneId"]!.GetValue<string>());
        Assert.Null(observed["localMicrophoneOverride"]);
    }

    [Fact]
    public async Task AStaleDialogCannotApplyItsChoiceAfterPolicyOrBindingChanges()
    {
        await using var f = new Fixture();
        f.Management.ApplySchool(LocalChangePolicy(f, 2, true));
        var firstToken = Assert.IsType<Guid>(PolicyToken(f.Cameras));
        f.Management.ApplySchool(LocalChangePolicy(f, 3, true));
        Assert.Equal("EQUIPMENT_POLICY_CHANGED", ChangeMicrophone(f.Noise, f.Cameras, "mic-two", token: firstToken).ErrorCode);
        var tokenFromOldSchool = Assert.IsType<Guid>(PolicyToken(f.Cameras));

        var anotherBinding = new JsonObject { ["deviceId"] = Guid.NewGuid().ToString() };
        f.Management.BindSchool(anotherBinding.ToJsonString());
        var policy = LocalChangePolicy(f, 1, true);
        policy["identity"] = anotherBinding;
        f.Management.ApplySchool(policy);
        Assert.Equal("EQUIPMENT_POLICY_CHANGED", ChangeMicrophone(f.Noise, f.Cameras, "mic-two", token: tokenFromOldSchool).ErrorCode);
        Assert.Equal("mic-one", f.Noise.Snapshot().SelectedDeviceId);
        Assert.Null(f.Management.ObserveSchool()["localMicrophoneOverride"]);
        AssertBothSelectorsLocked(f.Noise, f.Cameras);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfirmedMicrophonePreferenceRespectsTheNextSchoolPolicy(bool revoke)
    {
        await using var f = new Fixture();
        var policy = LocalChangePolicy(f, 2, true);
        f.Management.ApplySchool(policy);
        string preference = Path.Combine(f.DirectoryPath, "noise-microphone.json");
        using (File.Open(preference, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var change = ChangeMicrophone(f.Noise, f.Cameras, "mic-two");
            Assert.Equal("EQUIPMENT_CHANGE_UNCONFIRMED", change.ErrorCode);
            var pending = f.Management.ObserveSchool();
            Assert.Equal("EQUIPMENT_STORE_UNAVAILABLE", pending["error"]!.GetValue<string>());
            Assert.Equal("mic-two", pending["localMicrophoneOverride"]!.GetValue<string>());
            Assert.Equal("mic-one", pending["selectedMicrophoneId"]!.GetValue<string>());
        }
        f.Management.ApplySchool(revoke ? LocalChangePolicy(f, 3, false) : policy);
        var recovered = f.Management.ObserveSchool();
        Assert.Null(recovered["error"]);
        Assert.Equal(revoke ? "mic-one" : "mic-two", recovered["selectedMicrophoneId"]!.GetValue<string>());
        Assert.Equal(revoke ? null : "mic-two", recovered["localMicrophoneOverride"]?.GetValue<string>());
        AssertBothSelectorsLocked(f.Noise, f.Cameras);
    }

    [Fact]
    public async Task WireReceiptRetainsTheLocalReplacementAndCameraWhenAllEndpointIdsAreMaximal()
    {
        await using var f = new Fixture();
        string schoolMic = new('甲', 2048), localMic = new('乙', 2048), camera = new('摄', 2048);
        string micName = new('声', 512), cameraName = new('像', 512);
        f.Microphones = Enumerable.Range(0, 27).Select(i => new NoiseDevice(new string('音', 500) + i, "其他输入"))
            .Concat([new(schoolMic, micName), new(localMic, micName)]).ToArray();
        f.CameraDevices = Enumerable.Range(0, 27).Select(i => new StudyCameraDevice(new string('像', 500) + i, "其他摄像头"))
            .Append(new(camera, cameraName)).ToArray();
        var policy = LocalChangePolicy(f, 2, true);
        policy["equipment"]!["microphoneId"] = schoolMic;
        policy["equipment"]!["cameraId"] = camera;
        f.Management.ApplySchool(policy);
        Assert.Equal(schoolMic, f.Noise.Snapshot().SelectedDeviceId);
        Assert.Equal("Succeeded", ChangeMicrophone(f.Noise, f.Cameras, localMic).Outcome);
        var observed = f.Management.ObserveSchool();
        SchoolInventoryBudgetTests.AddRuntimeContext(observed);
        Assert.InRange(Encoding.UTF8.GetByteCount(observed.ToJsonString()), 1, 65536);
        Assert.Null(observed["error"]);
        Assert.Equal(localMic, observed["localMicrophoneOverride"]!.GetValue<string>());
        Assert.Equal(localMic, observed["selectedMicrophoneId"]!.GetValue<string>());
        Assert.Equal(camera, observed["selectedCameraId"]!.GetValue<string>());
        Assert.Contains(observed["microphones"]!.AsArray(), d => d!["id"]!.GetValue<string>() == localMic);
        Assert.Contains(observed["cameras"]!.AsArray(), d => d!["id"]!.GetValue<string>() == camera);
    }

    [Fact]
    public async Task ALocalChangeDuringScheduledCaptureIsRefusedWithoutStoppingOrQueuingIt()
    {
        int starts = 0;
        await using var f = new Fixture(_ => { starts++; return new SilentCapture(); });
        var policy = LocalChangePolicy(f, 2, true);
        f.Management.ApplySchool(policy);
        var session = Guid.NewGuid();
        Assert.Equal("Accepted", f.Noise.StartScheduled(session).Outcome);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (f.Noise.Snapshot().State != "Active") await Task.Delay(10, timeout.Token);
        Assert.Equal("EQUIPMENT_PENDING", ChangeMicrophone(f.Noise, f.Cameras, "mic-two").ErrorCode);
        var active = f.Noise.Snapshot();
        Assert.Equal(session, active.SessionId);
        Assert.Equal("Active", active.State);
        Assert.Equal("mic-one", active.SelectedDeviceId);
        f.Noise.StopScheduled(session);
        while (f.Noise.CaptureBusy) await Task.Delay(10, timeout.Token);
        f.Management.ApplySchool(policy);
        Assert.Equal("mic-one", f.Management.ObserveSchool()["selectedMicrophoneId"]!.GetValue<string>());
        Assert.Equal(1, starts);
        Assert.Equal("Succeeded", ChangeMicrophone(f.Noise, f.Cameras, "mic-two").Outcome);
    }

    private sealed class SilentCapture : INoiseCapture
    {
        public string DeviceName => "Synthetic classroom input";
        public event Action<NoiseFrame>? Frame { add { } remove { } }
        public event Action<string>? Failed { add { } remove { } }
        public void Start() { }
        public void Dispose() { }
    }
}
