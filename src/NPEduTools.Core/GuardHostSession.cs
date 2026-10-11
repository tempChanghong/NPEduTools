namespace NPEduTools.Core;

public sealed class GuardHostSession : IAsyncDisposable
{
    private readonly GuardFiles _files;
    private readonly Func<bool> _armed;
    private readonly GuardProcess _identity = GuardProcess.Current();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private long _sequence;
    private readonly Func<Guid, GuardProcess, GuardWakePlan?>? _plan;
    private readonly Action? _ensureGuard;
    private bool _expected;
    private readonly object _writeSync = new();
    public GuardHostSession(GuardFiles files, Func<bool> armed, Func<Guid, GuardProcess, GuardWakePlan?>? plan = null, Action? ensureGuard = null)
    { _files = files; _armed = armed; _plan = plan; _ensureGuard = ensureGuard; _loop = LoopAsync(); }
    public bool ExpectedStop()=>ExpectedStop(true);
    public bool ExpectedStop(bool skipCurrentWindow)
    {
        try
        {
            lock (_writeSync)
            {
                if (_files.Read<GuardRegistration>("registration.json") is { } r)
                {
                    var plan = _plan?.Invoke(r.Generation, _identity);
                    var previous = _files.Read<GuardWakePlan>("wake.json");
                    var end = plan?.CurrentEndUtc ?? (previous?.Generation == r.Generation && previous.Host == _identity ? previous.CurrentEndUtc : null);
                    // Write the suppression before acknowledging exit. The guardian itself remains alive.
                    _files.Write("expected-exit.json", new GuardExpectedExit(r.Generation, _identity, skipCurrentWindow?end:null));
                    if (plan is not null) _files.Write("wake.json", plan);
                    _files.Write("lease.json", new GuardLease(r.Generation, _identity, ++_sequence, false));
                }
                _expected = true;
            }
            return true;
        }
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
                    lock (_writeSync)
                    {
                        if (!_expected && _files.Read<GuardRegistration>("registration.json") is { } r && !_files.Stopped(r.Generation))
                        {
                            _files.Write("lease.json", new GuardLease(r.Generation, _identity, ++_sequence, _armed()));
                            if (_plan?.Invoke(r.Generation, _identity) is { } plan) _files.Write("wake.json", plan);
                            if (_sequence % 5 == 0 && !_files.SessionEnding(r.Generation)) _ensureGuard?.Invoke();
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException) { }
                await Task.Delay(1000, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); await _loop; _stop.Dispose(); }
}
