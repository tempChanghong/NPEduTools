using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunClassCatalogueUiChecks()
    {
        const string origin = "https://school.example";
        var identity = new JsonObject
        {
            ["serverInstanceId"] = Guid.NewGuid().ToString(), ["deploymentEpoch"] = Guid.NewGuid().ToString()
        };
        var state = new NpepState(1, "UNPAIRED", "WAITING", "请核对学校服务。", Origin: origin, Server: identity,
            Catalogue: Quick(truncated: false, lookup: false));
        var commands = new List<NpepCommand>();
        TaskCompletionSource<HostResponse>? delayed = null;
        HostRequest? delayedRequest = null;
        bool failPage = false;
        var session = new NpepConnectionSession((request, _) =>
        {
            var command = request.Npep;
            if (command is null) return Task.FromResult(Reply(request));
            commands.Add(command);
            switch (command.Action)
            {
                case "inspect":
                    state = state with { Catalogue = Quick(truncated: true, lookup: true) };
                    break;
                case "find-class":
                    Assert(NpepContract.Valid(command), "lookup button emitted an invalid command");
                    if (command.ClassQuery == "延迟班")
                    {
                        delayed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        delayedRequest = request;
                        return delayed.Task;
                    }
                    state = state with { Catalogue = Page(command, failPage) };
                    if (failPage)
                    {
                        state = state with { Busy = true, OperationId = request.RequestId, Error = null, Message = "后台正在查找" };
                        return Task.FromResult(Reply(request, "Accepted"));
                    }
                    state = state with { Busy = false, Error = null };
                    break;
            }
            return Task.FromResult(Reply(request));
        });
        session.RefreshAsync().GetAwaiter().GetResult();
        var main = PrivateWorkspace("NPEduTools.Test.class-catalogue." + Guid.NewGuid().ToString("N"));
        typeof(MainWindow).GetField("_schoolConnection", WorkspaceFlags)!.SetValue(main, session);
        WorkspaceCall(main, "InitializeNpepConnection");
        Exercise(main, window =>
        {
            Click(window, "HomeSchoolConnection", true);
            window.Width = 900; window.Height = 680;
            window.UpdateLayout();
            var view = (NpepConnectionControl)window.FindName("SettingsSchoolConnection");
            T Control<T>(string name) where T : FrameworkElement => (T)view.FindName(name);
            var lookup = Control<Expander>("ClassLookupPanel");
            var selection = Control<ComboBox>("RequestedClass");
            var password = Control<PasswordBox>("ClassManagementSecret");
            var school = Control<TextBox>("SchoolLookupQuery");
            var classroom = Control<TextBox>("ClassLookupQuery");
            var hint = Control<TextBlock>("ClassCatalogueHint");
            var find = Control<Button>("FindClassButton");
            var previous = Control<Button>("PreviousClassPageButton");
            var next = Control<Button>("NextClassPageButton");
            ((CheckBox)Find(view, "NpepServerConfirmed")).IsChecked = true;
            Assert(selection.IsVisible && selection.Items.Count == 1 && selection.SelectedIndex == -1 && !lookup.IsVisible,
                "legacy small catalogue is no longer directly selectable");
            var alternate = Control<Expander>("WebPairingOptions");
            Assert(!alternate.IsExpanded, "a usable legacy catalogue should keep alternate pairing folded");
            state = state with { Catalogue = null };
            session.RefreshAsync().GetAwaiter().GetResult(); window.UpdateLayout();
            Assert(alternate.IsVisible && alternate.IsExpanded && ((TextBox)Find(view, "NpepWebPairingCode")).IsVisible,
                "a legacy KV without a catalogue must expose web code pairing after service confirmation");
            state = state with { Catalogue = new JsonObject { ["items"] = new JsonArray(), ["truncated"] = false } };
            session.RefreshAsync().GetAwaiter().GetResult(); window.UpdateLayout();
            Assert(alternate.IsExpanded, "a legacy KV with no available screens hid the alternate pairing route");
            state = state with { Catalogue = Quick(truncated: true, lookup: false) };
            session.RefreshAsync().GetAwaiter().GetResult(); window.UpdateLayout();
            Assert(!lookup.IsVisible && hint.Text.Contains("升级学校服务") && selection.Items.Count == 1,
                "legacy truncation does not guide the user without removing current choices");
            state = state with { Catalogue = Quick(truncated: true, lookup: true) };
            session.RefreshAsync().GetAwaiter().GetResult(); window.UpdateLayout();
            Assert(lookup.IsVisible && hint.Text.Contains("精确查找"), "new truncated catalogue has no discoverable lookup entry");
            lookup.IsExpanded = true;
            selection.SelectedIndex = 0; password.Password = "fixture-only";
            school.Text = "SCHOOL-01"; classroom.Text = "高二（3）班";
            window.UpdateLayout();
            Assert(selection.SelectedIndex == -1 && password.Password.Length == 0 && find.IsEnabled, "editing lookup retained quick-list selection or password");
            Raise(find);
            window.UpdateLayout();
            Assert(selection.Items.Count == 2 && selection.SelectedIndex == -1 && !previous.IsVisible && next.IsEnabled,
                "first page should preserve same-name screens, require manual selection and only allow forward paging");
            var choices = selection.Items.Cast<NpepConnectionSession.ClassChoice>().ToArray();
            Assert(choices[0].Id != choices[1].Id && choices[0].Name != choices[1].Name && choices.All(row =>
                row.Name.Contains("SCHOOL-01") && row.Name.Contains("G2-C03") && row.Name.Contains(row.Id)),
                "same-name results are indistinguishable or missing their codes and binding IDs");
            selection.SelectedIndex = 1;
            selection.BringIntoView(); window.UpdateLayout();
            Snapshot(window, "school-class-lookup-settings.png");
            password.Password = "fixture-only";
            session.RefreshAsync().GetAwaiter().GetResult(); window.UpdateLayout();
            Assert(selection.SelectedValue is "room-202" && password.Password == "fixture-only", "identical polling cleared the current result or password");
            failPage = true;
            Raise(next);
            window.UpdateLayout();
            Assert(selection.Items.Count == 0 && password.Password.Length == 0 && hint.Text.Contains("正在查找") &&
                !previous.IsEnabled && !Control<Button>("RetryClassPageButton").IsVisible, "Host acceptance allowed overlapping paging or appeared as an empty/error result");
            state = state with { Busy = false, Error = "RATE_LIMIT", Message = "查找过于频繁，请稍后重试。" };
            session.RefreshAsync().GetAwaiter().GetResult(); window.UpdateLayout();
            Assert(selection.Items.Count == 0 && selection.SelectedIndex == -1 && password.Password.Length == 0 &&
                Control<Button>("RetryClassPageButton").IsVisible && hint.Text.Contains("查找过于频繁"), "failed next page retained inputs or lacked retry");
            Assert(commands[^1].CatalogueAfter == "room-202" && commands[^1].SchoolQuery == "SCHOOL-01" && commands[^1].ClassQuery == "高二（3）班",
                "next page lost exact search scope");
            failPage = false;
            Raise(Control<Button>("RetryClassPageButton"));
            window.UpdateLayout();
            Assert(selection.Items.Count == 1 && selection.SelectedIndex == -1 && commands[^1].CatalogueAfter == "room-202", "retry did not fetch the same page without selection");
            selection.SelectedIndex = 0;
            password.Password = "fixture-only";
            Raise(previous);
            window.UpdateLayout();
            Assert(commands[^1] is { Action: "find-class", CatalogueAfter: null, SchoolQuery: "SCHOOL-01", ClassQuery: "高二（3）班" } &&
                selection.Items.Count == 2 && selection.SelectedIndex == -1 && password.Password.Length == 0 && !previous.IsVisible && next.IsEnabled,
                "returning to the first page did not reread its cursor or clear selection and password");
            selection.SelectedIndex = 1;
            password.Password = "fixture-only";
            Raise(next);
            window.UpdateLayout();
            Assert(commands[^1].CatalogueAfter == "room-202" && selection.Items.Count == 1 && selection.SelectedIndex == -1 &&
                password.Password.Length == 0 && previous.IsEnabled && !next.IsVisible, "forward paging after going back did not restore the second page and clear inputs");
            selection.SelectedIndex = 0;
            password.Password = "fixture-only";
            Raise(FlowElements<Button>(view).Single(button => Equals(button.Content, "验证口令并连接")));
            Assert(commands[^1].Action == "password-class" && commands[^1].ScreenBindingId == "room-203", "pairing did not use the manually selected ID");

            classroom.Text = "延迟班";
            Assert(!previous.IsVisible, "changing query conditions retained the previous search's cursors");
            Raise(find);
            Assert(delayedRequest?.Npep?.ClassQuery == "延迟班" && classroom.IsEnabled, "query input cannot change while an old lookup is pending");
            classroom.Text = "另一班";
            state = state with { Catalogue = Page(delayedRequest!.Npep!, false) };
            delayed!.SetResult(Reply(delayedRequest));
            PumpUntil(() => !session.IsWorking, "outdated lookup did not finish");
            session.RefreshAsync().GetAwaiter().GetResult(); window.UpdateLayout();
            Assert(selection.Items.Count == 0 && password.Password.Length == 0 && classroom.Text == "另一班", "late or cached page replaced changed query fields");

            classroom.Text = "延迟班";
            Raise(find);
            Click(window, "HomeNav"); window.UpdateLayout();
            state = state with { Catalogue = Page(delayedRequest!.Npep!, false) };
            delayed!.SetResult(Reply(delayedRequest));
            PumpUntil(() => !session.IsWorking, "lookup after leaving settings did not finish");
            Click(window, "HomeSchoolConnection", true); window.UpdateLayout();
            Assert(selection.Items.Count == 0 && selection.SelectedIndex == -1 && password.Password.Length == 0 &&
                Control<Button>("RestoreQuickCatalogueButton").IsVisible, "leaving and resuming showed results from the previous page visit");
            Raise(Control<Button>("RestoreQuickCatalogueButton")); window.UpdateLayout();
            Assert(selection.Items.Count == 1 && selection.SelectedIndex == -1 && commands[^1].Action == "inspect" && session.ServerConfirmed,
                "restoring quick catalogue should inspect the same confirmed service");
            window.Close();
        });
        Checks.Add("Class catalogue settings preserve small legacy lists, explain truncation, distinguish duplicate names, clear selection and password when paging both ways, reject stale results, and restore the quick list");

        session.RefreshAsync().GetAwaiter().GetResult();
        var actionsType = typeof(OnboardingWindow).Assembly.GetType("NPEduTools.App.OnboardingActions")!;
        Action nothing = () => { };
        var actions = Activator.CreateInstance(actionsType, Enumerable.Repeat<object>(nothing, 9).ToArray())!;
        var constructor = typeof(OnboardingWindow).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
        var wizard = (OnboardingWindow)constructor.Invoke(["NPEduTools.Test.class-catalogue-oobe." + Guid.NewGuid().ToString("N"),
            new OnboardingState(Features: OnboardingFeatures.School, Step: "school"), (Action<OnboardingState>)(_ => { }),
            session, actions, (Action<bool>)(_ => { }), null]);
        wizard.Width = 720; wizard.Height = 600;
        Exercise(wizard, window =>
        {
            var view = (NpepConnectionControl)window.FindName("SchoolConnection");
            var lookup = (Expander)view.FindName("ClassLookupPanel");
            if (!session.ServerConfirmed)
            {
                var confirm = (CheckBox)Find(view, "NpepServerConfirmed");
                Assert(confirm.IsVisible && confirm.IsEnabled, "wizard lost the inspected service and cannot verify its identity");
                confirm.IsChecked = true;
            }
            PumpUntil(() => lookup.IsVisible && lookup.IsEnabled, "verified wizard cannot reach exact lookup");
            lookup.IsExpanded = true;
            var find = (Button)view.FindName("FindClassButton");
            window.UpdateLayout();
            PumpUntil(() => find.IsVisible, "opening exact lookup in the wizard did not reveal its action");
            ((TextBox)view.FindName("SchoolLookupQuery")).Text = "学校全称";
            ((TextBox)view.FindName("ClassLookupQuery")).Text = "G2-C03";
            Raise(find);
            window.UpdateLayout();
            var selection = (ComboBox)view.FindName("RequestedClass");
            Assert(selection.Items.Count == 2 && selection.SelectedIndex == -1 && commands[^1].SchoolQuery == "学校全称" && commands[^1].ClassQuery == "G2-C03",
                "minimum-size OOBE lost exact lookup or manual class selection");
            selection.SelectedIndex = 0;
            selection.BringIntoView(); window.UpdateLayout();
            Snapshot(window, "school-class-lookup-oobe.png");
            window.Close();
        });
        Checks.Add("Minimum-size real OOBE uses the shared class lookup with full school name and class code and leaves its matching screens unselected");

        HostResponse Reply(HostRequest request, string outcome = "Succeeded", string message = "学校目录回执") =>
            new(Protocol.Version, request.RequestId, outcome, outcome == "Rejected" ? "FIXTURE" : null, message, Npep: state);

        static JsonObject Choice(string id) => new()
        {
            ["screenBindingId"] = id, ["schoolName"] = "学校全称", ["schoolCode"] = "SCHOOL-01", ["gradeName"] = "高二",
            ["className"] = "高二（3）班", ["classCode"] = "G2-C03", ["screenName"] = "教室大屏", ["preauthorized"] = true
        };
        static JsonObject Quick(bool truncated, bool lookup) => new()
        { ["truncated"] = truncated, ["supportsLookup"] = lookup, ["items"] = new JsonArray(Choice("room-quick")) };
        static JsonObject Page(NpepCommand command, bool failure) => new()
        {
            ["supportsLookup"] = true, ["schoolQuery"] = command.SchoolQuery, ["classQuery"] = command.ClassQuery,
            ["after"] = command.CatalogueAfter, ["nextCursor"] = command.CatalogueAfter is null && !failure ? "room-202" : null,
            ["items"] = failure ? new JsonArray() : command.CatalogueAfter is null ? new JsonArray(Choice("room-201"), Choice("room-202")) : new JsonArray(Choice("room-203"))
        };
        static void Raise(Button button)
        {
            Assert(button.IsEnabled && button.IsVisible, $"action {button.Name} is unavailable (visible={button.IsVisible}, enabled={button.IsEnabled})");
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        }
    }
}
