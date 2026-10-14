using System.Text.Json;
using NPEduTools.App;
using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed class NoiseCalibrationStatusTests
{
    private static readonly Guid Instance = Guid.NewGuid();
    private static readonly NoiseState Noise = new(Instance, 7, "Idle", "fixture", null, null, null, null, "Waiting", null, []);
    private static NoiseCalibrationStatus Status(string state, string method = "Quick", string? reason = null) => new(Instance, 7, state, method, reason);
    [Theory]
    [InlineData("Uncalibrated", "None", "未校准")]
    [InlineData("Saved", "Quick", "已快速校准")]
    [InlineData("Ready", "Quick", "已快速校准")]
    [InlineData("Saved", "ManualReference", "已参考校准")]
    [InlineData("Ready", "UsbReferenceBeta", "已联机校准 (Beta)")]
    [InlineData("Invalidated", "Quick", "校准已失效")]
    [InlineData("Unavailable", "Reference", "校准不可用")]
    [InlineData("future-state", "Quick", "校准状态待确认")]
    [InlineData("Ready", "future-method", "校准状态待确认")]
    public void Labels_keep_methods_and_unknown_states_distinct(string state, string method, string label) =>
        Assert.Equal(label, NoiseCalibrationPresentation.Describe(Status(state, method), Noise).Text);
    [Fact]
    public void Old_missing_mismatched_or_stale_evidence_is_not_claimed_calibrated()
    {
        var ready = Status("Ready");
        foreach (var status in new NoiseCalibrationStatus?[] { null, ready with { InstanceId = Guid.Empty },
            ready with { InstanceId = Guid.NewGuid() }, ready with { NoiseRevision = 8 } })
            Assert.Equal("校准状态待确认", NoiseCalibrationPresentation.Describe(status, Noise).Text);
        Assert.Equal("校准状态待确认", NoiseCalibrationPresentation.Describe(ready, null).Text);
        Assert.Equal("校准状态待确认", NoiseCalibrationPresentation.Describe(ready, Noise, false).Text);
    }
    [Fact]
    public void Saved_and_applied_are_distinguished_and_quick_is_not_spl()
    {
        var quick = NoiseCalibrationPresentation.Describe(Status("Saved"), Noise);
        Assert.Contains("dBFS", quick.Detail); Assert.Contains("没有转换", quick.Detail);
        Assert.Contains("下次采集", quick.Detail); Assert.Equal("Muted", quick.Tone);
        var ready = NoiseCalibrationPresentation.Describe(Status("Ready", "ManualReference"), Noise);
        Assert.Contains("估算 dB(A)", ready.Detail); Assert.Contains("输入设置已核对", ready.Detail); Assert.Equal("Normal", ready.Tone);
    }
    [Theory]
    [InlineData("DEVICE_CHANGED", "麦克风")]
    [InlineData("INPUT_SETTINGS_CHANGED", "输入音量")]
    [InlineData("INPUT_SETTINGS_UNAVAILABLE", "无法核对")]
    [InlineData("SCHOOL_CONFIGURATION_CHANGED", "学校配置")]
    public void Invalidation_has_actionable_explanation(string reason, string text)
    {
        var label = NoiseCalibrationPresentation.Describe(Status("Invalidated", reason: reason), Noise);
        Assert.Contains(text, label.Detail); Assert.Equal("Warning", label.Tone);
    }
    [Theory]
    [InlineData("study.status.calibration", "study.status.photos")]
    [InlineData("manual.study.status.calibration", "manual.study.status")]
    [InlineData("noise.status.calibration", "noise.status")]
    [InlineData("noise.devices.calibration", "noise.devices")]
    public void Opt_in_statuses_keep_ordinary_read_only_validation(string alias, string original)
    {
        var request = new HostRequest(1, Guid.NewGuid(), alias);
        Assert.Null(Protocol.Validate(request)); Assert.Equal(original, NoiseCalibrationStatusContract.BaseCapability(alias));
        var malformed = request with { Noise = new("select", Instance, 7, "mic") };
        Assert.Equal(Protocol.Validate(malformed with { Capability = original }), Protocol.Validate(malformed));
        var legacy = new HostResponse(1, request.RequestId, "Succeeded", null, "fixture", Noise: Noise);
        Assert.DoesNotContain("noiseCalibrationStatus", JsonSerializer.Serialize(legacy, Protocol.Json));
        Assert.Null(JsonSerializer.Deserialize<HostResponse>(JsonSerializer.Serialize(legacy, Protocol.Json), Protocol.Json)!.NoiseCalibrationStatus);
    }
    private sealed class Clock : TimeProvider
    { public long Ms; public override long TimestampFrequency => 1000; public override long GetTimestamp() => Ms; }
    [Fact]
    public async Task Older_host_falls_back_and_is_not_probed_each_poll()
    {
        var clock = new Clock(); var reader = new NoiseCalibrationRead(clock); var calls = new List<string>();
        Task<HostResponse> Send(HostRequest request)
        {
            calls.Add(request.Capability);
            return Task.FromResult(NoiseCalibrationStatusContract.BaseCapability(request.Capability) is not null
                ? new HostResponse(1, request.RequestId, "Rejected", "UnknownCapability", "old host")
                : new HostResponse(1, request.RequestId, "Succeeded", null, "normal", Noise: Noise));
        }
        var request = new HostRequest(1, Guid.NewGuid(), "noise.status");
        var first = await reader.RequestAsync(request, Send);
        Assert.NotNull(first.Noise); Assert.Null(first.NoiseCalibrationStatus);
        await reader.RequestAsync(request, Send);
        Assert.Equal(new[] { "noise.status.calibration", "noise.status", "noise.status" }, calls);
        clock.Ms = 60000; await reader.RequestAsync(request, Send);
        Assert.Equal(2, calls.Count(c => c == "noise.status.calibration"));
    }
    [Fact]
    public async Task Failure_is_not_retried_as_a_legacy_read()
    {
        var reader = new NoiseCalibrationRead(); int calls = 0;
        var reply = await reader.RequestAsync(new(1, Guid.NewGuid(), "noise.status"), request =>
        { calls++; return Task.FromResult(new HostResponse(1, request.RequestId, "Rejected", "NoiseConfigurationUnavailable", "fixture")); });
        Assert.Equal(1, calls); Assert.Equal("NoiseConfigurationUnavailable", reply.ErrorCode);
    }
}
