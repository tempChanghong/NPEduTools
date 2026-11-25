using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using NPEduTools.Contracts;
using System.Diagnostics;
using System.Windows.Threading;

namespace NPEduTools.App;

/// <summary>Reusable local management prompt. Nothing is saved by the UI; Host verifies and scopes every grant.</summary>
internal sealed class NoiseManagementDialog : Window
{
    private readonly PasswordBox _old = new() { MaxLength = 128, Margin = new(0, 6, 0, 12) };
    private readonly PasswordBox _new = new() { MaxLength = 128, Margin = new(0, 6, 0, 12) };
    private readonly PasswordBox _confirm = new() { MaxLength = 128, Margin = new(0, 6, 0, 12) };
    private readonly RadioButton _usbChoice = new() { Content = "使用教师 U 盘", Margin = new(0, 4, 0, 8) };
    private readonly string _mode;
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) };
    private readonly Button _submit = new() { Content = "验证并继续", IsDefault = true, MinWidth = 120 };
    private readonly TextBlock _deadline = new() { Margin = new(0, 0, 0, 14) };
    private readonly string _pipe;
    private readonly NoiseProtectionState _state;
    private readonly HostRequest? _target;
    private readonly StudyExitState? _exit;
    private readonly Func<Task<StudyExitState?>>? _exitRead;
    private readonly long _opened=Stopwatch.GetTimestamp();
    private readonly DispatcherTimer _exitTimer=new(){Interval=TimeSpan.FromMilliseconds(250)};
    private bool _completed,_reading,_submitting;
    public StudyExitState? FailedExit {get;private set;}
    public Guid? Ticket { get; private set; }
    internal NoiseManagementDialog(Window owner, string pipe, NoiseProtectionState state, HostRequest? target,StudyExitState? exit=null,Func<Task<StudyExitState?>>? exitRead=null, RecordingSources? recordingSources = null)
    {
        Owner = PrepareOwner(owner); _pipe = pipe; _state = state; _target = target;
        _exit=exit;_exitRead=exitRead;
        _mode = target is null ? "default" : ClassroomActionAccess.Mode(state, Protocol.NoisePurpose(target));
        Title = target?.Capability switch
        {
            null => "本机管理口令",
            "host.stop" => "退出后台 · 管理验证",
            "manual.study.command" => target.ManualStudy?.StartUtc is null ? "开始手动自习 · 管理验证" : "预约手动自习 · 管理验证",
            "recording.command" => "开始手动录课 · 管理验证",
            "classroom.set" or "classroom.retry" => target.ClassroomMode?.Target == "Exam" ? "切换到考试模式 · 管理验证" : "切换到日常模式 · 管理验证",
            "classroom.restore" => "恢复课堂模式配置 · 管理验证",
            _ => "定时监测 · 管理验证"
        };
        Width = 460; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        bool hiddenExitOwner = target?.Capability == "host.stop" && (!Owner.IsVisible || Owner.WindowState == WindowState.Minimized);
        WindowStartupLocation = hiddenExitOwner ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Topmost = hiddenExitOwner || Owner.Topmost;
        ShowInTaskbar = false;
        Background = System.Windows.Media.Brushes.White; FontFamily = new("Microsoft YaHei UI"); FontSize = 14;
        string secretName = state.ManagementSource == "School" ? "学校管理口令" : "本机管理口令";
        string sourceHint = state.ManagementSource == "School"
            ? "使用学校管理员为本班设置的口令，原本机口令已停用。" : "";
        var reservationStart = target?.ManualStudy?.StartUtc;
        bool fitContent = reservationStart.HasValue || recordingSources is not null;
        var body = new StackPanel { Margin = fitContent ? new(26, 18, 26, 16) : new(26) };
        body.Children.Add(new TextBlock { Text = Title, FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new(0,0,0,14) });
        if (recordingSources is not null)
            body.Children.Add(new TextBlock { Text = recordingSources.Summary, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) });
        _message.Text = target is null
            ? "用于本机停止定时监测和维护退出，与学校 PIN 分开。请使用 8–128 字符口令；Host 只保存加盐摘要。"
            : $"本场定时监测正在运行。请输入{secretName}。{sourceHint}本次验证只授权当前会话的这一项操作，关闭窗口不会停止采集。";
        body.Children.Add(_message);
        if (_mode != "default") _message.Text = ClassroomActionAccess.Describe(_mode);
        if (_mode == "both" && fitContent) _message.Text = "请同时验证本班管理口令和已登记的教师 U 盘。";
        if (reservationStart is { } start) _message.Text += $"\n预约开始时间　{start.ToLocalTime():yyyy-MM-dd HH:mm}";
        if(exit is not null)
        {
            _message.Text += "\n请在倒计时结束前完成本次退出验证。";
            body.Children.Add(_deadline); UpdateDeadline(exit.VerificationRemainingSeconds);
        }
        if (_mode == "either")
        {
            var choices = new StackPanel();
            var password = new RadioButton { Content = "使用本班管理口令", IsEnabled = state.Configured,
                IsChecked = state.Configured, Margin = new(0, 4, 0, 4) };
            _usbChoice.IsChecked = !state.Configured;
            choices.Children.Add(password);
            choices.Children.Add(_usbChoice);
            body.Children.Add(choices);
            password.Checked += (_, _) => { _old.Visibility = Visibility.Visible; _old.Focus(); };
            _usbChoice.Checked += (_, _) => { _old.Clear(); _old.Visibility = Visibility.Collapsed; };
            if (!state.Configured) body.Children.Add(new TextBlock { Text = "本班口令尚未就绪，可使用已登记的教师 U 盘。", TextWrapping = TextWrapping.Wrap });
            _old.Visibility = state.Configured ? Visibility.Visible : Visibility.Collapsed;
            body.Children.Add(_old);
        }
        else if (_mode != "usb" && (target is not null || state.Configured))
        {
            body.Children.Add(new TextBlock { Text = target is null ? "原管理口令" : _mode == "default" ? secretName : "本班管理口令" });
            body.Children.Add(_old);
        }
        if (_mode is "usb" or "both" || fitContent && _mode == "either")
            body.Children.Add(new TextBlock { Text = reservationStart.HasValue ? "如使用 U 盘，预约受理后即可拔出。" :
                recordingSources is not null ? "如使用 U 盘，开始录制后即可拔出。" : "请保持 U 盘插入，直至操作完成。",
                TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) });
        if (target is null)
        {
            body.Children.Add(new TextBlock { Text = "新口令" }); body.Children.Add(_new);
            body.Children.Add(new TextBlock { Text = "再次输入" }); body.Children.Add(_confirm); _submit.Content = "保存管理口令";
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if(exit is null)buttons.Children.Add(new Button { Content = "取消", IsCancel = true, Margin = new(0,0,12,0), MinWidth = 80 });
        buttons.Children.Add(_submit); body.Children.Add(buttons); Content = body;
        DesktopDialogSizing.Attach(this, pipe, useContentHeight: fitContent);
        _submit.Click += Submit;
        Closed += (_, _) => { _old.Clear(); _new.Clear(); _confirm.Clear(); };
        Loaded += (_, _) =>
        {
            if (_mode == "usb" || _usbChoice.IsChecked == true) _submit.Focus();
            else if (target is not null || state.Configured) _old.Focus();
            else _new.Focus();
        };
        if(exit is not null){WindowStyle=WindowStyle.None;_exitTimer.Tick+=ExitTick;Loaded+=(_,_)=>_exitTimer.Start();Closed+=(_,_)=>_exitTimer.Stop();Closing+=(_,e)=>{if(!_completed)e.Cancel=true;};}
    }
    private async void ExitTick(object? sender,EventArgs args)
    {
        if(_reading || _completed)return;_reading=true;
        try {
            var state=_exitRead is null?null:await _exitRead();if(_completed)return;
            // Host has accepted the password, but its scoped ticket can still be in flight.
            if(state?.AttemptId==_exit!.AttemptId && state.State=="Succeeded" && _submitting)return;
            if(state is not null && state.AttemptId==_exit!.AttemptId && state.State!="Waiting"){
                FailedExit=state.State=="Failed"?state:null;_completed=true;Close();return;
            }
            var remaining=_exit!.VerificationRemainingSeconds-Stopwatch.GetElapsedTime(_opened).TotalSeconds;
            UpdateDeadline(state?.AttemptId==_exit.AttemptId && state.State=="Waiting"?state.VerificationRemainingSeconds:remaining);
            if(remaining<=0){_completed=true;Close();}
        }finally{_reading=false;}
    }
    private void UpdateDeadline(double seconds)=>_deadline.Text=$"管理验证剩余 {Math.Max(0,Math.Ceiling(seconds)):0} 秒";
    private async void Submit(object sender, RoutedEventArgs e)
    {
        if(_submitting)return;_submitting=true;_submit.IsEnabled = false;
        try
        {
            if (_target is null && _new.Password != _confirm.Password) { _message.Text = "两次新口令不一致。"; return; }
            var command = _target is null
                ? new NoiseManagementCommand("configure", _state.Configured ? _old.Password : null, _new.Password)
                : ClassroomActionAccess.AuthorizeCommand(_state, _target, _mode,
                    _mode == "usb" || _usbChoice.IsChecked == true ? null : _old.Password);
            if (!NoiseManagementContract.Valid(command) && _exit is null) { _message.Text = "请输入 8–128 字符口令，不含控制字符。"; return; }
            var reply = await Request(_pipe, new(Protocol.Version, Guid.NewGuid(), "noise.management.command", NoiseManagement: command));
            _old.Clear(); _new.Clear(); _confirm.Clear();
            // Cancel may close the modal while the bounded request is in flight.
            // Its eventual reply must not reopen it or set DialogResult on a closed window.
            if (!IsLoaded || _completed) return;
            if (reply.Outcome != "Succeeded") {
                _message.Text = reply.ErrorCode switch
                {
                    "CLASSROOM_USB_REQUIRED" => "未检测到本班获准的教师 U 盘。请插入正确的 U 盘再验证。",
                    "CLASSROOM_ACTION_DENIED" => ClassroomActionAccess.Describe("deny"),
                    "CLASSROOM_POLICY_CHANGED" => "学校策略已变化，请关闭此窗口并重新核对本次操作。",
                    _ => reply.Message
                };
                if(_exit is not null && _exitRead is not null){var failed=await _exitRead();if(_completed)return;if(failed?.AttemptId==_exit.AttemptId && failed.State=="Failed"){FailedExit=failed;_completed=true;Close();}}
                return;
            }
            if (_target is not null && (Ticket = ClassroomActionAccess.Ticket(reply, _state)) is null)
            { _message.Text = "未收到原操作的有效验证结果，请重新核对。"; return; }
            _completed=true;DialogResult = true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or JsonException)
        { _message.Text = "无法确认管理验证，未继续操作。请核实后台连接后重试。"; }
        finally { _submitting=false;_submit.IsEnabled = true; }
    }
    internal static async Task<HostResponse> Request(string pipe, HostRequest request)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await HostClient.RequestAsync(pipe, request, deadline.Token);
    }
    internal static Window PrepareOwner(Window owner)
    {
        owner = WorkspacePanel.DialogOwner(owner);
        // Quiet login may never show MainWindow. A native source is required by
        // WPF Owner, but creating it must not open or focus the hidden workspace.
        new WindowInteropHelper(owner).EnsureHandle();
        return owner;
    }
    private static async Task<HostResponse?> ReadStateAsync(Window owner, string pipe, string? purpose = null)
    {
        bool closed = false;
        void OwnerClosed(object? sender, EventArgs args) => closed = true;
        owner.Closed += OwnerClosed;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var reply = purpose switch
            {
                "start-manual-recording" => await HostClient.RequestAsync(pipe, ClassroomActionAccess.ManualRecordingStatusCapability, deadline.Token),
                "start-manual-study" => await HostClient.RequestAsync(pipe, ClassroomActionAccess.ManualStudyStatusCapability, deadline.Token),
                "stop" or "maintenance" or "switch-classroom-mode" => await ClassroomActionAccess.ReadAsync(pipe, deadline.Token, purpose == "switch-classroom-mode"),
                _ => await HostClient.RequestAsync(pipe, "noise.management.status", deadline.Token)
            };
            // IsLoaded can remain true until WPF processes unloading after Closed.
            return closed ? null : reply;
        }
        finally { owner.Closed -= OwnerClosed; }
    }
    internal static async Task<HostRequest?> AuthorizeAsync(Window owner, string pipe, HostRequest target,StudyExitState? exit=null,Func<Task<StudyExitState?>>? exitRead=null,Action<StudyExitState>? failed=null, Guid? expectedSession = null, NoiseProtectionState? expectedAccess = null, Action<string>? reportBlocked = null, Func<bool>? stillCurrent = null, RecordingSources? recordingSources = null)
    {
        var purpose = Protocol.NoisePurpose(target);
        bool manualStart = purpose is "start-manual-study" or "start-manual-recording";
        if (!manualStart && !Protocol.NoiseInterruption(target)) return target;
        owner = PrepareOwner(owner);
        var reply = await ReadStateAsync(owner, pipe, purpose);
        if (reply is null || stillCurrent?.Invoke() == false) return null;
        // Older Hosts have no manual-start policy. Their normal start command still enforces capture and schedule priority.
        if (manualStart && reply is { Outcome: "Rejected", ErrorCode: "UnknownCapability" }) return target;
        if (reply.Outcome != "Succeeded" || reply.NoiseProtection is not { } state)
        {
            if (reportBlocked is null) MessageBox.Show(owner, reply.Message, "无法核实操作权限");
            else reportBlocked("无法核实操作权限：" + reply.Message);
            return null;
        }
        if (expectedSession is { } session && (!state.Protected || state.SessionId != session || state.InstanceId != target.Noise?.InstanceId)) return null;
        if (expectedAccess is not null && (state.InstanceId != expectedAccess.InstanceId || state.SessionId != expectedAccess.SessionId ||
            state.ClassroomPolicy != expectedAccess.ClassroomPolicy || state.CredentialRevision != expectedAccess.CredentialRevision))
            return null;
        var mode = ClassroomActionAccess.Mode(state, purpose);
        if (manualStart && mode == "default") return target;
        if (ClassroomActionAccess.Blocked(state, mode, purpose) is { } blocked)
        {
            if (reportBlocked is null) MessageBox.Show(owner, blocked, "学校管理策略");
            else reportBlocked(blocked);
            return null;
        }
        if (mode == "default" && !state.Protected) return target;
        if (purpose == "start-manual-recording" && target.Recording is { Options: { } selected } commandTarget)
        {
            if (recordingSources is null)
            {
                reportBlocked?.Invoke("无法确认本次录制的设备。请在录制设置中重新检测，或明确选择录制屏幕和已开启的音源。");
                return null;
            }
            target = target with { Recording = commandTarget with { Options = recordingSources.Pin(selected) } };
        }
        if (mode == "allow")
        {
            var command = ClassroomActionAccess.AuthorizeCommand(state, target, mode, null);
            var grant = await Request(pipe, new(Protocol.Version, Guid.NewGuid(), "noise.management.command", NoiseManagement: command));
            if (ClassroomActionAccess.Ticket(grant, state) is { } managedTicket) return target with { NoiseAuthorization = managedTicket };
            reportBlocked?.Invoke(grant.Outcome == "Succeeded" ? "本次授权结果与原请求不符，请重新核对。" : grant.Message);
            return null;
        }
        var dialog = new NoiseManagementDialog(owner, pipe, state, target,exit,exitRead, recordingSources);
        bool succeeded=dialog.ShowDialog()==true;
        if(dialog.FailedExit is {} denied)failed?.Invoke(denied);
        return succeeded && dialog.Ticket is { } ticket ? target with { NoiseAuthorization = ticket } : null;
    }
    internal static async Task ConfigureAsync(Window owner, string pipe)
    {
        owner = PrepareOwner(owner);
        var reply = await ReadStateAsync(owner, pipe);
        if (reply is null) return;
        if (reply.NoiseProtection is not { } state || reply.Outcome != "Succeeded")
        { MessageBox.Show(owner, reply.Message, "无法读取管理设置"); return; }
        if (state.ManagementSource == "School")
        { MessageBox.Show(owner, CalibrationAccess.Hint(state) + "\n学校口令由学校管理员在 NPEduConsole 设置，本机不能覆盖。", "学校管理口令"); return; }
        if (state.Protected && !state.Configured)
        { MessageBox.Show(owner, ClassroomActionAccess.Blocked(state, "default")!, "本机管理口令"); return; }
        new NoiseManagementDialog(owner, pipe, state, null).ShowDialog();
    }
}
