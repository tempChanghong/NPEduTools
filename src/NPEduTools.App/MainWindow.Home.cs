using System.Windows;
using System.Windows.Controls.Primitives;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class MainWindow
{
    private UniformGrid? _homeShortcutGrid;
    private readonly StudyDisplayReturn _homeSchoolDisplay = new(), _homeManualDisplay = new();
    private bool _homeSchoolPresentationPriority, _homeHomeworkBusy;

    private void HomeLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement page) return;
        HomeCurrentTasks.Columns = HomeUpcoming.Columns = page.ActualWidth >= 500 ? 2 : 1;
        if (_homeShortcutGrid is not null) _homeShortcutGrid.Columns = HomeShortcutColumns(page.ActualWidth);
    }

    private static int HomeShortcutColumns(double width) => width >= 720 ? 3 : width >= 500 ? 2 : 1;

    private void HomeShortcutsLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is UniformGrid grid)
        {
            _homeShortcutGrid = grid;
            grid.Columns = HomeShortcutColumns(HomePage.ViewportWidth);
        }
    }

    private void HomeSchoolConnectionClicked(object sender, RoutedEventArgs e) => ShowSettingsCategory("Npep");

    private void ObserveHomeSchool(HostResponse? response)
    {
        HomeSchoolStatus.Text = response?.Npep is { } connection ? NpepConnectionPresentation.Title(connection) : "连接状态待确认";
        _homeSchoolPresentationPriority = response is not null &&
            (SchoolStudyTakesPriority(response) || response.Study?.ProjectionPriority == true);
        ObserveHomeHomework(_homeSchoolDisplay, response);
    }

    private void ObserveHomeHomework(StudyDisplayReturn display, HostResponse? response)
    {
        display.Observe(response is not null && !SchoolLinkWithdrawn(response) &&
            response.Study is { Homework: { State: "READY", Chunks: > 0 } } study && study.Settings?.Homework.Enabled != false ? study : null);
        RefreshHomeHomeworkAction();
    }

    private bool HomeHomeworkAvailable => _homeSchoolDisplay.Available || !_homeSchoolPresentationPriority && _homeManualDisplay.Available;
    private void RefreshHomeHomeworkAction() => HomeHomeworkButton.IsEnabled = !_homeHomeworkBusy && HomeHomeworkAvailable;

    private async void HomeHomeworkClicked(object sender, RoutedEventArgs e)
    {
        if (_homeHomeworkBusy || SchoolStudyPresentationBlocked) return;
        bool manual = !_homeSchoolDisplay.Available && !_homeSchoolPresentationPriority;
        var display = manual ? _homeManualDisplay : _homeSchoolDisplay;
        if (display.Request(manual) is not { } request)
        {
            RefreshHomeHomeworkAction();
            return;
        }

        _homeHomeworkBusy = true;
        display.Observe(null);
        RefreshHomeHomeworkAction();
        try
        {
            var reply = await ManagementRequestAsync(request);
            if (!_lifetime.IsCancellationRequested && HomePage.IsVisible && reply.Outcome != "Succeeded") HomeMessage.Text = reply.Message;
        }
        catch (Exception error) when (IsManagementError(error))
        {
            if (!_lifetime.IsCancellationRequested && HomePage.IsVisible) HomeMessage.Text = "展示恢复结果待确认，请查看自习状态。";
        }
        finally
        {
            _homeHomeworkBusy = false;
            RefreshHomeHomeworkAction();
        }
    }
}
