using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunSchoolAdmissionJourney(bool occupied)
    {
        const string origin = "https://school.example", school = "明德中学", room = "高二（3）班", screen = "教室前屏";
        const string binding = "screen-g2-03-front";
        const string secret = "fixture-password";
        var server = new JsonObject { ["serverInstanceId"] = "0a38695a-4df3-4755-976e-92181e7928ad", ["deploymentEpoch"] = "fa9cbadd-64b2-48c2-95b4-dd5bed698226" };
        var catalogue = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["screenBindingId"] = binding, ["schoolName"] = school,
            ["className"] = room, ["gradeName"] = "高二", ["screenName"] = screen, ["preauthorized"] = true }), ["truncated"] = false, ["supportsLookup"] = true };
        var approval = new JsonObject { ["schoolName"] = school, ["administrativeClassName"] = room, ["screenBindingName"] = screen,
            ["schoolId"] = "school-01", ["administrativeClassId"] = "g2-03", ["screenBindingId"] = binding, ["approvalId"] = "1bd589e9-2a63-4fa3-8b67-faf8ae4796b2" };
        var state = new NpepState(1, "UNPAIRED", "DISABLED", "尚未连接学校服务。");
        var commands = new List<string>();
        var session = new NpepConnectionSession((request, _) =>
        {
            var outcome = "Succeeded";
            if (request.Npep is { } command)
            {
                Assert(NpepContract.Valid(command), "invalid command during classroom admission");
                commands.Add(command.Action);
                if (command.Action == "inspect")
                {
                    Assert(command.Origin == origin, "inspection sent a different address");
                    state = state with { Revision = 2, Connection = "VERIFIED", Origin = origin, Server = server, Catalogue = catalogue };
                }
                else
                {
                    Assert(command is { Action: "password-class", ScreenBindingId: binding, ManagementSecret: secret } &&
                        command.ServerInstanceId == (string?)server["serverInstanceId"], "class admission sent an unexpected action, screen or secret");
                    outcome = "Accepted";
                    state = state with { Revision = 3, Busy = true, OperationId = request.RequestId };
                }
            }
            else if (state.Busy)
            {
                state = occupied
                    ? state with { Revision = 4, Busy = false, State = "CREATING", Connection = "STOPPED", Error = "BINDING_OCCUPIED",
                        PairingSource = "CLASS_PASSWORD", Message = "指定绑定已被占用。", Catalogue = null }
                    : state with { Revision = 4, Busy = false, State = "ACTIVE", Connection = "ONLINE", Error = null,
                        Approval = approval, PairingSource = "CLASS_PASSWORD", Catalogue = null, LastReceivedAt = "2026-11-21T01:15:00Z" };
            }
            return Task.FromResult(new HostResponse(Protocol.Version, request.RequestId, outcome, null, "学校互联状态", Npep: state));
        });
        session.RefreshAsync().GetAwaiter().GetResult();
        var main = PrivateWorkspace("NPEduTools.Test.school-journey." + Guid.NewGuid().ToString("N"));
        typeof(MainWindow).GetField("_schoolConnection", WorkspaceFlags)!.SetValue(main, session);
        WorkspaceCall(main, "InitializeNpepConnection");
        Exercise(main, window =>
        {
            Click(window, "HomeSchoolConnection", true);
            window.Width = 1040; window.Height = 760; window.UpdateLayout();
            var view = (NpepConnectionControl)window.FindName("SettingsSchoolConnection");
            var address = (TextBox)Find(view, "NpepOrigin");
            Assert(address.IsVisible && address.IsEnabled && !((PasswordBox)view.FindName("ClassManagementSecret")).IsVisible,
                "first connection should ask for a service address before any classroom credential");
            address.Text = origin;
            if (!occupied) Snapshot(window, "school-journey-address.png");
            ClickVisible((Button)Find(view, "NpepInspect"));
            var confirm = (CheckBox)Find(view, "NpepServerConfirmed");
            Assert(confirm.IsVisible && confirm.IsEnabled && confirm.IsChecked != true, "expected manual confirmation of the inspected school service");
            confirm.IsChecked = true;
            window.UpdateLayout();
            var choices = (ComboBox)view.FindName("RequestedClass");
            Assert(choices.IsVisible && choices.IsEnabled && choices.Items.Count == 1 && choices.SelectedIndex == -1, "expected manual, unselected class catalogue");
            Assert(!address.IsVisible && !((PasswordBox)view.FindName("ClassManagementSecret")).IsVisible, "class step retained address or prematurely displayed a password");
            choices.SelectedIndex = 0;
            var password = (PasswordBox)view.FindName("ClassManagementSecret");
            Assert(password.IsVisible && password.IsEnabled, "class management password unavailable after manual choice");
            if (!occupied) { password.BringIntoView(); Snapshot(window, "school-journey-selected.png"); }
            password.Password = secret;
            ClickVisible(FlowElements<Button>(view).Single(button => Equals(button.Content, "验证口令并连接")));
            session.RefreshAsync().GetAwaiter().GetResult(); window.UpdateLayout();
            Assert(!session.IsWorking && session.HasPairing == !occupied, "the UI treated initial Host acceptance as a confirmed pairing");
            var scroll = (ScrollViewer)window.FindName("SettingsScroll");
            var viewport = (FrameworkElement)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
            var status = (TextBlock)Find(view, "NpepStatusTitle");
            PumpUntil(() =>
            {
                window.UpdateLayout();
                var bounds = status.TransformToAncestor(viewport).TransformBounds(new Rect(status.RenderSize));
                return status.IsVisible && bounds.Top >= -1 && bounds.Bottom <= viewport.ActualHeight + 1;
            }, "after submitting a credential, the resulting connection status stayed outside the viewport");
            // Auto density may settle while polling. Render every comparison at the same logical window size.
            window.Width = 1040; window.Height = 760; window.UpdateLayout();
            Snapshot(window, occupied ? "school-journey-occupied-return.png" : "school-journey-online-return.png");
            scroll.ScrollToTop();
            window.UpdateLayout();
            if (!occupied)
            {
                foreach (var (id, text) in new[] { ("NpepBoundSchool", school), ("NpepBoundClass", room), ("NpepBoundScreen", screen) })
                {
                    var field = (TextBlock)Find(view, id);
                    var bounds = field.TransformToAncestor(viewport).TransformBounds(new Rect(field.RenderSize));
                    Assert(field.IsVisible && field.Text == text && bounds.Top >= -1 && bounds.Bottom <= viewport.ActualHeight + 1,
                        id + " missing from the connected first viewport");
                }
                Assert(((TextBlock)Find(view, "NpepReceipt")).Text.Contains("2026-11-21"), "confirmed device lost its receipt");
                Snapshot(window, "school-journey-online.png");
            }
            else
            {
                var advice = (TextBlock)Find(view, "NpepNextAction");
                Assert(!session.CanFinish && status.Text.Contains("大屏") && advice.IsVisible &&
                    advice.Text.Contains("管理员") && advice.Text.Contains("旧") && advice.Text.Contains("恢复"),
                    "occupied screen did not explain how to review the old terminal and resume");
                Snapshot(window, "school-journey-occupied.png");
            }
            Assert(commands.SequenceEqual(["inspect", "password-class"]), "admission sent a duplicate or unsolicited command");
            window.Close();
        });
        Checks.Add(occupied ? "Occupied screen returns a stopped candidate without sending any revoke or replacement command"
            : "Home to school service, explicit confirmation, manual screen selection, preauthorized admission and first receipt");

        static void ClickVisible(Button button)
        {
            Assert(button.IsVisible && button.IsEnabled, "attempt to activate a hidden or disabled classroom action");
            button.BringIntoView();
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        }
    }
}
