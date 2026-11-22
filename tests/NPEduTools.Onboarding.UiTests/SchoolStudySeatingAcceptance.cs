using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

internal static partial class Program
{
    private static void RunSchoolStudySeatingAcceptance(string path)
    {
        var data = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var origin = new Uri(data["origin"]!.GetValue<string>());
        if (origin.Scheme != "http" || origin.Host != "127.0.0.1" || origin.UserInfo.Length != 0 || data["isolated"]?.GetValue<bool>() != true)
            throw new ArgumentException("School study acceptance requires an isolated loopback fixture.");
        using var http = new HttpClient { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(6) };
        using var finish = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var identity = data["deployment"]!.AsObject();
        string bearer = data["bearer"]!.GetValue<string>();
        string classId = data["classId"]!.GetValue<string>();
        var date = DateOnly.Parse(data["date"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
        var now = new DateTimeOffset(date.ToDateTime(new TimeOnly(19, 20)), TimeSpan.Zero);
        var clock = new StudyUiClock();
        var connection = Guid.NewGuid();
        var bridge = Guid.NewGuid();
        long sample = 0, reports = 0;
        var host = new StudySettingsService(() => new(connection, bridge, Interlocked.Increment(ref sample), 0,
            now.AddMilliseconds(clock.Ms), 0, "Advancing", "test school clock"), () => false, clock, background: false);
        var display = new StudyDisplayService(host.DisplaySnapshot, clock, directory: _output);
        host.PresentationObservation = display.ObservePresentation;
        string pipe = "NPEduTools.Test.school-study." + Guid.NewGuid().ToString("N");
        var served = ServeAsync();
        var window = new StudyWindow(pipe, () => {}, muted: true, time: clock) { Width = 1060, Height = 670, Topmost = false };
        var surface = (StudySurface)window.Content;
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        var overlay = (FrameworkElement)surface.FindName("SeatingOverlay");
        var open = (Button)surface.FindName("SeatingButton");
        var close = (Button)surface.FindName("CloseSeating");
        try
        {
            var session = Await(DeviceAsync("sessions", "0.1", new JsonObject
            {
                ["serverInstanceId"] = identity["serverInstanceId"]!.DeepClone(),
                ["deploymentEpoch"] = identity["deploymentEpoch"]!.DeepClone(),
                ["runId"] = Guid.NewGuid().ToString(), ["expectedStatusEpoch"] = 0
            }));
            var config = Await(DeviceAsync("study-settings", "0.10", null));
            Assert(config["scope"]!["classId"]!.GetValue<string>() == classId, "Native policy must use the self-study classroom.");
            Assert(config["effective"]!["settings"]!["homework"]!["sourceClassIds"]!.AsArray()
                .Any(item => item!.GetValue<string>() != classId), "Fixture must use another classroom's homework.");
            var context = new JsonObject
            {
                ["sessionId"] = session["sessionId"]!.DeepClone(), ["runId"] = session["runId"]!.DeepClone(),
                ["statusEpoch"] = session["statusEpoch"]!.DeepClone(), ["identity"] = config["identity"]!.DeepClone()
            };
            host.BindStudy(config["identity"]!.ToJsonString());
            for (int i = 0; i < 4; i++) { clock.Ms += 500; host.ApplyStudy(config); }
            Assert(host.Snapshot().Phase == "Active" && host.Snapshot().ClockReady, "Real Host must select Saturday's confirmed Thursday window.");
            window.Show();

            void Apply()
            {
                window.Apply(display.Snapshot(), null);
                window.UpdateLayout();
            }
            void Tick()
            {
                clock.Ms += 350;
                host.ApplyStudy(config);
                Apply();
                Wait(window.ReportPresentationAsync());
            }
            JsonObject Exchange()
            {
                var status = host.ObserveStudy() ?? throw new InvalidDataException("No configuration observation from Host.");
                Await(DeviceAsync("study-settings/receipt", "0.10", new JsonObject
                {
                    ["context"] = context.DeepClone(), ["sequence"] = ++reports, ["status"] = status
                }));
                return Await(DeviceAsync("study-settings/seating", "0.14", new JsonObject
                {
                    ["context"] = context.DeepClone(), ["version"] = config["version"]!.DeepClone(),
                    ["downloaded"] = host.ObserveSeatingDownload(), ["observation"] = host.ObserveSeating()
                }, sequenced: true));
            }
            void Load(JsonObject manifest)
            {
                Assert(manifest["classId"]!.GetValue<string>() == classId, "Seating manifest cannot be scoped to the homework source.");
                if (manifest["asset"] is not JsonObject asset) host.ApplySeating(manifest, null);
                else
                {
                    using var bytes = new MemoryStream();
                    for (int index = 0; index < asset["chunks"]!.GetValue<int>(); index++)
                    {
                        var part = Await(DeviceAsync("study-settings/seating/chunk", "0.14", new JsonObject
                        {
                            ["context"] = context.DeepClone(), ["version"] = config["version"]!.DeepClone(),
                            ["revision"] = manifest["revision"]!.DeepClone(), ["hash"] = asset["hash"]!.DeepClone(), ["index"] = index
                        }));
                        Assert(part["index"]!.GetValue<int>() == index, "Unexpected seating chunk index.");
                        bytes.Write(Convert.FromBase64String(part["data"]!.GetValue<string>()));
                    }
                    host.ApplySeating(manifest, bytes.ToArray());
                }
                Apply();
            }
            JsonObject WaitFor(int revision)
            {
                JsonObject? manifest = null;
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                do
                {
                    Tick();
                    manifest = Exchange();
                    if (manifest["revision"]!.GetValue<int>() == revision) return manifest;
                    Wait(Task.Delay(850, finish.Token));
                } while (System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(55));
                throw new TimeoutException($"School did not publish expected revision {revision}: {manifest}");
            }
            void SendVisible(int revision)
            {
                Tick();
                Assert(host.ObserveSeating() is { } observed && observed["visible"]!.GetValue<bool>() &&
                    observed["revision"]!.GetValue<int>() == revision, $"Host did not receive WPF's live v{revision} pulse.");
                Exchange();
            }

            if (data["homeworkAcceptance"]?.GetValue<bool>() == true)
            {
                VerifyHomework();
                return;
            }

            Load(WaitFor(1));
            PumpUntil(() => open.IsEnabled && surface.SeatingRevision == 1, "WPF did not decode v1 through the Host pipe.");
            Click(open);
            PumpUntil(() => overlay.IsVisible, "WPF did not open v1.");
            SendVisible(1);
            Mark("old-open");
            Snapshot(window, "front-old-open.png");

            Load(WaitFor(2));
            Assert(host.ObserveSeatingDownload()?["revision"]?.GetValue<int>() == 2 && surface.SeatingRevision == 1 && overlay.IsVisible,
                "The current reader must still see the old image after the Host cached the new image.");
            SendVisible(1);
            Mark("old-while-new-downloaded");
            Snapshot(window, "front-old-while-new-downloaded.png");
            long waiting = System.Diagnostics.Stopwatch.GetTimestamp();
            while (!Allowed("allow-close"))
            {
                Assert(System.Diagnostics.Stopwatch.GetElapsedTime(waiting) < TimeSpan.FromSeconds(30), "Console did not observe the separate old/new evidence.");
                SendVisible(1);
                Wait(Task.Delay(950, finish.Token));
            }

            Click(close);
            PumpUntil(() => !overlay.IsVisible && surface.SeatingRevision == 2, "Closing did not promote the pending image.");
            Click(open);
            PumpUntil(() => overlay.IsVisible && surface.SeatingRevision == 2, "Reopening did not render the current version.");
            SendVisible(2);
            Mark("new-open");
            Snapshot(window, "front-reopened-new.png");

            Load(WaitFor(3));
            Assert(!overlay.IsVisible && surface.SeatingHash is null && host.ObserveSeatingDownload() is null,
                "Withdrawal must clear the actual display and Host's cached image.");
            Mark("withdrawn");
            Load(WaitFor(4));
            PumpUntil(() => surface.SeatingRevision == 4 && !overlay.IsVisible, "Reposted history did not stay closed until requested.");
            Click(open);
            PumpUntil(() => overlay.IsVisible, "Reposted history did not open.");
            SendVisible(4);
            Mark("restored");
            Snapshot(window, "front-restored-history.png");
            Checks.Add("Real isolated KV policy/assets → native Host scheduling and cache → private named pipe → WPF render → Host observation and KV seating evidence; old image remains until closed, withdrawal clears, history reopens.");

            void VerifyHomework()
            {
                var manifest = WaitFor(1);
                Assert(manifest["asset"]?["hash"]?.GetValue<string>() == data["seatingHash"]!.GetValue<string>(), "The seventh classroom must keep its own seating plan.");
                Load(manifest);
                var initial = ReadHomework();
                Assert(initial.Items is [{ Sources.Count: 2 } duplicate] &&
                    duplicate.Sources.Select(row => row.Name).ToHashSet().SetEquals(new[] {"高二（8）班", "物理 A1"}),
                    "The isolated device received one publication with the two authorized labels.");
                var reader = (FrameworkElement)surface.FindName("HomeworkReader");
                var openReader = (Button)surface.FindName("OpenHomeworkReader");
                var pager = (StudyHomeworkPager)typeof(StudySurface).GetField("_pager", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
                PumpUntil(() => openReader.IsEnabled && pager.PageCount > 1, "Real WPF did not unpack and paginate the long KV assignment.");
                Assert(pager.IntervalSeconds == 30, "The actual display did not apply 30 seconds per page.");
                Snapshot(window, "front-deduplicated-homework.png");
                Mark("homework-open");
                Click(openReader);
                PumpUntil(() => reader.IsVisible, "The real reading view did not open.");
                int currentPage = pager.Page;
                for (int second = 0; second < 40; second++)
                {
                    clock.Ms += 1000;
                    host.ApplyStudy(config);
                    Apply();
                }
                Assert(reader.IsVisible && pager.Page == currentPage, "The reading view must pause the overview rotation.");
                Mark("append-homework");
                Exchange();
                var updated = ReadHomework();
                Assert(updated.Items.Count == 2 && updated.Items.Any(item => item.Title == "同来源新作业"), "Same-source publications should refresh the snapshot.");
                Assert(reader.IsVisible && pager.Page == currentPage, "Refreshing a same-source snapshot must not interrupt reading.");
                Snapshot(window, "front-reading-with-pending-refresh.png");
                InputKey((Button)surface.FindName("ReturnFromReader"), System.Windows.Input.Key.Escape);
                PumpUntil(() => !reader.IsVisible, "Escape did not return from reading.");
                long presentationSequence = 0;
                bool marked = false;
                while (!Allowed("display-checked"))
                {
                    Tick();
                    var observed = host.ObserveStudyPresentation();
                    if (observed?["status"]?["state"]?.GetValue<string>() == "Visible")
                    {
                        Exchange();
                        Await(DeviceAsync("study-settings/presentation", "0.13", new JsonObject
                        {
                            ["context"] = context.DeepClone(), ["sequence"] = ++presentationSequence,
                            ["version"] = observed["version"]!.DeepClone(), ["status"] = observed["status"]!.DeepClone(),
                        }));
                        if (!marked) { Mark("presentation"); marked = true; }
                    }
                    Wait(Task.Delay(350, finish.Token));
                }
                Assert(marked, "No real WPF window observation reached KV.");
                Snapshot(window, "front-verified-study-window.png");
                Checks.Add("Actual Host and WPF read deduplicated KV homework with both labels, paginate at 30 seconds, keep reading during refresh, own-class seating, and emit presentation evidence.");
            }

            StudyHomeworkSnapshot ReadHomework()
            {
                Exchange();
                var request = host.StudyHomeworkRequest() ?? throw new InvalidDataException("Study homework is not eligible.");
                string? snapshotId = null;
                using var memory = new MemoryStream();
                int count = 1;
                for (int index = 0; index < count; index++)
                {
                    var part = Await(DeviceAsync("study-settings/homework", "0.12", new JsonObject
                    {
                        ["context"] = context.DeepClone(), ["version"] = request["version"]!.DeepClone(), ["date"] = request["date"]!.DeepClone(),
                        ["snapshotId"] = snapshotId, ["index"] = index,
                    }));
                    snapshotId ??= part["snapshotId"]!.GetValue<string>();
                    count = part["count"]!.GetValue<int>();
                    memory.Write(Convert.FromBase64String(part["data"]!.GetValue<string>()));
                }
                var bytes = memory.ToArray();
                host.ApplyStudyHomework(JsonNode.Parse(bytes)!.AsObject(), bytes, snapshotId!);
                Apply();
                return StudyHomeworkContract.Parse(bytes);
            }
        }
        finally
        {
            window.Shutdown();
            finish.Cancel();
            try { served.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        static void InputKey(FrameworkElement control, System.Windows.Input.Key key)
        {
            var source = PresentationSource.FromVisual(control) ?? throw new InvalidOperationException("Test window is not connected.");
            control.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source,
                Environment.TickCount, key) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
        }
        T Await<T>(Task<T> task) { PumpUntil(() => task.IsCompleted, "Native school study request did not finish.", 60); return task.GetAwaiter().GetResult(); }
        void Wait(Task task) { PumpUntil(() => task.IsCompleted, "Native school study action did not finish.", 60); task.GetAwaiter().GetResult(); }
        void Mark(string name)
        {
            using var response = Await(http.PostAsync("/__study/native/" + name, null, finish.Token));
            response.EnsureSuccessStatusCode();
        }
        bool Allowed(string name)
        {
            using var response = Await(http.GetAsync("/__study/native/" + name, finish.Token));
            return response.IsSuccessStatusCode;
        }
        async Task<JsonObject> DeviceAsync(string route, string version, JsonObject? body, bool sequenced = false)
        {
            string requestId = Guid.NewGuid().ToString();
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, "/api/v2/npep/device/" + route);
            request.Headers.Add("Authorization", "Bearer " + bearer);
            request.Headers.Add("X-NPEP-Version", version);
            request.Headers.Add("X-Request-Id", requestId);
            if (sequenced) request.Headers.Add("X-NPEP-Seating-Sequence", "1");
            if (body is not null) { body["requestId"] = requestId; request.Content = JsonContent.Create(body); }
            using var response = await http.SendAsync(request, finish.Token).ConfigureAwait(false);
            var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(finish.Token).ConfigureAwait(false))!;
            if (!response.IsSuccessStatusCode || result["requestId"]?.GetValue<string>() != requestId)
                throw new InvalidDataException($"Isolated device {route}: {response.StatusCode} {result["error"]?["code"]}");
            return result["data"]!.AsObject();
        }
        async Task ServeAsync()
        {
            while (!finish.IsCancellationRequested)
            {
                using var stream = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
                await stream.WaitForConnectionAsync(finish.Token).ConfigureAwait(false);
                var request = await Protocol.ReadAsync<HostRequest>(stream, finish.Token).ConfigureAwait(false);
                if (Protocol.Validate(request) is not null) throw new InvalidDataException("Invalid private Host request.");
                var result = request.Capability switch
                {
                    "study.seating" => host.ReadSeating(request),
                    "study.seating.presentation" => host.ReceiveSeating(request, display.Snapshot()),
                    "study.homework" => host.ReadHomework(request),
                    _ => display.Handle(request)
                };
                await Protocol.WriteAsync(stream, result, finish.Token).ConfigureAwait(false);
            }
        }
    }
}
