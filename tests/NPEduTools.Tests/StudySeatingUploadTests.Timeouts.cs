using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Tests;

public sealed partial class StudySeatingUploadTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Queued_seating_deadline_returns_an_explicit_result_without_releasing_another_request(bool uploading)
    {
        if (!OperatingSystem.IsWindows()) return;
        string pipe = "NPEduTools.Test.seating-queued-deadline." + Guid.NewGuid().ToString("N");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool hold = false; int preparations = 0, publications = 0;
        Guid id = Guid.NewGuid(); byte[] bytes = Image();
        var service = new StudySeatingUploadService(pipe, Policy,
            async (_, cancellation) =>
            {
                Interlocked.Increment(ref preparations);
                if (hold) { entered.TrySetResult(); await release.Task.WaitAsync(cancellation); }
                return new NpepSeatingUploadScope(new(), Scope());
            },
            (_, uploadId, revision, png, _) =>
            {
                Assert.Equal(id, uploadId); Interlocked.Increment(ref publications);
                var result = Scope(revision + 1);
                result["hash"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(png));
                return Task.FromResult(result);
            });
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var running = new PipeServer(pipe, new NoSchoolRead(), _ => { }, seatingUploads: service).RunAsync(lifetime.Token);
        Task<HostResponse>? blocker = null;
        try
        {
            var request = new HostRequest(1, Guid.NewGuid(), "study.seating.upload.status");
            if (uploading)
            {
                var status = await HostClient.RequestAsync(pipe, request, lifetime.Token);
                StudySeatingTransfer.Write(pipe, id, bytes); request = Command(status.SeatingUpload!, id, bytes);
            }
            int before = preparations;
            hold = true;
            blocker = HostClient.RequestAsync(pipe, new HostRequest(1, Guid.NewGuid(), "study.seating.upload.status", TimeoutMs: 8000), lifetime.Token);
            await entered.Task.WaitAsync(lifetime.Token);
            var expired = request with { TimeoutMs = 250 };
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var response = await HostClient.RequestAsync(pipe, expired with { RequestId = Guid.NewGuid() }, lifetime.Token);
                Assert.Equal("Rejected", response.Outcome); Assert.Equal("UPLOAD_UNAVAILABLE", response.ErrorCode);
                Assert.Equal(before + 1, preparations); Assert.Equal(0, publications);
                Assert.False(blocker.IsCompleted); Assert.False(running.IsCompleted);
            }
            Assert.Equal("Succeeded", (await HostClient.RequestAsync(pipe, "host.ping", lifetime.Token)).Outcome);
            release.TrySetResult();
            Assert.Equal("Succeeded", (await blocker).Outcome);
            var recovered = await HostClient.RequestAsync(pipe, request with { RequestId = Guid.NewGuid() }, lifetime.Token);
            Assert.Equal("Succeeded", recovered.Outcome);
            Assert.Equal(uploading ? "Saved" : "Ready", recovered.SeatingUpload!.State);
            Assert.Equal(uploading ? 1 : 0, publications);
        }
        finally
        {
            release.TrySetResult(); lifetime.Cancel(); StudySeatingTransfer.Delete(pipe, id);
            if (blocker is not null) try { await blocker; } catch (Exception e) when (e is IOException or OperationCanceledException) { }
            try { await running; } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Http_deadline_returns_an_explicit_seating_result_and_the_same_upload_can_be_retried(bool uploading, bool requestDeadline)
    {
        if (!OperatingSystem.IsWindows()) return;
        string pipe = "NPEduTools.Test.seating-timeout." + Guid.NewGuid().ToString("N");
        Guid id = Guid.NewGuid(); byte[] bytes = Image(); bool timeout = true; int attempts = 0;
        var service = new StudySeatingUploadService(pipe, Policy,
            async (_, cancellation) =>
            {
                if (!uploading && timeout)
                {
                    if (requestDeadline) await Task.Delay(Timeout.Infinite, cancellation);
                    throw new TaskCanceledException("Synthetic HTTP-owned deadline");
                }
                return new NpepSeatingUploadScope(new(), Scope());
            },
            async (_, uploadId, revision, png, cancellation) =>
            {
                Assert.Equal(id, uploadId); attempts++;
                if (timeout)
                {
                    if (requestDeadline) await Task.Delay(Timeout.Infinite, cancellation);
                    throw new TaskCanceledException("Synthetic HTTP-owned deadline after publication began");
                }
                var result = Scope(revision + 1); result["hash"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(png));
                return result;
            });
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var running = new PipeServer(pipe, new NoSchoolRead(), _ => { }, seatingUploads: service).RunAsync(lifetime.Token);
        try
        {
            var request = new HostRequest(1, Guid.NewGuid(), "study.seating.upload.status");
            if (uploading)
            {
                var status = await HostClient.RequestAsync(pipe, request, lifetime.Token);
                StudySeatingTransfer.Write(pipe, id, bytes); request = Command(status.SeatingUpload!, id, bytes);
            }
            request = request with { TimeoutMs = 250 };
            var response = await HostClient.RequestAsync(pipe, request, lifetime.Token);
            Assert.Equal("Rejected", response.Outcome);
            Assert.Equal(uploading ? "UPLOAD_PENDING" : "UPLOAD_UNAVAILABLE", response.ErrorCode);
            Assert.Equal("Succeeded", (await HostClient.RequestAsync(pipe, "host.ping", lifetime.Token)).Outcome);
            Assert.False(running.IsCompleted);
            timeout = false;
            var recovered = await HostClient.RequestAsync(pipe, request with { RequestId = Guid.NewGuid() }, lifetime.Token);
            Assert.Equal("Succeeded", recovered.Outcome);
            if (uploading)
            {
                Assert.Equal("Saved", recovered.SeatingUpload!.State); Assert.Equal(2, attempts);
                StudySeatingTransfer.Delete(pipe, id);
                Assert.Equal(recovered.SeatingUpload, (await HostClient.RequestAsync(pipe, request, lifetime.Token)).SeatingUpload);
                Assert.Equal(2, attempts); // The saved result does not publish another revision.
            }
            else Assert.Equal("Ready", recovered.SeatingUpload!.State);
        }
        finally
        {
            lifetime.Cancel(); StudySeatingTransfer.Delete(pipe, id);
            try { await running; } catch (OperationCanceledException) { }
        }
    }
}
