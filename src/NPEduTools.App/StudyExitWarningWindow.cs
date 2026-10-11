using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NPEduTools.Contracts;
using NPEduTools.Core;
namespace NPEduTools.App;
// Plain glyphs, no glow, blur, shadow, audio or sensor command.
public sealed class StudyExitWarningWindow : Window
{
    private readonly TextBlock _copy=new(){FontSize=28,TextWrapping=TextWrapping.Wrap,LineHeight=43,Foreground=Brushes.White};
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromMilliseconds(500)};
    private readonly Func<Task<StudyExitState?>> _read;
    private readonly Guid _attempt;
    private readonly long _origin=Stopwatch.GetTimestamp();
    private readonly double _remaining;
    private bool _done,_reading;
    public StudyExitWarningWindow(Window owner,StudyExitState state,Func<Task<StudyExitState?>> read)
    {
        Owner=owner;_attempt=state.AttemptId;_remaining=Math.Clamp(state.WarningRemainingSeconds,0,30);_read=read;
        Title="NPEduTools · 自习退出验证未通过";WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;
        Left=Top=0;Width=SystemParameters.PrimaryScreenWidth;Height=SystemParameters.PrimaryScreenHeight;Topmost=true;ShowInTaskbar=false;Background=new SolidColorBrush(Color.FromRgb(5,12,17));
        var panel=new Grid{Width=1920,Height=1080};
        var full=new Grid{Background=new SolidColorBrush(Color.FromArgb(238,158,23,32))};
        panel.ColumnDefinitions.Add(new(){Width=new(2.35,GridUnitType.Star)});panel.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
        var left=new TextBlock{Text=StudyExitNotice.Translations,FontFamily=new("Segoe UI"),FontSize=49,LineHeight=91,Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center,Margin=new(64,40,40,40),TextAlignment=TextAlignment.Left};
        var right=new StackPanel{VerticalAlignment=VerticalAlignment.Center,Margin=new(0,40,62,40)};
        right.Children.Add(_copy);right.Children.Add(new TextBlock{Text="30 秒后返回自习展示 · 后台监测继续",FontSize=23,TextWrapping=TextWrapping.Wrap,Foreground=Brushes.White,Margin=new(0,30,0,0)});
        Grid.SetColumn(right,1);panel.Children.Add(left);panel.Children.Add(right);full.Children.Add(new Viewbox{Stretch=Stretch.Uniform,Child=panel});Content=full;_copy.Text=StudyExitNotice.Explanation(state);
        _timer.Tick+=Tick;Loaded+=(_,_)=>_timer.Start();Closed+=(_,_)=>_timer.Stop();Closing+=(_,e)=>{if(!_done)e.Cancel=true;};
    }
    private async void Tick(object? sender,EventArgs args)
    {
        if(Stopwatch.GetElapsedTime(_origin).TotalSeconds>=_remaining){Finish();return;}
        if(_reading)return;_reading=true;
        try{var state=await _read();if(_done)return;if(state is not null){if(state.AttemptId!=_attempt || state.State!="Failed"){Finish();return;}_copy.Text=StudyExitNotice.Explanation(state);}}
        finally{_reading=false;}
    }
    private void Finish(){if(_done)return;_done=true;_timer.Stop();Close();}
    public void Shutdown()=>Finish();
}
