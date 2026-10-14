using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunStudyConnectionVisualChecks()
    {
        var clock = new StudyUiClock();
        var surface = new StudySurface(() => Task.CompletedTask, () => { }, true, clock);
        // 2560x1440 at 200% corresponds to a 1280x720 logical workspace.
        var window = new Window { Content = surface, Width = 1280, Height = 720, WindowStyle = WindowStyle.None };
        T Element<T>(string name) where T : FrameworkElement => (T)surface.FindName(name);
        var state = StudyFixture("Active", 4500);
        var online = new NpepState(1, "ACTIVE", "ONLINE", "fixture", LastReceivedAt: "2026-10-14T12:00:00.000Z");
        try
        {
            Exercise(window, w =>
            {
                void Apply(NpepState? connection, StudyHostState? study = null) => surface.Apply(study ?? state, null, connection: connection);
                Apply(online); w.UpdateLayout();
                Assert(Element<TextBlock>("SyncText").Text == "学校互联正常", "receipt not shown on study surface");
                Assert(Element<TextBlock>("SyncText").ActualWidth < 450 && Element<TextBlock>("SyncText").FontSize < 24, "connection label dominates or clips the header");
                var position = Element<Grid>("SchoolSync").TranslatePoint(new Point(), Element<Canvas>("LayoutCanvas"));
                Assert(position.Y + Element<Grid>("SchoolSync").ActualHeight < Canvas.GetTop((UIElement)Element<TextBlock>("PhaseText").Parent), "connection overlaps phase card");
                Snapshot(w, "study-sync-online.png");
                string progress = Element<TextBlock>("ProgressPercent").Text;
                Apply(online with { Connection = "OFFLINE", Error = "NETWORK_UNAVAILABLE" }, state with { Reason = "OFFLINE_CONTINUING" });
                Assert(Element<TextBlock>("SyncText").Text == "学校正在重连 · 本次继续", "offline continuation missing");
                Assert(Element<TextBlock>("ProgressPercent").Text == progress && Element<TextBlock>("PhaseText").Text == "自习进行中", "connection label changed study progression");
                Snapshot(w, "study-sync-reconnecting.png");
                Element<Button>("SeatingButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); w.UpdateLayout();
                var seatingLabel = Element<StackPanel>("SeatingSync").Children.OfType<TextBlock>().Single();
                PumpUntil(() => seatingLabel.Text == Element<TextBlock>("SyncText").Text, "seating label did not mirror connection");
                Assert(Element<StackPanel>("SeatingSync").ActualWidth < 570, "seating connection overlaps center clock");
                Snapshot(w, "study-sync-seating.png");
                Element<Button>("CloseSeating").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Apply(online with { Connection = "STOPPED" });
                Assert(Element<TextBlock>("SyncText").Text == "学校连接已停止", "stopped connection mislabeled retrying");
                Apply(online with { ReportingPaused = true });
                Assert(Element<TextBlock>("SyncText").Text == "学校互联已暂停", "manual pause mislabeled retrying");
                Apply(online);
                clock.Ms += 1600;
                PumpUntil(() => Element<TextBlock>("SyncText").Text == "后台状态待确认", "stale Host status stayed green");
                Apply(null); Assert(Element<TextBlock>("SyncText").Text == "学校互联待确认", "old Host inherited previous online receipt");
                var manual = state with { Manual = new(Guid.NewGuid(), 1, state.Execution!.SessionId, "Active", null, null, null, true, 600) };
                Apply(online with { State = "UNPAIRED", Connection = "DISABLED" }, manual);
                Assert(Element<TextBlock>("SyncText").Text == "仅本机自习", "local manual study falsely promises school sync");
                Apply(online, manual); Assert(Element<TextBlock>("SyncText").Text == "学校互联正常", "manual study connection missing");
                Snapshot(w, "study-sync-manual.png");
                Checks.Add("Study connection: real WPF normal/retry/stop/pause/stale/old-Host/local-manual states, seating mirror and 1280x720 logical layout; no capture/network changes");
                w.Close();
            });
        }
        finally { surface.Stop(); window.Close(); }
    }
}
