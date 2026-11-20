using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Host;

public sealed class SchoolUpdateMaintenance(Func<string?> check, Func<CancellationToken, Task<IDisposable?>> reserve,
    Func<bool> desktopRunning, Func<bool> expectedStop, Action cancelExpectedStop, Action stop) : ISchoolUpdateHost
{
    internal sealed class Reservation(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
    private sealed class DesktopCheck
    {
        internal readonly TaskCompletionSource<bool> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Close = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private DesktopCheck? _desktop;
    internal bool CheckingDesktop => Volatile.Read(ref _desktop) is not null;
    internal async Task<bool> DesktopReadyAsync(CancellationToken token)
    {
        var check = Volatile.Read(ref _desktop);
        if (check is null) return false;
        check.Ready.TrySetResult(true);
        try { return await check.Close.Task.WaitAsync(TimeSpan.FromSeconds(14), token); }
        catch (TimeoutException) { return false; }
    }
    public string RunningVersion { get; } = typeof(SchoolUpdateMaintenance).Assembly.GetName().Version!.ToString(4);
    public string? Deferral() => check();
    internal static string? Deferral(SchoolClockFrame clock, StudyHostState study, ManualStudyState manual,
        RecordingState recording, bool captureBusy, bool modeBusy)
    {
        if (recording.Active) return "RECORDING_ACTIVE";
        if (captureBusy || manual.State is "Waiting" or "Preparing" or "Starting" or "Active" or "Stopping" ||
            study.Phase is "Active" or "Preparing" or "Ended") return "STUDY_ACTIVE";
        if (modeBusy || recording.Phase is not ("Idle" or "Saved") || manual.State is not ("Idle" or "Ended" or "Cancelled") ||
            study.PolicyState != "APPLIED" || study.Phase is not ("Outside" or "Disabled")) return "STATE_UNKNOWN";
        if (clock is not { State: "Advancing", SchoolNow: {} now, Schedule: { Enabled: true, ClockVerified: true } day } ||
            !double.IsFinite(clock.AgeMs) || clock.AgeMs is < 0 or >= SchoolClockFrame.MaxAgeMs ||
            day.Date != DateOnly.FromDateTime(now.Date) || day.Lessons is null) return "STATE_UNKNOWN";
        var lessons = day.Lessons.Where(l => l.Enabled).ToArray();
        if (lessons.Length == 0 || lessons.Any(l => l.End <= l.Start)) return "STATE_UNKNOWN";
        return now < lessons.Max(l => l.End) ? "CLASS_IN_SESSION" : null;
    }
    public ManagedUpdateReceipt? Result(Guid planId) => SchoolUpdateWorker.ReadResult(planId);
    public Task<IPreparedSchoolUpdate> PrepareAsync(ManagedUpdatePlan plan, CancellationToken token) =>
        SchoolUpdateWorker.PrepareAsync(plan, RunningVersion, token);
    public async Task<bool> HandoffAsync(IPreparedSchoolUpdate worker, Func<Task<bool>> authorize, CancellationToken token)
    {
        var prompt = new DesktopCheck();
        if (Interlocked.CompareExchange(ref _desktop, prompt, null) is not null) return false;
        IDisposable? hold = null;
        bool handedOff = false, guardPrepared = false;
        try
        {
            if (desktopRunning())
            {
                try { await prompt.Ready.Task.WaitAsync(TimeSpan.FromSeconds(8), token); }
                catch (TimeoutException) { return false; }
            }
            if (check() is not null || (hold = await reserve(token)) is null || check() is not null) return false;
            if (!await authorize() || check() is not null) return false;
            guardPrepared = true;
            if (!expectedStop()) throw new SchoolUpdateException("MAINTENANCE_UNAVAILABLE");
            // The worker proceeds only after it receives apply and observes the original processes exit.
            // Writing to this specific child first keeps the desktop running if the worker already died.
            try { await worker.ApplyAsync(token); }
            catch (IOException) { throw new SchoolUpdateException("WORKER_UNAVAILABLE"); }
            prompt.Close.TrySetResult(true);
            if (desktopRunning())
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(8));
                try { while (desktopRunning()) await Task.Delay(100, deadline.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { throw new SchoolUpdateException("DESKTOP_STILL_RUNNING"); }
            }
            stop();
            handedOff = true;
            return true;
        }
        finally
        {
            prompt.Close.TrySetResult(false);
            Interlocked.CompareExchange(ref _desktop, null, prompt);
            if (!handedOff)
            {
                try { if (guardPrepared) cancelExpectedStop(); }
                finally { hold?.Dispose(); }
            }
            // After apply, keep the reservations until this Host exits.
        }
    }
}
