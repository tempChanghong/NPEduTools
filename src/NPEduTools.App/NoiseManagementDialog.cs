using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
    private NoiseManagementDialog(Window owner, string pipe, NoiseProtectionState state, HostRequest? target,StudyExitState? exit=null,Func<Task<StudyExitState?>>? exitRead=null)
    {
        Owner = owner; _pipe = pipe; _state = state; _target = target;
        _exit=exit;_exitRead=exitRead;
        Title = target is null ? "本机管理口令" : "定时监测 · 管理验证";
        Width = 460; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = System.Windows.Media.Brushes.White; FontFamily = new("Microsoft YaHei UI"); FontSize = 14;
        var body = new StackPanel { Margin = new(26) };
        body.Children.Add(new TextBlock { Text = Title, FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new(0,0,0,14) });
        _message.Text = target is null
            ? "用于本机停止学校定时监测和维护退出，与学校 PIN 分开。请使用 8–128 字符口令；Host 只保存加盐摘要。"
            : "学校定时监测正在运行。请输入本机管理口令。本次验证只授权当前会话的这一项操作，关闭窗口不会停止采集。";
        body.Children.Add(_message);
        if(exit is not null){_message.Text="请在 30 秒内输入本机管理口令。首次密码错误或超时将显示退出验证未通过提醒，后台监测继续。";body.Children.Add(_deadline);UpdateDeadline(exit.VerificationRemainingSeconds);}
        if (target is not null || state.Configured)
        { body.Children.Add(new TextBlock { Text = target is null ? "原管理口令" : "管理口令" }); body.Children.Add(_old); }
        if (target is null)
        {
            body.Children.Add(new TextBlock { Text = "新口令" }); body.Children.Add(_new);
            body.Children.Add(new TextBlock { Text = "再次输入" }); body.Children.Add(_confirm); _submit.Content = "保存管理口令";
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if(exit is null)buttons.Children.Add(new Button { Content = "取消", IsCancel = true, Margin = new(0,0,12,0), MinWidth = 80 });
        buttons.Children.Add(_submit); body.Children.Add(buttons); Content = body;
        DesktopDialogSizing.Attach(this, pipe);
        _submit.Click += Submit;
        Closed += (_, _) => { _old.Clear(); _new.Clear(); _confirm.Clear(); };
        Loaded += (_, _) => { if (target is not null || state.Configured) _old.Focus(); else _new.Focus(); };
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
                : new NoiseManagementCommand("authorize", _old.Password, Purpose: Protocol.NoisePurpose(_target),
                    TargetRequestId: _target.RequestId, InstanceId: _state.InstanceId, SessionId: _state.SessionId);
            if (!NoiseManagementContract.Valid(command) && _exit is null) { _message.Text = "请输入 8–128 字符口令，不含控制字符。"; return; }
            var reply = await Request(_pipe, new(Protocol.Version, Guid.NewGuid(), "noise.management.command", NoiseManagement: command));
            _old.Clear(); _new.Clear(); _confirm.Clear();
            // Cancel may close the modal while the bounded request is in flight.
            // Its eventual reply must not reopen it or set DialogResult on a closed window.
            if (!IsLoaded || _completed) return;
            if (reply.Outcome != "Succeeded") {
                _message.Text = reply.Message;
                if(_exit is not null && _exitRead is not null){var failed=await _exitRead();if(_completed)return;if(failed?.AttemptId==_exit.AttemptId && failed.State=="Failed"){FailedExit=failed;_completed=true;Close();}}
                return;
            }
            Ticket = reply.NoiseProtection?.Ticket; _completed=true;DialogResult = true;
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
    private static async Task<HostResponse?> ReadStateAsync(Window owner, string pipe)
    {
        bool closed = false;
        void OwnerClosed(object? sender, EventArgs args) => closed = true;
        owner.Closed += OwnerClosed;
        try
        {
            var reply = await Request(pipe, new(Protocol.Version, Guid.NewGuid(), "noise.management.status"));
            // IsLoaded can remain true until WPF processes unloading after Closed.
            return closed ? null : reply;
        }
        finally { owner.Closed -= OwnerClosed; }
    }
    internal static async Task<HostRequest?> AuthorizeAsync(Window owner, string pipe, HostRequest target,StudyExitState? exit=null,Func<Task<StudyExitState?>>? exitRead=null,Action<StudyExitState>? failed=null)
    {
        if (!Protocol.NoiseInterruption(target)) return target;
        var reply = await ReadStateAsync(owner, pipe);
        if (reply is null) return null;
        if (reply.Outcome != "Succeeded" || reply.NoiseProtection is not { } state)
        { MessageBox.Show(owner, reply.Message, "无法核实定时保护"); return null; }
        if (!state.Protected) return target;
        if (!state.Configured)
        {
            MessageBox.Show(owner, "本机尚未设置管理口令。请从班级大屏网页验证学校 PIN 后结束本次监测；再在噪音监测页设置本机管理口令。", "定时监测受到保护");
            return null;
        }
        var dialog = new NoiseManagementDialog(owner, pipe, state, target,exit,exitRead);
        bool succeeded=dialog.ShowDialog()==true;
        if(dialog.FailedExit is {} denied)failed?.Invoke(denied);
        return succeeded && dialog.Ticket is { } ticket ? target with { NoiseAuthorization = ticket } : null;
    }
    internal static async Task ConfigureAsync(Window owner, string pipe)
    {
        var reply = await ReadStateAsync(owner, pipe);
        if (reply is null) return;
        if (reply.NoiseProtection is not { } state || reply.Outcome != "Succeeded")
        { MessageBox.Show(owner, reply.Message, "无法读取管理设置"); return; }
        if (state.ManagementSource == "School")
        { MessageBox.Show(owner, CalibrationAccess.Hint(state) + "\n学校口令由学校管理员在 NPClassworks 后台设置，本机不能覆盖。", "学校管理口令"); return; }
        if (state.Protected && !state.Configured)
        { MessageBox.Show(owner, "首次设置需在没有定时监测时进行。请先从大屏网页验证学校 PIN 后结束本次监测。", "本机管理口令"); return; }
        new NoiseManagementDialog(owner, pipe, state, null).ShowDialog();
    }
}
