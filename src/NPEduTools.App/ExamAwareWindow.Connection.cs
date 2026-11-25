using System.Windows;
using System.Windows.Controls;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class ExamAwareWindow
{
    private enum ConnectionStep { Path, Plugin, Pairing, Verify }

    private ConnectionStep? _selectedConnectionStep;
    private const string PairingExportedMessage = "配对文件已导出。到 ExamAware2 主页选择「连接 NPEduTools」导入，完成后可删除该文件。";
    private bool _manageConnection, _firstSetup, _pairingExported;

    private void UpdateConnectionColumns(double width)
    {
        bool stacked = width < 840;
        Grid.SetColumn(ConnectionSummaryActions, stacked ? 0 : 1);
        Grid.SetRow(ConnectionSummaryActions, stacked ? 1 : 0);
        ConnectionSummaryGrid.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : GridLength.Auto;
        ConnectionSummaryActions.Margin = stacked ? new Thickness(0, 12, 0, 0) : new Thickness(24, 0, 0, 0);
        ConnectionSummaryActions.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    }

    private void RenderConnectionSteps(ExamAwareStatus? state, bool needsExecutable)
    {
        bool current = state is not null && !_workspaceAwaitingStatus;
        bool hasPath = current && !needsExecutable;
        bool connected = current && state!.BridgeState == "Connected";
        bool canConfigure = current && state!.BridgeState != "Unavailable";
        bool initialSetup = needsExecutable || !connected && _firstSetup;
        bool showSteps = canConfigure && (_manageConnection || initialSetup);
        var recommended = !hasPath ? ConnectionStep.Path :
            connected || _wasConnected || _pairingExported ? ConnectionStep.Verify : ConnectionStep.Plugin;
        var step = _selectedConnectionStep ?? recommended;

        bool returnFocus = connected && !showSteps && ConnectionSteps.IsKeyboardFocusWithin;
        ConnectionSteps.Visibility = StepVisibility(showSteps);
        ConnectionMaintenance.Visibility = StepVisibility(_manageConnection);
        PathStepBody.Visibility = StepVisibility(step == ConnectionStep.Path);
        PluginStepBody.Visibility = StepVisibility(step == ConnectionStep.Plugin);
        PairingStepBody.Visibility = StepVisibility(step == ConnectionStep.Pairing);
        VerifyStepBody.Visibility = StepVisibility(step == ConnectionStep.Verify);

        PathStepState.Text = hasPath ? "已保存" : "待保存";
        PluginStepState.Text = connected ? "桥接已响应" : "在本体操作";
        PairingStepState.Text = connected ? "已认证" : _pairingExported ? "待在本体导入" : "待核对";
        VerifyStepState.Text = state?.BridgeState == "UnsupportedVersion" ? "版本不支持" :
            connected ? "已连接" : _pairingExported ? "等待回报" : "待核对";

        StartButton.Visibility = StepVisibility(canConfigure && hasPath);
        StartButton.Style = (Style)FindResource(showSteps && step != ConnectionStep.Verify ? "TeacherQuiet" : "TeacherPrimary");
        ConfigureButton.Content = _manageConnection ? "收起配置" : "管理连接";
        ConfigureButton.Visibility = StepVisibility(canConfigure && (!initialSetup || _manageConnection));
        ConnectionSummaryActions.Visibility = StepVisibility(StartButton.Visibility == Visibility.Visible || ConfigureButton.Visibility == Visibility.Visible);
        if (returnFocus) ConfigureButton.Focus();
    }

    private static Visibility StepVisibility(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    private void ConfigureClicked(object sender, RoutedEventArgs e)
    {
        _manageConnection = !_manageConnection;
        _selectedConnectionStep = _manageConnection && _state?.BridgeState == "Connected" ? ConnectionStep.Path : null;
        Render(_state);
    }

    private void ConnectPathStepClicked(object sender, RoutedEventArgs e) => SelectConnectionStep(ConnectionStep.Path);
    private void ConnectPluginStepClicked(object sender, RoutedEventArgs e) => SelectConnectionStep(ConnectionStep.Plugin);
    private void ConnectPairingStepClicked(object sender, RoutedEventArgs e) => SelectConnectionStep(ConnectionStep.Pairing);
    private void ConnectVerifyStepClicked(object sender, RoutedEventArgs e) => SelectConnectionStep(ConnectionStep.Verify);

    private void SelectConnectionStep(ConnectionStep step)
    {
        _selectedConnectionStep = step;
        Render(_state);
    }

    private void ExecutableChanged(object sender, TextChangedEventArgs e)
    {
        if (SaveProgramButton is null || BrowseProgramButton is null) return;
        bool hasInput = !string.IsNullOrWhiteSpace(Executable.Text);
        SaveProgramButton.IsEnabled = hasInput;
        SaveProgramButton.Style = (Style)FindResource(hasInput ? "TeacherPrimary" : "TeacherButton");
        BrowseProgramButton.Style = (Style)FindResource(hasInput ? "TeacherButton" : "TeacherPrimary");
    }

    private void ConnectionChanged(string capability)
    {
        _pairingExported = false;
        _selectedConnectionStep = capability == "examaware.pairing.reset" ? ConnectionStep.Pairing : null;
        if (capability == "examaware.pairing.reset") _manageConnection = true;
    }
}
