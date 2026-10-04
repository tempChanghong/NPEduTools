namespace NPEduTools.Core;

public sealed class GuardSupervisor(GuardFiles files, GuardRegistration registration, IGuardProcesses processes)
{
    private readonly GuardRecoveryPolicy _policy = new();
    private GuardProcess _app = registration.App;
    private GuardProcess? _host;
    public void Suspend() => _policy.Suspend();
    public void BeginSessionEnd() => files.SessionQuery(registration.Generation, true);
    public void CancelSessionEnd() => files.SessionQuery(registration.Generation, false);
    public void Finish(string reason)
    {
        files.Stop(registration.Generation, reason);
        Save("Stopped", "守护已停止；正常退出、维护或 Windows 会话结束不会自动拉起。");
    }
    public bool Tick(double seconds, bool interactive)
    {
        if (files.Stopped(registration.Generation) || files.Read<GuardRegistration>("registration.json")?.Generation != registration.Generation)
        { Save("Stopped", "守护已停止；正常退出、维护或 Windows 会话结束不会自动拉起。"); return false; }
        if (files.SessionEnding(registration.Generation))
        { Save("SessionEnding", "Windows 正在确认是否结束会话，守护暂不启动进程。"); return true; }
        var lease = files.Read<GuardLease>("lease.json");
        if (lease?.Generation == registration.Generation && (lease.Host == _host || processes.Matches(lease.Host, false)))
        { _host = lease.Host; _policy.Observe(lease, seconds); }
        var current = files.Read<GuardRegistration>("registration.json");
        if (current?.Generation == registration.Generation && processes.Matches(current.App, true)) _app = current.App;
        bool appMissing = !processes.IsAlive(_app), hostMissing = _host is not null && !processes.IsAlive(_host);
        string phase = _policy.Limited ? "RecoveryLimit" : _policy.Armed(seconds) ? "Armed" : "Watching";
        if (_policy.MayRestart(appMissing || hostMissing, interactive, seconds))
        {
            if (files.Stopped(registration.Generation)) return false;
            if (files.SessionEnding(registration.Generation)) return true;
            _policy.Attempted(); phase = "Recovering";
            try { if (appMissing) _app = processes.Start(true); else _host = processes.Start(false); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException) { phase = "RecoveryFailed"; }
        }
        Save(phase, phase switch { "Armed" => "定时监测守护已就绪。", "Recovering" => "正在恢复异常退出的程序；采集仍需重新核验学校规则和时间。",
            "RecoveryLimit" => "自动恢复已达三次上限，请检查故障后手动重启 NPEduTools。", "RecoveryFailed" => "自动恢复启动失败，将按退避重试。",
            _ => "守护待命；只有实际定时采集期间的异常退出会恢复。" });
        return !(appMissing && (_host is null || hostMissing) && !_policy.Armed(seconds));
    }
    private void Save(string phase, string message) => files.Write("status.json", new GuardStatus(registration.Generation, phase, _policy.Restarts, message, DateTimeOffset.UtcNow, Environment.ProcessId));
}
