using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Tests;

public sealed class StudyPresentationReporterTests
{
    private static StudyPresentationPulse Pulse(Guid? instance = null, long sequence = 1) =>
        new(instance ?? Guid.NewGuid(), Guid.NewGuid(), sequence, new string('a', 64),
            new(new(2026, 10, 23, 19, 15, 0, TimeSpan.Zero), new(2026, 10, 23, 21, 30, 0, TimeSpan.Zero)),
            "Active", "Visible", 0);
    private static HostResponse Epoch(HostRequest request, StudyPresentationPulse pulse, Guid epoch) =>
        new(1, request.RequestId, "Succeeded", null, "epoch", PresentationEpoch: new(pulse.InstanceId, epoch));
    private static HostResponse Ok(HostRequest request) => new(1, request.RequestId, "Succeeded", null, "ok");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Probe_is_read_only_and_resamples_frame_age_after_negotiation(bool manual)
    {
        var reporter = new StudyPresentationReporter(); var original = Pulse(); var epoch = Guid.NewGuid();
        var requests = new List<HostRequest>(); bool probeCompleted = false; int samples = 0;
        StudyPresentationPulse Sample() => original with { Sequence = ++samples,
            State = probeCompleted ? "Stale" : "Visible", RenderAgeMs = probeCompleted ? null : 0 };
        Task<HostResponse> Send(HostRequest request, CancellationToken token)
        {
            Assert.Null(Protocol.Validate(request)); Assert.False(Protocol.NoiseInterruption(request)); requests.Add(request);
            if (request.Capability.EndsWith(".epoch", StringComparison.Ordinal))
            {
                Assert.Null(request.StudyPresentation); probeCompleted = true;
                return Task.FromResult(Epoch(request, original, epoch));
            }
            return Task.FromResult(Ok(request));
        }
        await reporter.ReportAsync(Sample, Send, manual, default);
        await reporter.ReportAsync(Sample, Send, manual, default);
        string prefix = manual ? "manual.study.presentation" : "study.presentation";
        Assert.Equal(new[] { prefix + ".epoch", prefix, prefix }, requests.Select(r => r.Capability));
        Assert.All(requests.Skip(1), r => {
            Assert.Equal("Stale", r.StudyPresentation!.State); Assert.Null(r.StudyPresentation.RenderAgeMs);
            Assert.Equal(epoch, r.StudyPresentation.ObservationEpoch);
        });
        Assert.Equal(2, requests[1].StudyPresentation!.Sequence);
        Assert.Equal(3, requests[2].StudyPresentation!.Sequence);
    }

    [Fact]
    public async Task Old_Host_explicit_unknown_capability_falls_back_to_old_strict_pulse_shape()
    {
        var reporter = new StudyPresentationReporter(); var pulse = Pulse(); var requests = new List<HostRequest>();
        Task<HostResponse> Send(HostRequest request, CancellationToken token)
        {
            requests.Add(request);
            if (request.Capability.EndsWith(".epoch", StringComparison.Ordinal))
            {
                // This is the exact old validation outcome for a new capability without new payload.
                Assert.Null(request.StudyPresentation);
                return Task.FromResult(new HostResponse(1, request.RequestId, "Rejected", "UnknownCapability", "old host"));
            }
            var json = JsonSerializer.Serialize(request.StudyPresentation, Protocol.Json);
            Assert.DoesNotContain("observationEpoch", json);
            Assert.NotNull(JsonSerializer.Deserialize<LegacyPulse>(json, Protocol.Json));
            return Task.FromResult(Ok(request));
        }
        await reporter.ReportAsync(() => pulse, Send, false, default);
        await reporter.ReportAsync(() => pulse with { Sequence = 2 }, Send, false, default);
        Assert.Equal(3, requests.Count); Assert.Null(requests[1].StudyPresentation!.ObservationEpoch);
    }

    [Theory]
    [InlineData("STUDY_UNSUPPORTED")]
    [InlineData("STUDY_STATE_CHANGED")]
    [InlineData("InvalidStudyPresentation")]
    public async Task Other_negotiation_failures_never_fall_back_to_unfenced_observations(string code)
    {
        var reporter = new StudyPresentationReporter(); var pulse = Pulse(); int sent = 0;
        await reporter.ReportAsync(() => pulse, (request, token) => {
            sent++; Assert.EndsWith(".epoch", request.Capability);
            return Task.FromResult(new HostResponse(1, request.RequestId, "Rejected", code, "error"));
        }, false, default);
        Assert.Equal(1, sent);
    }

