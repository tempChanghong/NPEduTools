namespace NPEduTools.Host;

/// <summary>
/// Ordinary reservations fail fast; priority exam requests wait for existing holders.
/// Callers retain their lease until the actual operation finishes,
/// not just until an IPC acknowledgement is returned. Read-only requests need no lease.
/// </summary>
public sealed class RuntimeOperationGate(bool coordinateDesktop = false)
{
    private readonly object _sync = new();
    private int _mutations;
    private bool _switching;
    private bool _priorityPending;

    public async Task<IDisposable> ReservePrioritySwitchAsync(CancellationToken token)
    {
        lock (_sync)
        {
            if (_priorityPending || _switching) throw new RemoteExamException("OPERATION_BUSY");
            _priorityPending = true;
        }
        IDisposable? priority = null;
        try
        {
            if (coordinateDesktop)
                priority = NPEduTools.Core.RuntimeOperationFile.RequestPriority() ?? throw new RemoteExamException("OPERATION_BUSY");
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(30))
            {
                token.ThrowIfCancellationRequested();
                var lease = TryReserveSwitch(priorityOwner: true);
                if (lease is not null)
                {
                    var ownedPriority = priority; priority = null;
                    return new Lease(() => { lease.Dispose(); ownedPriority?.Dispose(); lock (_sync) _priorityPending = false; });
                }
                await Task.Delay(100, token);
            }
            throw new RemoteExamException("OPERATION_BUSY");
        }
        catch { priority?.Dispose(); lock (_sync) _priorityPending = false; throw; }
    }

    public bool Switching { get { lock (_sync) return _switching; } }
    public bool ExamRequested { get { lock (_sync) return _priorityPending; } }

    // IPC acceptance is not completion: retain a mutation lease through its background work.
    internal static void ReleaseAfter(IDisposable lease, Task work) =>
        _ = work.ContinueWith(_ => lease.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public IDisposable? TryEnterMutation()
    {
        lock (_sync)
        {
            if (_switching || _priorityPending) return null;
            var desktop = coordinateDesktop ? NPEduTools.Core.RuntimeOperationFile.TryAcquire(false) : null;
            if (coordinateDesktop && desktop is null) return null;
            _mutations++;
            return new Lease(() => { lock (_sync) { _mutations--; desktop?.Dispose(); } });
        }
    }

    public IDisposable? TryReserveSwitch() => TryReserveSwitch(priorityOwner: false);

    private IDisposable? TryReserveSwitch(bool priorityOwner)
    {
        lock (_sync)
        {
            if (_switching || _mutations != 0 || (_priorityPending && !priorityOwner)) return null;
            var desktop = coordinateDesktop ? NPEduTools.Core.RuntimeOperationFile.TryAcquire(true) : null;
            if (coordinateDesktop && desktop is null) return null;
            _switching = true;
            return new Lease(() => { lock (_sync) { _switching = false; desktop?.Dispose(); } });
        }
    }

    internal sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
