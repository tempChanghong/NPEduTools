using System.IO;
using System.Windows;

namespace NPEduTools.App;

public partial class OnboardingWindow
{
    private bool _preparationBusy;

    // These status reads never pair, configure startup, enumerate recording devices or open a microphone.
    private async Task RefreshPreparationAsync()
    {
        var visit = _state;
        string step = visit.Step;
        bool CurrentVisit() => !_lifetime.IsCancellationRequested && ReferenceEquals(visit, _state);
        if (_preparationBusy || _lifetime.IsCancellationRequested || step is not ("classroom" or "noise" or "secrandom")) return;
        if (step == "secrandom") { await CheckSecRandomPreparationAsync(); return; }
        _preparationBusy = true;
        var target = step == "classroom" ? ClassroomText : NoiseText;
        target.Text = "正在读取后台状态…";
        try
        {
            string detail;
            if (step == "classroom")
            {
                var path = await RequestAsync("classisland.config.get");
                if (!CurrentVisit()) return;
                var exam = await RequestAsync("examaware.status");
                string Program(string? value) => string.IsNullOrWhiteSpace(value) ? "尚未保存位置"
                    : File.Exists(value) ? "已保存，文件可访问" : "保存的文件不可访问，请重新选择";
                detail = $"ClassIsland：{(path.Outcome == "Succeeded" && path.Launch is { StorageWarning: null } launch ? Program(launch.Settings.ExecutablePath) : "暂时无法读取配置")}\n" +
                    $"ExamAware2：{(exam.Outcome == "Succeeded" && exam.ExamAware is { } state ? Program(state.ExecutablePath) : "暂时无法读取配置")}\n" +
                    $"考试看板桥接：{(exam.Outcome == "Succeeded" && exam.ExamAware?.BridgeState == "Connected" ? "已连接；具体权限请在考试看板查看" : "尚未确认连接")}\n" +
                    "程序位置不代表完整环境已就绪。请在课堂模式中运行“首次配置检查”，核实管理员任务与桥接权限。";
            }
            else
            {
                var response = await RequestAsync("noise.status");
                detail = response.Outcome == "Succeeded" && response.Noise is { } state
                    ? $"当前监测：{state.Message}\n" +
                      (string.IsNullOrWhiteSpace(state.SelectedDeviceId) ? "尚未保存监测麦克风。请在噪音监测页选择并保存。" : "已保存监测麦克风。设备可用性和输入电平需要在监测页实测。")
                    : "暂时无法读取监测状态，请检查后台连接。";
            }
            if (CurrentVisit()) target.Text = detail;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (CurrentVisit())
                target.Text = "后台状态暂不可用。可打开对应管理页查看，或跳过后再配置。";
        }
        finally
        {
            _preparationBusy = false;
            // Revisiting the same step also needs a new read, not the previous visit's result.
            if (!_lifetime.IsCancellationRequested && !ReferenceEquals(visit, _state) && _state.Step is ("classroom" or "noise" or "secrandom"))
                _ = RefreshPreparationAsync();
        }
    }

    private async void RefreshPreparationClicked(object sender, RoutedEventArgs e) => await RefreshPreparationAsync();
    private void ClassroomClicked(object sender, RoutedEventArgs e) => _actions.Classroom();
    private void ExamAwareClicked(object sender, RoutedEventArgs e) => _actions.ExamAware();
    private void SchoolClicked(object sender, RoutedEventArgs e) => _actions.School();
    private void NoiseClicked(object sender, RoutedEventArgs e) => _actions.Noise();
    private void SecRandomClicked(object sender, RoutedEventArgs e) => _actions.SecRandom();
    private void AgreementsClicked(object sender, RoutedEventArgs e) => new AgreementsWindow(_endpoint, false) { Owner = this }.ShowDialog();
    private async Task<bool> CheckSecRandomPreparationAsync()
    {
        if (_preparationBusy || _lifetime.IsCancellationRequested) return false;
        _preparationBusy = true;
        NextButton.IsEnabled = BackButton.IsEnabled = SkipButton.IsEnabled = false;
        try
        {
            var response = await RequestAsync("secrandom.status");
            if (_lifetime.IsCancellationRequested || _state.Step != "secrandom") return false;
            var state = response.SecRandom;
            bool configured = response.Outcome == "Succeeded" && !string.IsNullOrWhiteSpace(state?.ExecutablePath) && File.Exists(state.ExecutablePath);
            SecRandomText.Text = configured
                ? $"程序位置已保存，文件可访问。\n接口：{state!.Connection}。本步不会启动软件、读取名单或执行抽取。\n请在点名页核对 SecRandom V3 接口及名单；侧栏“抽”按钮会执行真实闪抽。"
                : "尚未保存可访问的 SecRandom 程序位置。请打开点名配置，从 URL 登记查找或浏览选择程序，再保存；也可跳过。";
            if (!configured) ErrorText.Text = "请保存 SecRandom 程序位置，或选择“跳过此项”。";
            else ErrorText.Text = "";
            return configured;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        { if (!_lifetime.IsCancellationRequested) { SecRandomText.Text = "暂时无法读取点名配置，请检查后台或稍后配置。"; ErrorText.Text = "点名配置未确认，可以跳过后再设置。"; } return false; }
        finally { _preparationBusy = false; NextButton.IsEnabled = BackButton.IsEnabled = SkipButton.IsEnabled = true; }
    }
}
