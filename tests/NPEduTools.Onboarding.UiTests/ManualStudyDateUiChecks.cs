using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Host;

internal static partial class Program
{
    private static void RunManualDateBoundaryChecks(NoiseWindow window, ManualStudyService manual)
    {
        var previous = SynchronizationContext.Current;
        var context = new QuickEventContext();
        Exception? eventError = null;
        void OnError(object sender, DispatcherUnhandledExceptionEventArgs e) { eventError = e.Exception; e.Handled = true; }
        Application.Current.DispatcherUnhandledException += OnError;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var initial = manual.Status();
            foreach (bool start in new[] { true, false })
            foreach (bool minimum in new[] { true, false })
            {
                Check(window, "UseStart").IsChecked = start;
                Check(window, "UseEnd").IsChecked = !start;
                var date = (DatePicker)window.FindName(start ? "ManualStartDate" : "ManualEndDate");
                var time = (TextBox)window.FindName(start ? "ManualStartTime" : "ManualEndTime");
                date.SelectedDate = minimum ? DateTime.MinValue.Date : DateTime.MaxValue.Date;
                time.Text = minimum ? "00:00" : "23:59";
                PumpUntil(() => Button(window, "StartButton").IsEnabled, "manual form did not become available");
                Click(window, "StartButton"); SettleManagementEvent(context);
                Assert(eventError is null, "invalid manual date escaped its async UI event: " + eventError);
                Assert(manual.Status() == initial && Button(window, "StartButton").IsEnabled,
                    "invalid manual date changed the session or left the form stuck");
                Assert(!string.IsNullOrWhiteSpace(((TextBlock)window.FindName("MessageText")).Text) &&
                    date.SelectedDate == (minimum ? DateTime.MinValue.Date : DateTime.MaxValue.Date) && time.Text == (minimum ? "00:00" : "23:59"),
                    "invalid manual date has no feedback or its draft was discarded");
                Checks.Add($"manual {(start ? "start" : "end")}/{(minimum ? "minimum" : "maximum")} date: local conversion/Host rejection has no unhandled event, session mutation or lost draft; timezone={TimeZoneInfo.Local.Id}");
            }
        }
        finally
        {
            Application.Current.DispatcherUnhandledException -= OnError;
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
