namespace NPEduTools.Core;

public sealed class GuardHostSession : IAsyncDisposable
{
    private readonly GuardFiles _files;
    private readonly Func<bool> _armed;
    private readonly GuardProcess _identity = GuardProcess.Current();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private long _sequence;
    public GuardHostSession(GuardFiles files, Func<bool> armed)
    { _files = files; _armed = armed; _loop = LoopAsync(); }
    public bool ExpectedStop()
    {
        try { _files.StopCurrent("HostNormalStop"); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
    private async Task LoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    if (_files.Read<GuardRegistration>("registration.json") is { } r && !_files.Stopped(r.Generation))
                        _files.Write("lease.json", new GuardLease(r.Generation, _identity, ++_sequence, _armed()));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                await Task.Delay(1000, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); await _loop; _stop.Dispose(); }
}
