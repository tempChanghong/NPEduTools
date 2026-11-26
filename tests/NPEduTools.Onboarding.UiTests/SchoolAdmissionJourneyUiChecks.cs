using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunSchoolAdmissionJourney(bool occupied)
    {
        const string origin = "https://school.example", school = "明德中学", room = "高二（3）班", front = "screen-g2-03-front";
        string screen = occupied ? "教室后屏" : "教室前屏";
        string binding = occupied ? "screen-g2-03-back" : front;
        string screenLabel = school + " · " + room + " · " + screen;
        const string secret = "fixture-password";
        var server = new JsonObject { ["serverInstanceId"] = "0a38695a-4df3-4755-976e-92181e7928ad", ["deploymentEpoch"] = "fa9cbadd-64b2-48c2-95b4-dd5bed698226" };
        JsonObject ClassScreen(string id, string name) => new() { ["screenBindingId"] = id, ["schoolName"] = school,
            ["className"] = room, ["gradeName"] = "高二", ["screenName"] = name, ["preauthorized"] = true };
        var catalogue = new JsonObject { ["items"] = new JsonArray(ClassScreen(front, "教室前屏"), ClassScreen("screen-g2-03-back", "教室后屏")),
            ["truncated"] = false, ["supportsLookup"] = true };
        var approval = new JsonObject { ["schoolName"] = school, ["administrativeClassName"] = room, ["screenBindingName"] = screen,
            ["schoolId"] = "school-01", ["administrativeClassId"] = "g2-03", ["screenBindingId"] = binding, ["approvalId"] = "1bd589e9-2a63-4fa3-8b67-faf8ae4796b2" };
        var state = new NpepState(1, "UNPAIRED", "DISABLED", "尚未连接学校服务。");
        var commands = new List<string>();
        var installed = new Dictionary<string, string> { [front] = "前屏原设备", ["screen-g2-03-back"] = "后屏旧设备" };
        bool dropConfirmation = occupied, dropStatusOnce = false;
        Task<HostResponse> Send(HostRequest request, CancellationToken _)
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
                else if (command.Action == "replacement-class")
                {
                    Assert(command.ScreenBindingId == binding && command.Origin == origin && command.ManagementSecret == secret &&
                        command.DeviceName == state.RequestedDeviceName, "replacement did not use the original target and freshly entered class password");
                    state = state with { Revision = 5, State = "PENDING", Connection = "WAITING", PairingSource = "REPLACEMENT",
                        Error = null, Pairing = new() { ["expiresAt"] = "2026-11-22T01:15:00Z", ["userCode"] = "TEST2345" } };
                }
                else if (command.Action == "confirm")
                {
                    Assert(state.State == "APPROVED" && command.ApprovalId == (string?)approval["approvalId"], "unexpected replacement confirmation");
                    Assert(installed[front] == "前屏原设备" && installed[binding] == "后屏旧设备", "a request changed another device before local confirmation");
                    installed[binding] = "新讲台电脑";
                    if (dropConfirmation)
                    {
                        dropConfirmation = false;
                        dropStatusOnce = true;
                        state = state with { Revision = 7, State = "CONFIRMING", Connection = "OFFLINE", Error = "NETWORK_UNAVAILABLE" };
                        throw new System.IO.IOException("隔离确认回包丢失");
                    }
                    Confirmed();
                }
                else if (command.Action == "recover")
                {
                    Assert(state is { State: "CONFIRMING", PairingSource: "REPLACEMENT" } && state.RequestedScreenBindingId == binding &&
                        command.Revision == state.Revision && command.ScreenBindingId is null && command.ManagementSecret is null,
                        "the UI tried to recreate or retarget the original handover");
                    Confirmed();
                }
                else
                {
                    Assert(command is { Action: "password-class", ManagementSecret: secret } && command.ScreenBindingId == binding &&
                        command.ServerInstanceId == (string?)server["serverInstanceId"], "class admission sent an unexpected action, screen or secret");
                    outcome = "Accepted";
                    state = state with { Revision = 3, Busy = true, OperationId = request.RequestId };
                }
            }
            else if (dropStatusOnce)
            {
                dropStatusOnce = false;
                throw new System.IO.IOException("隔离后台暂未恢复");
            }
            else if (state.Busy)
            {
                state = occupied
                    ? state with { Revision = 4, Busy = false, State = "CREATING", Connection = "STOPPED", Error = "BINDING_OCCUPIED",
                        PairingSource = "CLASS_PASSWORD", Message = "指定绑定已被占用。", Catalogue = new() { ["replacementTarget"] = new JsonObject
                            { ["screenBindingId"] = binding, ["screenName"] = screenLabel, ["deviceName"] = "新讲台电脑" } } }
                    : state with { Revision = 4, Busy = false, State = "ACTIVE", Connection = "ONLINE", Error = null,
                        Approval = approval, PairingSource = "CLASS_PASSWORD", Catalogue = null, LastReceivedAt = "2026-11-21T01:15:00Z" };
            }
            return Task.FromResult(new HostResponse(Protocol.Version, request.RequestId, outcome, null, "学校互联状态", Npep: state));

            void Confirmed() => state = state with { Revision = 8, State = "ACTIVE", Connection = "ONLINE", Error = null,
                LastReceivedAt = "2026-11-21T01:20:00Z", Catalogue = null };
        }
        var session = new NpepConnectionSession(Send);
        session.RefreshAsync().GetAwaiter().GetResult();
        var main = PrivateWorkspace("NPEduTools.Test.school-journey." + Guid.NewGuid().ToString("N"));
        typeof(MainWindow).GetField("_schoolConnection", WorkspaceFlags)!.SetValue(main, session);
        WorkspaceCall(main, "InitializeNpepConnection");
        Exercise(main, window =>
        {
            Click(window, "HomeSchoolConnection", true);
            window.Width = 1040; window.Height = 760; window.UpdateLayout();
            var scroll = (ScrollViewer)window.FindName("SettingsScroll");
            var viewport = (FrameworkElement)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
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
            Assert(choices.IsVisible && choices.IsEnabled && choices.Items.Count == 2 && choices.SelectedIndex == -1, "expected two screens with no selection inferred");
            Assert(!address.IsVisible && !((PasswordBox)view.FindName("ClassManagementSecret")).IsVisible, "class step retained address or prematurely displayed a password");
            choices.SelectedValue = binding;
            var password = (PasswordBox)view.FindName("ClassManagementSecret");
            Assert(password.IsVisible && password.IsEnabled, "class management password unavailable after manual choice");
            if (!occupied) { password.BringIntoView(); Snapshot(window, "school-journey-selected.png"); }
            else
            {
                var chosenScreen = (TextBlock)Find(view, "NpepSelectedScreenName");
                Assert(chosenScreen.Text == screen, "the separate chosen target describes the other screen");
                password.Password = "old-screen-draft";
                choices.SelectedValue = front;
                Assert(chosenScreen.Text == "教室前屏" && password.Password.Length == 0, "switching to the front screen retained the rear screen secret");
                choices.SelectedValue = binding;
                Assert(chosenScreen.Text == screen, "returning to the rear screen kept the other identity");
                CaptureStep("selected", "NpepSelectedScreen", "NpepVerifyClass", "NpepApplyClass");
            }
            password.Password = secret;
            ClickVisible(FlowElements<Button>(view).Single(button => Equals(button.Content, "验证口令并连接")));
            session.RefreshAsync().GetAwaiter().GetResult(); window.UpdateLayout();
            Assert(!session.IsWorking && session.HasPairing == !occupied, "the UI treated initial Host acceptance as a confirmed pairing");
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
                    advice.Text.Contains("管理员") && advice.Text.Contains("旧") && advice.Text.Contains("换机"),
                    "occupied screen did not explain the replacement and approval choices");
                Snapshot(window, "school-journey-occupied.png");
                var replacement = (Button)Find(view, "NpepRequestReplacement");
                var newSecret = (PasswordBox)view.FindName("ReplacementSecret");
                Assert(newSecret.IsVisible && newSecret.Password.Length == 0 && replacement.IsEnabled &&
                    ((TextBlock)Find(view, "NpepReplacementTarget")).Text == screenLabel, "missing or pre-filled replacement form");
                CaptureStep("occupied", "NpepReplacementTarget", "NpepRequestReplacement");
                ClickVisible(replacement);
                Assert(commands.SequenceEqual(["inspect", "password-class"]), "empty password submitted a replacement request");
                newSecret.Password = secret;
                ClickVisible(replacement);
                window.UpdateLayout();
                Assert(!newSecret.IsVisible && newSecret.Password.Length == 0 && !session.ShowCode && session.Title.Contains("审核"),
                    "pending replacement retained the secret or prompted for a legacy pairing code");
                var original = commands.ToArray();
                Assert(installed[front] == "前屏原设备" && installed[binding] == "后屏旧设备" &&
                    !((Button)Find(view, "NpepRecover")).IsVisible && ((TextBlock)Find(view, "NpepCurrentRequestTarget")).Text == screenLabel,
                    "waiting did not preserve the old device and original request without retrying");
                Click(window, "NpepRefresh", true);
                CaptureStep("waiting", "NpepCurrentRequestTarget", "NpepRequestExpiry", "NpepRefresh");
                Assert(commands.SequenceEqual(original), "refresh during administrator approval resent a command");
            }
            Assert(commands.SequenceEqual(occupied ? ["inspect", "password-class", "replacement-class"] : new[] { "inspect", "password-class" }),
                "admission sent a duplicate or unsolicited command");
            if (occupied)
            {
                Click(window, "HomeNav");
                state = state with { Revision = 6, State = "APPROVED", Approval = approval };
                Click(window, "HomeSchoolConnection", true);
                var review = (CheckBox)Find(view, "NpepBindingConfirmed");
                var confirmButton = (Button)Find(view, "NpepConfirm");
                Assert(!confirmButton.IsEnabled && !session.CanFinish && session.BoundScreen == screen && installed[binding] == "后屏旧设备",
                    "on re-entry the approved rear screen was not awaiting local confirmation or the old device already moved");
                review.IsChecked = true;
                Click(window, "HomeNav");
                Click(window, "HomeSchoolConnection", true);
                Assert(review.IsChecked != true && !confirmButton.IsEnabled && installed[binding] == "后屏旧设备", "leaving reused a local consent or retired old device");
                CaptureStep("approved", "NpepBinding", "NpepBindingConfirmed", "NpepConfirm");
                review.IsChecked = true;
                ClickVisible(confirmButton);
                Assert(status.Text.Contains("未确认") && !address.IsVisible && !session.CanFinish, "lost confirmation prematurely offered a new address or completed pairing");
                Click(window, "HomeNav");
                Click(window, "HomeSchoolConnection", true);
                Assert(!address.IsVisible && !session.CanFinish, "one failed refresh discarded the unconfirmed operation view");
                Click(window, "NpepRefresh", true);
                Assert(!session.CanFinish && session.CurrentRequestTarget == screenLabel, "fresh read lost the original rear-screen operation");
                CaptureStep("recover", "NpepCurrentRequestTarget", "NpepRecover");
                ClickVisible((Button)Find(view, "NpepRecover"));
                Assert(session.CanFinish && session.BoundScreen == screen && installed[front] == "前屏原设备" && installed[binding] == "新讲台电脑" &&
                    commands.SequenceEqual(["inspect", "password-class", "replacement-class", "confirm", "recover"]),
                    "receipt recovery changed the front screen or replayed the handover");
                CaptureStep("bound", "NpepBoundSchool", "NpepBoundClass", "NpepBoundScreen", "NpepReceipt");
            }
            window.Close();

            void CaptureStep(string phase, params string[] controls)
            {
                foreach (int width in new[] { 1040, 840 })
                {
                    int height = width == 1040 ? 760 : 600;
                    window.Width = width; window.Height = height;
                    window.UpdateLayout();
                    scroll.ScrollToTop();
                    window.UpdateLayout();
                    Snapshot(window, $"school-rear-{phase}-{width}-{height}.png");
                    foreach (var id in controls.Prepend("NpepStatusTitle"))
                    {
                        var item = (FrameworkElement)Find(view, id);
                        var bounds = item.TransformToAncestor(viewport).TransformBounds(new Rect(item.RenderSize));
                        Assert(item.IsVisible && bounds.Top >= -1 && bounds.Bottom <= viewport.ActualHeight + 1,
                            $"{phase} {width}×{height}: {id} lies at {bounds.Top:0}–{bounds.Bottom:0} outside first viewport {viewport.ActualHeight:0}");
                    }
                    foreach (string id in new[] { "MinimizeWindow", "MaximizeRestoreWindow", "CloseWindow" })
                        Assert(((Button)Find(window, id)).IsVisible, "school admission lost its main caption " + id);
                    Assert(((FrameworkElement)window.FindName("NotificationWorkspaceEntry")).IsVisible == (phase == "bound"),
                        "connected-only controls were competing with the current application or missing once connected");
                    if (phase == "selected")
                    {
                        var actionCard = (Border)view.FindName("ClassActionCard");
                        Assert(Grid.GetColumn(actionCard) == (width == 1040 ? 2 : 0), "selection and action did not adapt to the main window");
                    }
                }
            }
        });
        if (!occupied)
        {
            Checks.Add("Home to school service, explicit confirmation, manual screen selection, preauthorized admission and first receipt");
            return;
        }

        Checks.Add("Main-window rear screen: wide/narrow selection, occupied, waiting, approved, recovery and bound; lost receipt reads the original before one recovery, front screen unchanged in the isolated state fixture");
        installed[binding] = "后屏旧设备";
        commands.Clear();
        var restored = new NpepConnectionSession(Send);
        var actionsType = typeof(OnboardingWindow).Assembly.GetType("NPEduTools.App.OnboardingActions")!;
        Action nothing = () => { };
        var actions = Activator.CreateInstance(actionsType, Enumerable.Repeat<object>(nothing, 9).ToArray())!;
        var constructor = typeof(OnboardingWindow).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
        var wizard = (OnboardingWindow)constructor.Invoke(["NPEduTools.Test.replacement-oobe." + Guid.NewGuid().ToString("N"),
            new OnboardingState(Features: OnboardingFeatures.School, Step: "school"), (Action<OnboardingState>)(_ => { }),
            restored, actions, (Action<bool>)(_ => { }), null]);
        wizard.Width = 720; wizard.Height = 600;
        Exercise(wizard, window =>
        {
            var view = (NpepConnectionControl)window.FindName("SchoolConnection");
            state = state with { Revision = 6, State = "APPROVED", Approval = approval };
            restored.RefreshAsync().GetAwaiter().GetResult();
            window.UpdateLayout();
            var confirmButton = (Button)Find(view, "NpepConfirm");
            Assert(!confirmButton.IsEnabled && !restored.CanFinish && !commands.Contains("confirm") &&
                ((TextBlock)Find(view, "NpepBinding")).Text.Contains(room) && Equals(confirmButton.Content, "确认交接并启用本机"),
                "opening the wizard implicitly confirmed or failed to present replacement approval");
            ((CheckBox)Find(view, "NpepBindingConfirmed")).IsChecked = true;
            ClickVisible(confirmButton);
            Assert(restored.CanFinish && commands.SequenceEqual(["confirm"]),
                "explicit local handover did not complete in the wizard");
            window.Close();
        });
        Checks.Add("Settings submits a replacement only with a fresh class password; restored minimum-size OOBE waits for explicit local confirmation of the approved target");

        static void ClickVisible(Button button)
        {
            Assert(button.IsVisible && button.IsEnabled, "attempt to activate a hidden or disabled classroom action");
            button.BringIntoView();
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        }
    }
}
