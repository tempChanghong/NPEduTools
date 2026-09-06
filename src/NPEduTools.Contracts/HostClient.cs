using System.IO.Pipes;
using System.Runtime.CompilerServices;

namespace NPEduTools.Contracts;

public static class HostClient
{
    public static async Task<HostResponse> RequestAsync(string pipeName, string capability, CancellationToken token)
    {
        await using var pipe = CreatePipe(pipeName);
        await pipe.ConnectAsync(1500, token);
        var request = new HostRequest(Protocol.Version, Guid.NewGuid(), capability);
        await Protocol.WriteAsync(pipe, request, token);
        var response = await Protocol.ReadAsync<HostResponse>(pipe, token);
        if (response.Version != Protocol.Version || response.RequestId != request.RequestId)
            throw new InvalidDataException("Response correlation or protocol mismatch.");
        return response;
    }

    public static async IAsyncEnumerable<WatchSnapshot> WatchAsync(string pipeName,
        [EnumeratorCancellation] CancellationToken token)
    {
        await using var pipe = CreatePipe(pipeName);
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            handshake.CancelAfter(TimeSpan.FromSeconds(3));
            await pipe.ConnectAsync(1500, handshake.Token);
        }
        var request = new HostRequest(Protocol.Version, Guid.NewGuid(), "classisland.watch");
        using (var write = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            write.CancelAfter(TimeSpan.FromSeconds(2));
            await Protocol.WriteAsync(pipe, request, write.Token);
        }
        Guid? stream = null;
        long lastSequence = -1;
        while (!token.IsCancellationRequested)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var snapshot = await Protocol.ReadAsync<WatchSnapshot>(pipe, deadline.Token);
            if (snapshot.Version != Protocol.Version || snapshot.RequestId != request.RequestId ||
                (stream is not null && stream != snapshot.StreamId) || snapshot.Sequence < lastSequence ||
                (snapshot.Outcome == "Succeeded" && snapshot.Status is null) ||
                (snapshot.Outcome != "Succeeded" && snapshot.Status is not null))
                throw new InvalidDataException("Invalid subscription snapshot.");
            // Sequence gaps are safe: each frame replaces all state, never applies partial deltas.
            stream = snapshot.StreamId;
            lastSequence = snapshot.Sequence;
            yield return snapshot;
            if (snapshot.Outcome is "Rejected" or "Stopped") yield break;
        }
    }

    private static NamedPipeClientStream CreatePipe(string name) => new(".", name, PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
}
