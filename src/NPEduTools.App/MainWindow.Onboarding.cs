using System.IO;
using System.Windows;

namespace NPEduTools.App;

public partial class MainWindow
{
    private OnboardingStore _onboardingStore = null!;
    private OnboardingState _onboarding = new();
    private OnboardingWindow? _onboardingWindow;
    private bool _onboardingReadable = true;
    private string? _onboardingError;

    private void InitializeOnboarding()
    {
        _onboardingStore = new(OnboardingStore.PathFor(_pipe));
        try
        {
            var saved = _onboardingStore.Read();
            _onboarding = saved ?? new(Deferred: OnboardingStore.HasExistingSettings(_pipe));
            // Establish the first-launch marker before other components can create settings.
            if (saved is null) _onboardingStore.Save(_onboarding);
        }
        catch (Exception error) when (IsStartupError(error))
        {
            _onboardingReadable = false;
            _onboarding = new(Deferred: true);
            _onboardingError = "初始设置进度无法读取或保存，原记录已保留。请检查配置目录权限后重新启动。";
            HomeMessage.Text = _onboardingError;
        }
    }

    private void StartOnboarding(bool atLogin)
    {
        UpdateOnboardingEntry();
        if (_onboardingReadable && _onboarding.ShouldShow(atLogin)) ShowOnboarding();
    }
    private void UpdateOnboardingEntry() => _quick?.SetOnboardingAction(ShowOnboarding, !_onboarding.Completed);
    private void OpenOnboardingClicked(object sender, RoutedEventArgs e) => ShowOnboarding();
    private void ShowOnboarding()
    {
        _quick?.Collapse(false);
        if (_onboardingWindow is null)
        {
            _onboardingWindow = new OnboardingWindow(_pipe, _onboarding, SaveOnboarding,
                ShowSettings, () => { ShowSettings(); ExecutablePathBox.BringIntoView(); }, ShowRecording, ShowShortcutManager,
                automatic => { HideToEdge(); if (automatic) ShowRecordingPlan(); else _quick?.OpenPanel(); },
                _onboardingError);
            _onboardingWindow.Closed += (_, _) => _onboardingWindow = null;
        }
        _onboardingWindow.Show();
        _onboardingWindow.WindowState = WindowState.Normal;
        _onboardingWindow.Activate();
    }
    private void SaveOnboarding(OnboardingState state)
    {
        if (!_onboardingReadable) throw new IOException(_onboardingError);
        _onboardingStore.Save(state);
        _onboarding = state;
        UpdateOnboardingEntry();
    }
}
