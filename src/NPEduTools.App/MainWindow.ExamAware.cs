using System.Windows;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class MainWindow
{
    private ExamAwareWindow? _examAwareWindow;
    private void OpenExamAwareClicked(object sender, RoutedEventArgs e) => ShowExamAware();
    private void ShowExamAware()
    {
        _quick?.Collapse(false);
        _examAwareWindow ??= new ExamAwareWindow(_pipe);
        _examAwareWindow.Show();
        if (_examAwareWindow.WindowState == WindowState.Minimized) _examAwareWindow.WindowState = WindowState.Normal;
        _examAwareWindow.Activate();
    }
    private async void OpenExamAwareQuick()
    {
        _quick?.Collapse(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var result = await HostClient.RequestAsync(_pipe, "examaware.start", deadline.Token);
            if (result.Outcome != "Accepted") ShowExamAware();
        }
        catch (Exception ex) when (ex is System.IO.IOException or TimeoutException or OperationCanceledException or System.Text.Json.JsonException)
        { if (!_lifetime.IsCancellationRequested) ShowExamAware(); }
    }
}
