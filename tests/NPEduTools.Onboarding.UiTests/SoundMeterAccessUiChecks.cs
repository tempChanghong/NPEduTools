using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Host;

internal static partial class Program
{
    private static void RunSoundMeterAccessChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            foreach (string change in new[] { "host", "credential", "source" })
            foreach (bool duringRead in new[] { false, true })
                SoundMeterAccessChanged(change, duringRead);
            SoundMeterAccessChanged("host", false, list: true);
            SoundMeterAccessChanged("host", true, list: true);
            SoundMeterAccessChanged("host", true, list: true, firstList: true);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void SoundMeterAccessChanged(string change, bool duringRead, bool list = false, bool firstList = false)
    {
        const string secret = "fixture-meter-access";
        var original = new NoiseProtectionState(false, true, Guid.NewGuid(), null,
            ManagementSource: "School", CredentialRevision: 7);
        var changed = change switch
        {
            "host" => original with { InstanceId = Guid.NewGuid() },
            "credential" => original with { CredentialRevision = 8 },
            _ => original with { ManagementSource = "Local", CredentialRevision = 0 }
        };
        var access = original;
        int armed = 0;
        var backend = new MeterUiBackend();
        var meter = new SoundMeterService(() => backend,
            value => value == secret ? null : "MANAGEMENT_SECRET_INVALID");
        string pipe = "NPEduTools.Test.meter-access." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var server = Task.Run(() => WorkspaceProxy(pipe, async request =>
        {
            if (request.Capability == "noise.management.status")
            {
                if (!duringRead && Interlocked.Exchange(ref armed, 0) == 1)
                    access = changed;
                return new(1, request.RequestId, "Succeeded", null, "synthetic management status",
                    NoiseProtection: access);
            }
            if (request.Capability != "noise.meter.command")
                return new(1, request.RequestId, "Rejected", "UnknownCapability", "synthetic fixture");
            var response = await meter.HandleAsync(request, stop.Token);
            if (duringRead && request.SoundMeter?.Action == (list ? "list" : "read") && Interlocked.Exchange(ref armed, 0) == 1)
                access = changed;
            return response with { NoiseProtection = access };
        }, stop.Token));
        var window = new SoundMeterWindow(pipe);
        try
        {
            var password = (PasswordBox)window.FindName("ManagementSecret");
            var devices = (ComboBox)window.FindName("Devices");
            var credentials = (FrameworkElement)window.FindName("CredentialsPanel");
            var reading = (TextBlock)window.FindName("Reading");
            var message = (TextBlock)window.FindName("Message");
            void Execute(string action)
            {
                var task = (Task)typeof(SoundMeterWindow).GetMethod("ExecuteAsync", WorkspaceFlags)!
                    .Invoke(window, [action])!;
                PumpUntil(() => task.IsCompleted, "meter access fixture request timed out");
                task.GetAwaiter().GetResult();
            }
            password.Password = secret;
            if (!firstList)
            {
                Execute("list");
                Assert(devices.Items.Count == 1 && credentials.Visibility == Visibility.Collapsed,
                    "fixture did not verify and list the synthetic meter");
                devices.SelectedIndex = 0;
                Execute("read");
                Assert(reading.Text.Contains("60.5") && backend.Opens == 1 && backend.Disposals == 1,
                    "unchanged access failed to display or release the synthetic reading");
            }
            int initialOpens = backend.Opens, initialEnumerations = backend.Enumerations;
            Volatile.Write(ref armed, 1);
            Execute(list ? "list" : "read");
            int expectedOpens = initialOpens + (duringRead && !list ? 1 : 0);
            Assert(backend.Opens == expectedOpens,
                "preflight access reset still opened the formerly selected meter");
            Assert(!list || backend.Enumerations == initialEnumerations + (duringRead ? 1 : 0),
                "preflight access reset still enumerated the meter");
            Assert(reading.Text == "—" && message.Text.Contains("重新验证"),
                "changed management access displayed a reading or lost re-verification feedback");
            Assert(devices.SelectedItem is null && password.Password.Length == 0 &&
                credentials.Visibility == Visibility.Visible && !Button(window, "ReadMeter").IsEnabled &&
                !Button(window, "OpenReference").IsEnabled && Button(window, "ListDevices").IsEnabled,
                "changed access left stale selection, credentials or actions enabled");
            password.Password = secret;
            Execute("list");
            devices.SelectedIndex = 0;
            Execute("read");
            Assert(reading.Text.Contains("60.5") && credentials.Visibility == Visibility.Collapsed &&
                backend.Opens == backend.Disposals && backend.Opens == expectedOpens + 1,
                "explicit re-verification did not restore reading or leaked a simulated handle");
            Checks.Add("Meter " + (firstList ? "initial list" : list ? "refresh" : "read") + "/" + change +
                " changes " + (duringRead ? "in flight" : "during preflight") +
                ": reset clears stale selection/value, preflight opens no device, explicit re-verification recovers; synthetic HID only");
        }
        finally
        {
            window.Close(); stop.Cancel();
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
    }
}
