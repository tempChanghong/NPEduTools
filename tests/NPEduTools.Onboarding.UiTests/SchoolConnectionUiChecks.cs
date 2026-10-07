using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunSchoolConnectionChecks()
    {
        NpepState state = new(1, "ACTIVE", "OFFLINE", "证书恢复后将自动连接。", Error: "TLS_VALIDATION_FAILED",
            Origin: "https://school.example", LastReceivedAt: "2026-10-07T01:00:00Z");
        bool unavailable = false;
        bool invalidProtocol = false;
        var requests = new List<HostRequest>();
        var session = new NpepConnectionSession((request, _) =>
        {
            requests.Add(request);
            if (unavailable) throw new IOException("isolated Host unavailable");
            if (invalidProtocol) throw new InvalidDataException("isolated invalid Host receipt");
            return Task.FromResult(new HostResponse(Protocol.Version, request.RequestId, "Succeeded", null, "隔离状态", Npep: state));
        });
        session.RefreshAsync().GetAwaiter().GetResult();
        var settings = new NpepConnectionControl(); settings.Bind(session);
        var onboarding = new NpepConnectionControl(); onboarding.Bind(session);
        var panels = new Grid(); panels.ColumnDefinitions.Add(new()); panels.ColumnDefinitions.Add(new());
        for (int i = 0; i < 2; i++)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = i == 0 ? "设置页共享控件" : "OOBE 共享控件", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 14) });
            content.Children.Add(i == 0 ? settings : onboarding);
            var scroll = new ScrollViewer { Content = content, Padding = new Thickness(18), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(scroll, i); panels.Children.Add(scroll);
        }
        var fixture = new Window { Title = "学校互联提示 · 隔离控件测试", Width = 1180, Height = 780, Background = Brushes.White, Content = panels };
        Exercise(fixture, window =>
        {
            window.UpdateLayout();
            foreach (var view in new[] { settings, onboarding })
            {
                Assert(((TextBlock)Find(view, "NpepStatusTitle")).Text.Contains("自动重试"), "retry title binding missing");
                Assert(((TextBlock)Find(view, "NpepNextAction")).Text.Contains("每次仍验证证书"), "TLS validation advice missing");
                Assert(((TextBlock)Find(view, "NpepError")).Text.Contains("证书验证失败"), "Chinese cause binding missing");
                var expander = (Expander)Find(view, "NpepTechnicalDetailsPanel");
                Assert(!expander.IsExpanded, "technical details expanded by default");
            }
            Snapshot(window, "school-retry.png");
            var detailPanel = (Expander)Find(settings, "NpepTechnicalDetailsPanel");
            detailPanel.IsExpanded = true; window.UpdateLayout();
            var details = (TextBox)Find(settings, "NpepTechnicalDetails");
            Assert(details.IsReadOnly && details.IsVisible && details.Text.Contains("TLS_VALIDATION_FAILED"), "raw detail not copyable/visible");
            detailPanel.IsExpanded = false;
            Checks.Add("Two real shared controls show the same retry/cause/advice; collapsed details expand into a read-only diagnostic field");

            state = state with { Connection = "PAUSED", ReportingPaused = true, Error = null };
            session.RefreshAsync().GetAwaiter().GetResult(); window.UpdateLayout();
            Assert(((TextBlock)Find(onboarding, "NpepStatusTitle")).Text.Contains("互联已暂停"), "shared pause title stale");
            Assert(((Button)Find(settings, "NpepPause")).Content.ToString() == "恢复互联", "resume button changed");
            state = state with { State = "SUSPENDED", Connection = "STOPPED", ReportingPaused = false, Error = "AUTH_INVALID" };
            session.RefreshAsync().GetAwaiter().GetResult(); window.UpdateLayout();
            Assert(((TextBlock)Find(settings, "NpepStatusTitle")).Text.Contains("已停用"), "suspension title missing");
            Assert(!((Button)Find(onboarding, "NpepPause")).IsVisible, "suspended device shows pause/resume");
            Snapshot(window, "school-suspended.png");
            unavailable = true; session.RefreshAsync().GetAwaiter().GetResult(); window.UpdateLayout();
            Assert(((TextBlock)Find(onboarding, "NpepStatusTitle")).Text.Contains("未确认"), "missing Host retains old suspension/online title");
            Assert(!((TextBox)Find(settings, "NpepTechnicalDetails")).Text.Contains("AUTH_INVALID"), "old error survived missing snapshot");
            unavailable = false; state = state with { State = "ACTIVE", Connection = "ONLINE", Error = null };
            session.RefreshAsync().GetAwaiter().GetResult(); window.UpdateLayout();
            Assert(((TextBlock)Find(settings, "NpepStatusTitle")).Text.Contains("当前在线"), "recovered online title missing");
            Assert(!((TextBlock)Find(onboarding, "NpepError")).IsVisible, "recovered error still visible");
            Assert(requests.All(r => r.Capability == "npep.status" && r.Npep is null), "rendering or refreshing sent a mutation");
            Checks.Add("Shared bindings refresh pause/suspension/Host loss/recovery; existing buttons remain consistent; reads never create commands");

            session.DeviceName = "隔离设备草稿";
            invalidProtocol = true;
            Assert(!session.RefreshAsync().GetAwaiter().GetResult(), "invalid protocol was treated as a successful refresh");
            window.UpdateLayout();
            foreach (var view in new[] { settings, onboarding })
            {
                Assert(((TextBlock)Find(view, "NpepStatusTitle")).Text.Contains("未确认"), "invalid protocol retained the online title");
                Assert(!((Button)Find(view, "NpepPause")).IsEnabled && !((Button)Find(view, "NpepUnpair")).IsEnabled,
                    "invalid protocol retained school mutation controls");
                Assert(((Button)Find(view, "NpepRefresh")).IsEnabled, "invalid protocol blocked refresh");
            }
            Assert(!session.CanFinish && !session.IsWorking && session.DeviceName == "隔离设备草稿",
                "unknown school state completes onboarding, holds the gate or loses the local draft");
            Snapshot(window, "school-protocol-unconfirmed.png");
            invalidProtocol = false; state = state with { Revision = 2, ReportingPaused = true };
            Assert(session.RefreshAsync().GetAwaiter().GetResult(), "fresh school state did not recover"); window.UpdateLayout();
            foreach (var view in new[] { settings, onboarding })
                Assert(((TextBlock)Find(view, "NpepStatusTitle")).Text.Contains("互联已暂停") &&
                    ((Button)Find(view, "NpepPause")).Content.ToString() == "恢复互联" &&
                    ((Button)Find(view, "NpepPause")).IsEnabled, "valid recovery did not refresh both shared controls");
            Assert(requests.All(r => r.Capability == "npep.status" && r.Npep is null), "protocol recovery sent a mutation");
            Checks.Add("Invalid protocol clears both shared online views and completion eligibility; read-only refresh restores the latest paused state without losing the device draft");
            window.Close();
        });

        // Also verify the actual wizard at its minimum supported size, with the same isolated session.
        state = state with { State = "ACTIVE", Connection = "OFFLINE", ReportingPaused = false, Error = "TLS_VALIDATION_FAILED" };
        session.RefreshAsync().GetAwaiter().GetResult();
        var actionsType = typeof(OnboardingWindow).Assembly.GetType("NPEduTools.App.OnboardingActions")!;
        Action nothing = () => { };
        var actions = Activator.CreateInstance(actionsType, Enumerable.Repeat<object>(nothing, 9).ToArray())!;
        var constructor = typeof(OnboardingWindow).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
        var wizard = (OnboardingWindow)constructor.Invoke(["NPEduTools.Test.connection-ui." + Guid.NewGuid().ToString("N"),
            new OnboardingState(Features: OnboardingFeatures.School, Step: "school"), (Action<OnboardingState>)(_ => { }),
            session, actions, (Action<bool>)(_ => { }), null]);
        wizard.Width = 720; wizard.Height = 600;
        Exercise(wizard, window =>
        {
            window.UpdateLayout();
            var school = (NpepConnectionControl)window.FindName("SchoolConnection");
            Assert(ReferenceEquals(school.DataContext, session), "actual OOBE uses another connection session");
            Assert(((TextBlock)Find(school, "NpepStatusTitle")).Text.Contains("自动重试"), "actual OOBE loses retry title");
            Assert(Button(window, "NextButton").IsEnabled && Button(window, "NextButton").ActualHeight > 0,
                "minimum OOBE layout or offline pairing blocks the existing next button");
            Snapshot(window, "oobe-school-minimum.png");
            window.Close();
        });
        Assert(requests.All(r => r.Capability == "npep.status" && r.Npep is null), "OOBE rendering mutated school state");
        Checks.Add("Actual OOBE school step uses the shared session at 720x600; existing next action remains available for a completed offline pairing");
    }
}
