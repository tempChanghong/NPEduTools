namespace NPEduTools.Core;

public sealed class GuardSupervisor(GuardFiles files, GuardRegistration registration, IGuardProcesses processes, TimeProvider? time = null)
{
    private readonly GuardRecoveryPolicy _policy = new();
    private GuardProcess _app = registration.App;
    private GuardProcess? _host;
    private GuardWakePlan? _plan;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private bool _suspended;
    private bool _finished;
    public void Suspend() { _policy.Suspend(); _suspended = true; }
    // Resuming permits cached launch hints, but never revives the pre-sleep capture lease.
    public void Resume() { _policy.Suspend(); _suspended = false; }
    public void BeginSessionEnd() => files.SessionQuery(registration.Generation, true);
    public void CancelSessionEnd() => files.SessionQuery(registration.Generation, false);
    public void Finish(string reason)
    {
        _finished = true;
        files.Stop(registration.Generation, reason);
        Save("Stopped", "守护已停止；维护或 Windows 会话结束不会自动拉起。");
    }
    public bool Tick(double seconds, bool interactive)
    {
        if (_finished) return false;
        if (files.Stopped(registration.Generation) || files.Read<GuardRegistration>("registration.json")?.Generation != registration.Generation)
        { Save("Stopped", "守护已停止；维护或 Windows 会话结束不会自动拉起。"); return false; }
        if (files.SessionEnding(registration.Generation))
        { Save("SessionEnding", "Windows 正在确认是否结束会话，守护暂不启动进程。"); return true; }
        var lease = files.Read<GuardLease>("lease.json");
        if (lease?.Generation == registration.Generation && (lease.Host == _host || processes.Matches(lease.Host, false)))
        { _host = lease.Host; if (_policy.Observe(lease, seconds) && processes.Matches(lease.Host, false)) _suspended = false; }
        var plan = files.Read<GuardWakePlan>("wake.json");
        // A cache may outlive its publishing Host. It authorizes a launch only, never capture.
        if (plan?.Generation == registration.Generation && (plan.Host == _host || processes.Matches(plan.Host, false) || _host is null)) _plan = plan;
        var expected = files.Read<GuardExpectedExit>("expected-exit.json");
        bool expectedHost = expected?.Generation == registration.Generation && expected.Host == _host;
        var utc = _time.GetUtcNow();
        bool skipWindow = expected?.Generation == registration.Generation && expected.SkipUntilUtc is { } until && utc < until;
        bool planned = !_suspended && !skipWindow && _plan?.Due(utc) is not null;
        // A previously expected Host exit must not reset the pending next-window wake on every tick.
        if (!planned && (expectedHost || skipWindow || _plan is not null && _plan.Due(utc) is null)) _policy.Suspend();
        var current = files.Read<GuardRegistration>("registration.json");
        if (current?.Generation == registration.Generation && processes.Matches(current.App, true)) _app = current.App;
        bool appMissing = !processes.IsAlive(_app), hostMissing = _host is not null && !processes.IsAlive(_host);
        string phase = _policy.Limited ? "RecoveryLimit" : _policy.Armed(seconds) ? "Armed" : planned ? "ScheduledWake" : "Watching";
        if (_policy.MayRestart(appMissing || hostMissing || planned && _host is null, interactive, seconds, planned))
        {
            if (files.Stopped(registration.Generation)) return false;
            if (files.SessionEnding(registration.Generation)) return true;
            _policy.Attempted(); phase = "Recovering";
            try { if (appMissing) _app = processes.Start(true); else _host = processes.Start(false); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException) { phase = "RecoveryFailed"; }
        }
        // Task Scheduler COM calls can pump WM_ENDSESSION on this same thread. Do not overwrite
        // the final stop record when a launch returns after the shutdown callback has finished.
        if (_finished) return false;
        if (files.Stopped(registration.Generation)) { Save("Stopped", "维护或 Windows 会话结束，守护停止。"); return false; }
        if (files.SessionEnding(registration.Generation)) { Save("SessionEnding", "Windows 正在确认是否结束会话，守护暂不启动进程。"); return true; }
        Save(phase, phase switch { "Armed" => "定时监测守护已就绪。", "Recovering" => "正在恢复异常退出的程序；采集仍需重新核验学校规则和时间。",
            "ScheduledWake" => "已到学校自习准备或监测时段；启动后仍须重新核验授权和学校时间。",
            "RecoveryLimit" => "连续恢复三次，暂停五分钟后继续核验；请维护人员检查故障。", "RecoveryFailed" => "独立启动失败，将按退避重试。",
            _ => "守护待命；后台退出不结束守护，将在下一次学校自习准备前唤醒。" });
        return true;
    }
    private void Save(string phase, string message) => files.Write("status.json", new GuardStatus(registration.Generation, phase, _policy.Restarts, message, DateTimeOffset.UtcNow, Environment.ProcessId));
}
