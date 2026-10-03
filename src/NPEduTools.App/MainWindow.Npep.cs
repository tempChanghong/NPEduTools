using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class MainWindow
{
    private NpepConnectionSession? _schoolConnection;
    private NpepConnectionSession SchoolConnectionSession => _schoolConnection ??= new(
        (request, token) => HostClient.RequestAsync(_pipe, request, token), _lifetime.Token);
    private void InitializeNpepConnection() => SettingsSchoolConnection.Bind(SchoolConnectionSession);
    private Task RefreshNpepAsync() => SchoolConnectionSession.RefreshAsync();
    private async Task NpepPollAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            if (IsVisible && SettingsPage.Visibility == System.Windows.Visibility.Visible && NpepSettingsPanel.Visibility == System.Windows.Visibility.Visible)
                await RefreshRemoteExamAsync();
            // Pairing polling belongs to the visible shared connection control.
            try { await Task.Delay(1000, _lifetime.Token); } catch (OperationCanceledException) { break; }
        }
    }
}
