using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static readonly List<string> Checks = [];
    private static string _output = "";
    [STAThread]
    private static int Main(string[] args)
    {
        bool examOnly = args.Length == 2 && args[1] == "--remote-exam";
        bool connectionOnly = args.Length == 2 && args[1] == "--school-connection";
        bool noiseOnly = args.Length == 2 && args[1] == "--noise-status";
        bool recordingOnly = args.Length == 2 && args[1] == "--automatic-recording";
        bool manualOnly = args.Length == 2 && args[1] == "--manual-recording";
        bool secRandomOnly = args.Length == 2 && args[1] == "--secrandom-status";
        bool examAwareOnly = args.Length == 2 && args[1] == "--examaware-status";
        bool classroomOnly = args.Length == 2 && args[1] == "--classroom-status";
        bool scheduledDisplayOnly = args.Length == 2 && args[1] == "--scheduled-display";
        bool preparationOnly = args.Length == 2 && args[1] == "--onboarding-preparation";
        bool touchOnly = args.Length == 2 && args[1] == "--touch-status";
        if (args.Length != 1 && !examOnly && !connectionOnly && !noiseOnly && !recordingOnly && !manualOnly && !secRandomOnly && !examAwareOnly && !classroomOnly && !scheduledDisplayOnly && !preparationOnly && !touchOnly) throw new ArgumentException("Usage: UiTests <output-directory> [--remote-exam|--school-connection|--noise-status|--automatic-recording|--manual-recording|--secrandom-status|--examaware-status|--classroom-status|--scheduled-display|--onboarding-preparation|--touch-status]");
        _output = Path.GetFullPath(args[0]); Directory.CreateDirectory(_output);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Load only the real styling, never App.OnStartup or any production Host/endpoint.
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml")).Root!
            .Element(presentation + "Application.Resources")!.Element(presentation + "ResourceDictionary")!;
        resources.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        foreach (var source in resources.Descendants().Attributes("Source"))
            source.Value = "/NPEduTools.App;component/" + source.Value;
        application.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resources.ToString());
        string pipe = "NPEduTools.Test.legal-ui." + Guid.NewGuid().ToString("N");
        string path = AgreementAcceptanceStore.PathFor(pipe);
        try
        {
            if (examOnly)
            {
                RunRemoteExamChecks();
                WriteResult("PASSED", null); return 0;
            }
            if (connectionOnly)
            {
                RunSchoolConnectionChecks();
                WriteResult("PASSED", null); return 0;
            }
            if (noiseOnly)
            {
                RunNoiseStatusChecks();
                WriteResult("PASSED", null); return 0;
            }
            if (recordingOnly)
            {
                RunAutomaticRecordingChecks();
                WriteResult("PASSED", null); return 0;
            }
            if (manualOnly)
            {
                RunManualRecordingChecks();
                WriteResult("PASSED", null); return 0;
            }
            if (secRandomOnly)
            {
                RunSecRandomStatusChecks();
                WriteResult("PASSED", null); return 0;
            }
            if (examAwareOnly)
            {
                RunExamAwareStatusChecks();
                WriteResult("PASSED", null); return 0;
            }
            if (classroomOnly)
            {
                RunClassroomStatusChecks();
                WriteResult("PASSED", null); return 0;
            }
            if (scheduledDisplayOnly)
            {
                RunScheduledDisplayChecks();
                WriteResult("PASSED", null); return 0;
            }
            if (preparationOnly)
            {
                RunOnboardingPreparationChecks();
                WriteResult("PASSED", null); return 0;
            }
            if (touchOnly)
            {
                RunTouchStatusChecks();
                WriteResult("PASSED", null); return 0;
            }
            Exercise(new AgreementsWindow(pipe), window =>
            {
                Assert(!Check(window, "AcceptApp").IsChecked.GetValueOrDefault(), "first consent preselected");
                Assert(!Button(window, "ContinueButton").IsEnabled, "empty consent allowed");
                Click(window, "ReadNpepPrivacy", true);
                var viewer = (FlowDocumentScrollViewer)window.FindName("DocumentViewer");
                Assert(new TextRange(viewer.Document.ContentStart, viewer.Document.ContentEnd).Text.Contains("原音频"), "privacy missing");
                Assert(viewer.Document.Blocks.OfType<Table>().Any(), "privacy tables missing");
                Click(window, "ReadGplLicense", true);
                Assert(new TextRange(viewer.Document.ContentStart, viewer.Document.ContentEnd).Text.Contains("GNU GENERAL PUBLIC LICENSE"), "GPL missing");
                Check(window, "AcceptApp").IsChecked = true; Check(window, "AcceptService").IsChecked = true;
                Assert(!Button(window, "ContinueButton").IsEnabled, "two consents allowed");
                Check(window, "AcceptPrivacy").IsChecked = true;
                Assert(Button(window, "ContinueButton").IsEnabled, "three confirmations blocked");
                Check(window, "AcceptPrivacy").IsChecked = false;
                Assert(!Button(window, "ContinueButton").IsEnabled, "unchecking did not block");
                Snapshot(window, "agreements.png"); window.Close();
            });
            Assert(!File.Exists(path), "closing saved implicit consent"); Checks.Add("Fresh/partial/unchecked/closed consent blocks; three separate checkboxes; offline privacy tables and GPL");
            Exercise(new AgreementsWindow(pipe) { Width = 760, Height = 620 }, window =>
            {
                Click(window, "ReadNpepPrivacy", true); Snapshot(window, "agreements-minimum.png");
                Assert(Button(window, "ContinueButton").ActualHeight > 0, "minimum layout loses confirmation"); window.Close();
            });

            Exercise(new AgreementsWindow(pipe), window =>
            {
                Check(window, "AcceptApp").IsChecked = Check(window, "AcceptService").IsChecked = Check(window, "AcceptPrivacy").IsChecked = true;
                Click(window, "ContinueButton");
            });
            Assert(new AgreementAcceptanceStore(path).Read()!.IsCurrent(AgreementCatalog.Load()), "consent not persisted");
            Exercise(new AgreementsWindow(pipe, false), window =>
            {
                Assert(Check(window, "AcceptApp").IsChecked == true && !Check(window, "AcceptApp").IsEnabled, "saved evidence not read-only");
                Assert(Button(window, "ContinueButton").Visibility == Visibility.Collapsed, "read-only window rewrites evidence");
                window.Close();
            });
            Checks.Add("Explicit confirmation persists and read-only viewer displays saved consent without rewriting");

            File.WriteAllText(path, "{\"SchemaVersion\":1,\"Agreements\":[]}");
            Exercise(new AgreementsWindow(pipe), window =>
            {
                using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                Check(window, "AcceptApp").IsChecked = Check(window, "AcceptService").IsChecked = Check(window, "AcceptPrivacy").IsChecked = true;
                Click(window, "ContinueButton");
                Assert(((TextBlock)window.FindName("ErrorText")).Text.Contains("未保存"), "write failure was accepted");
                Assert(window.DialogResult != true, "write failure closed successfully"); window.Close();
            });
            Checks.Add("Write failure leaves the required dialog open with no acceptance");

            int opened = 0;
            var state = new OnboardingState(Features: OnboardingFeatures.SecRandom);
            var actionsType = typeof(OnboardingWindow).Assembly.GetType("NPEduTools.App.OnboardingActions")!;
            Action nothing = () => { };
            var actions = Activator.CreateInstance(actionsType, Enumerable.Repeat<object>(nothing, 8).Append((Action)(() => opened++)).ToArray())!;
            var session = new NpepConnectionSession((_, _) => Task.FromException<HostResponse>(new IOException("isolated fixture")));
            var constructor = typeof(OnboardingWindow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var wizard = (OnboardingWindow)constructor.Invoke([pipe, state, (Action<OnboardingState>)(_ => { }), session, actions, (Action<bool>)(_ => { }), null]);
            Exercise(wizard, window =>
            {
                Check(window, "UseSecRandom").IsChecked = true;
                Assert(((TextBlock)window.FindName("ProgressText")).Text.Contains("/ 4"), "SecRandom conditional step missing");
                Snapshot(window, "purposes.png");
                window.Close();
            });
            var pointWizard = (OnboardingWindow)constructor.Invoke([pipe, state with { Step = "secrandom" }, (Action<OnboardingState>)(_ => { }), session, actions, (Action<bool>)(_ => { }), null]);
            Exercise(pointWizard, window =>
            {
                Button(window, "OobeSecRandomSettings", true).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert(opened == 1, "SecRandom configuration action not connected");
                Snapshot(window, "secrandom.png"); window.Close();
            });
            Checks.Add("SecRandom conditional purpose and existing configuration action; no actual draw, Host or hardware");
            WriteResult("PASSED", null); return 0;
        }
        catch (Exception error) { WriteResult("FAILED", error.ToString()); return 1; }
        finally { if (File.Exists(path)) File.Delete(path); application.Shutdown(); }
    }
    private static void Exercise(Window window, Action<Window> test)
    {
        Exception? failure = null;
        window.Loaded += (_, _) =>
        {
            try { test(window); }
            catch (Exception error) { failure = error; window.Close(); }
        };
        window.ShowDialog();
        if (failure is not null) throw failure;
    }
    private static CheckBox Check(Window window, string name) => (CheckBox)window.FindName(name);
    private static Button Button(Window window, string name, bool automation = false) => (Button)(automation ? Find(window, name) : window.FindName(name));
    private static void Click(Window window, string name, bool automation = false) => Button(window, name, automation).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    private static DependencyObject Find(DependencyObject root, string id)
    {
        if (System.Windows.Automation.AutomationProperties.GetAutomationId(root) == id) return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { try { return Find(VisualTreeHelper.GetChild(root, i), id); } catch (InvalidOperationException) { } }
        throw new InvalidOperationException("Control missing: " + id);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void PumpUntil(Func<bool> condition, string failure)
    {
        var elapsed = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (condition() || elapsed.Elapsed > TimeSpan.FromSeconds(5)) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert(condition(), failure);
    }
    private static void Snapshot(Window window, string file)
    {
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(_output, file)); encoder.Save(stream);
    }
    private static void WriteResult(string status, string? error) => File.WriteAllText(Path.Combine(_output, "result.json"), JsonSerializer.Serialize(new
    { status, checks = Checks, error, environment = "isolated-wpf-fixture", realDevices = false, production = false }, new JsonSerializerOptions { WriteIndented = true }));
}