    [Fact]
    public async Task Retired_epoch_requeries_next_tick_without_downgrading_or_reusing_old_sample()
    {
        var reporter = new StudyPresentationReporter(); var pulse = Pulse(); var epoch = Guid.NewGuid();
        var requests = new List<HostRequest>(); int queries = 0; long sequence = 0;
        Task<HostResponse> Send(HostRequest request, CancellationToken token)
        {
            requests.Add(request);
            if (request.Capability.EndsWith(".epoch", StringComparison.Ordinal))
            {
                queries++;
                return Task.FromResult(queries == 2
                    ? new HostResponse(1, request.RequestId, "Rejected", "UnknownCapability", "downgraded")
                    : Epoch(request, pulse, epoch));
            }
            return Task.FromResult(requests.Count == 2
                ? new HostResponse(1, request.RequestId, "Rejected", "STUDY_OBSERVATION_CHANGED", "rotate") : Ok(request));
        }
        StudyPresentationPulse Sample() => pulse with { Sequence = ++sequence };
        await reporter.ReportAsync(Sample, Send, false, default);
        await reporter.ReportAsync(Sample, Send, false, default);
        epoch = Guid.NewGuid(); await reporter.ReportAsync(Sample, Send, false, default);
        Assert.Equal(5, requests.Count); Assert.Equal(3, queries);
        Assert.All(requests.Where(r => r.StudyPresentation is not null), r => Assert.NotNull(r.StudyPresentation!.ObservationEpoch));
        Assert.Equal(epoch, requests[^1].StudyPresentation!.ObservationEpoch);
        Assert.True(requests[^1].StudyPresentation!.Sequence > requests[1].StudyPresentation!.Sequence);
    }

    [Fact]
    public async Task Display_change_during_handshake_discards_the_old_observation()
    {
        var reporter = new StudyPresentationReporter(); var initial = Pulse(); var current = initial; int sends = 0;
        var response = await reporter.ReportAsync(() => current, (request, token) => {
            sends++; current = Pulse(); return Task.FromResult(Epoch(request, initial, Guid.NewGuid()));
        }, false, default);
        Assert.Null(response); Assert.Equal(1, sends);
    }

    [Fact]
    public async Task Concurrent_reports_do_not_compete_and_canceled_handshake_can_retry()
    {
        var reporter = new StudyPresentationReporter(); var pulse = Pulse(); int samples = 0, sends = 0;
        var pending = new TaskCompletionSource<HostResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = reporter.ReportAsync(() => { samples++; return pulse; }, (request, token) => {
            sends++; return pending.Task;
        }, false, default);
        Assert.Null(await reporter.ReportAsync(() => { samples++; return pulse; }, (_, _) => throw new Exception("overlap"), false, default));
        Assert.Equal(1, samples); Assert.Equal(1, sends);
        pending.SetCanceled(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var reply = await reporter.ReportAsync(() => pulse, (request, token) => Task.FromResult(
            request.Capability.EndsWith(".epoch", StringComparison.Ordinal) ? Epoch(request, pulse, Guid.NewGuid()) : Ok(request)), false, default);
        Assert.Equal("Succeeded", reply!.Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_registration_cannot_authorize_a_pulse(bool wrongInstance)
    {
        var reporter = new StudyPresentationReporter(); var pulse = Pulse(); int sends = 0;
        await reporter.ReportAsync(() => pulse, (request, token) => {
            sends++;
            return Task.FromResult(new HostResponse(1, request.RequestId, "Succeeded", null, "invalid",
                PresentationEpoch: new(wrongInstance ? Guid.NewGuid() : pulse.InstanceId, wrongInstance ? Guid.NewGuid() : Guid.Empty)));
        }, false, default);
        Assert.Equal(1, sends);
    }

    [Fact]
    public void New_fields_are_opt_in_and_invalid_epoch_or_payload_is_rejected()
    {
        var response = new HostResponse(1, Guid.NewGuid(), "Succeeded", null, "ordinary");
        string json = JsonSerializer.Serialize(response, Protocol.Json);
        Assert.DoesNotContain("presentationEpoch", json);
        Assert.NotNull(JsonSerializer.Deserialize<LegacyResponse>(json, Protocol.Json));
        var pulse = Pulse();
        Assert.Equal("InvalidStudyPresentation", Protocol.Validate(new(1, Guid.NewGuid(), "study.presentation",
            StudyPresentation: pulse with { ObservationEpoch = Guid.Empty })));
        Assert.Equal("InvalidStudyPresentation", Protocol.Validate(new(1, Guid.NewGuid(), "study.presentation.epoch", StudyPresentation: pulse)));
        Assert.Null(Protocol.Validate(new(1, Guid.NewGuid(), "study.presentation.epoch")));
        Assert.Null(Protocol.Validate(new(1, Guid.NewGuid(), "manual.study.presentation.epoch")));
    }

    private sealed record LegacyPulse(Guid InstanceId, Guid AppInstanceId, long Sequence, string Version,
        SchoolNoiseWindow Window, string Phase, string State, int? RenderAgeMs);
    private sealed record LegacyResponse(int Version, Guid RequestId, string Outcome, string? ErrorCode, string Message, object? Status);
}
