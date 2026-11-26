using System.Windows;
using System.Windows.Controls;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class NoiseReferenceWindow
{
    private SoundMeterDevice? _usbWorkspaceMeter;
    private Action? _usbWorkspaceChanged;
    private bool _usbNeedsMeterCheck;
    private bool InUsbWorkspace => _usbWorkspaceMeter is not null;
    private bool CredentialsAreHidden() => ReferenceCredentials.Visibility == Visibility.Collapsed ||
        ManagementSecret.Password.Length == 0;

    internal void UseUsbWorkspace(SoundMeterDevice meter, Action changed)
    {
        _usbWorkspaceMeter = meter;
        _usbWorkspaceChanged = changed;
        ReferenceRoot.Tag = "UsbWorkspace";
        Heading.Visibility = DeviceStatus.Visibility = StepNavigation.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(ReferenceHeadingActions, Dock.Right);
        RefreshStatus.Content = "刷新状态";
        RefreshStatus.Padding = new Thickness(8, 3, 8, 3);
        RefreshStatus.MinHeight = 32;
        BeginDraft.Content = "准备同期采样";
        UpdateButtons();
    }

    internal int UsbWorkspaceStep => _step == 3 ? 2 : 1;
    internal bool UsbNeedsMeterCheck => _usbNeedsMeterCheck;
    internal bool CanChangeUsbMeter => !_busy && !_closing && !_verifying && !HasActiveSample;
    internal bool CanShowUsbPreparation => CanChangeUsbMeter && _step != 1 && !_usbNeedsMeterCheck;
    private bool CanNavigateUsbDraft => CanChangeUsbMeter && _connected && !_usbNeedsMeterCheck &&
        CurrentUsbDraft(_state)?.Workflow == SelectedWorkflow;
    internal bool CanShowUsbSampling => CanNavigateUsbDraft && _step != 2;
    internal bool CanShowUsbReview => CanNavigateUsbDraft && _step != 3 && CurrentUsbDraft(_state)?.Samples.Count > 0;

    internal void ShowUsbPreparation() { if (CanShowUsbPreparation) Navigate(1); }
    internal void ShowUsbSampling() { if (CanShowUsbSampling) Navigate(2); }
    internal void ShowUsbReview() { if (CanShowUsbReview) Navigate(3); }

    internal string UsbWorkspaceContext
    {
        get
        {
            if (_usbWorkspaceMeter is not { } meter) return "";
            string device = _state?.DeviceName ?? (_state is null ? "待核对" :
                _state.DeviceId is null ? "请先在自习设备设置中保存" : "已选定");
            string status = !_connected ? "后台状态待核对" :
                _usbNeedsMeterCheck ? "上次 USB 采样中断，需重新查找" :
                CurrentUsbDraft(_state) is null ? "已选定，待准备" :
                _state!.State == "Sampling" ? (_state.Meter?.Reading is null ? "正在等待同期读数" : "同期读取中") :
                _state.State == "Busy" ? "运行保护或采集占用" : "已找到本次仪表";
            return $"声级计 · {meter.Name}　|　麦克风 · {device}　|　{status}";
        }
    }

    private NoiseReferenceDraft? CurrentUsbDraft(NoiseReferenceState? state) =>
        state?.Draft is { } draft && draft.MeterDevicePath == _meterPath && draft.DeviceId == state.DeviceId ? draft : null;

    private static bool UsableFieldSegment(NoiseReferenceSample sample, string role) =>
        sample.Role == role && sample.Quality == "Good" && sample.SameWindowConfirmed &&
        NoiseReferenceContract.MeterEvidenceValid(sample.MeterEvidence, "Field");

    private bool CanResumeUsbCheck(NoiseReferenceState? state) =>
        CurrentUsbDraft(state) is { Workflow: "Field" } draft &&
        draft.Samples.Any(sample => UsableFieldSegment(sample, "Primary")) &&
        !draft.Samples.Any(sample => UsableFieldSegment(sample, "Check"));

    private void RenderUsbSamplingStatus(NoiseReferenceState state, NoiseReferenceDraft? draft)
    {
        if (_usbNeedsMeterCheck)
        {
            SamplingInstructions.Text = "切回连接步骤重新查找；已完成的有效采样会保留。";
            SamplingStatus.Text = "上次 USB 采样中断。请重新查找原仪表，再核对已记录的采样段。";
            return;
        }
        if (draft?.Workflow != "Field") return;
        if (state.State == "Sampling")
        {
            SamplingInstructions.Text = "保持仪表、麦克风位置和输入设置不变。";
            string segment = draft.Samples.Any(sample => UsableFieldSegment(sample, "Primary")) ? "第 2 段 · 复核" : "第 1 段 · 参考";
            SamplingStatus.Text = $"{segment}　|　有效采样 {state.MeasuredSeconds:F1} / {Progress.Maximum:g} 秒。";
        }
        if (state.State != "Ready") return;
        if (CanResumeUsbCheck(state))
        {
            SamplingInstructions.Text = "继续第二段时，保持原仪表、麦克风和输入设置。第一段有效记录会保留。";
            SamplingStatus.Text = "第 1 段已记录 · 第 2 段待采。";
            StartSample.Content = "继续第 2 段";
        }
        else if (draft.Samples.Any(sample => UsableFieldSegment(sample, "Check")))
        {
            SamplingStatus.Text = "两段均已记录，可查看复核结果。";
            StartSample.Content = "重采两段…";
        }
        else
        {
            SamplingInstructions.Text = "每段先预热约 5 秒，再采 30 秒。保持声级计、麦克风位置和输入设置不变。";
            SamplingStatus.Text = "第 1 段待采 · 第一段有效后才自动开始第 2 段。";
        }
    }

    private void ObserveUsbInterruption(NoiseReferenceState? previous, NoiseReferenceState state)
    {
        if (!InUsbWorkspace || previous is null || state.State != "Ready" ||
            state.CaptureMessage?.Contains("USB", StringComparison.Ordinal) != true ||
            CurrentUsbDraft(state) is not { } draft ||
            CurrentUsbDraft(previous) is not { } old || old.Id != draft.Id) return;
        if (draft.Samples.Any(sample => sample.Quality != "Good" && !old.Samples.Any(prior => prior.Id == sample.Id)))
            _usbNeedsMeterCheck = true;
    }
}
