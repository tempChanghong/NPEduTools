using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Screen = System.Windows.Forms.Screen;

namespace NPEduTools.EdgeRestrictionPrototype;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [STAThread]
    private static int Main(string[] arguments)
    {
        if (arguments is not ["--self-test", _] and not ["--watch-foreground", _] and not ["--probe-foreground", _, _, _])
        {
            Console.Error.WriteLine("Use --self-test <evidence-dir>, --watch-foreground <exact-url>, or --probe-foreground <pid> <exact-url> <evidence-dir>.");
            return 2;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (arguments is ["--self-test", var output]) await VerifyAsync(output);
                else if (arguments is ["--watch-foreground", var url])
                    await WatchForegroundAsync(ParseTarget(url));
                else if (arguments is ["--probe-foreground", var process, var probeUrl, var path] &&
                    int.TryParse(process, out int pid) && pid > 0)
                    await WatchForegroundAsync(ParseTarget(probeUrl), pid, path);
                else throw new InvalidDataException("The test Edge process ID is invalid.");
                app.Shutdown(0);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                app.Shutdown(1);
            }
        });
        return app.Run();
    }

    private static async Task VerifyAsync(string output)
    {
        Directory.CreateDirectory(output);
        var target = new Uri("https://douyin.com/__npeptest_synthetic");
        var gate = new ForegroundPolicyGate(target);
        Check(EdgeForegroundUia.Matches("douyin.com", new Uri("https://douyin.com/")) &&
            EdgeForegroundUia.Matches("douyin.com/__npeptest_synthetic", target) &&
            !EdgeForegroundUia.Matches("douyin.com", target) &&
            !EdgeForegroundUia.Matches("douyin.com.example.org/__npeptest_synthetic", target),
            "Normalize the HTTPS address and root slash without broadening the target.");
        var proof = new BrowserObservation("test-tab", "blocked-1", target.AbsoluteUri, "chrome-error://chromewebdata/",
            "net::ERR_BLOCKED_BY_ADMINISTRATOR", MainDocument: true, ActiveTab: true);
        var backdrop = new Window
        {
            Title = "NPEduTools 设备验收", Width = 400, Height = 230, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new TextBlock { Text = "隔离警告窗口验收", Margin = new(25) }
        };
        backdrop.Show();
        backdrop.Activate();
        await Task.Delay(300);
        var browserWindow = new WindowInteropHelper(backdrop).Handle;
        Check(GetForegroundWindow() == browserWindow, "The disposable test window did not receive foreground.");
        Check(!gate.TryOpen(proof with { CommittedUrl = "https://127.0.0.1/", BrowserFailure = "" },
            browserWindow, GetForegroundWindow()), "Typing a URL is insufficient.");
        Check(!gate.TryOpen(proof with { MainDocument = false }, browserWindow, GetForegroundWindow()), "Ignore background resources.");
        Check(!gate.TryOpen(proof with { ActiveTab = false }, browserWindow, GetForegroundWindow()), "Ignore a background tab.");
        Check(!gate.TryOpen(proof, browserWindow, 0), "Ignore an unfocused browser.");
        Check(!gate.TryOpen(proof with { BrowserFailure = "net::ERR_BLOCKED_BY_CLIENT" },
            browserWindow, GetForegroundWindow()), "Reject non-admin blocks.");
        Check(!gate.TryOpen(proof with { CommittedUrl = target.AbsoluteUri }, browserWindow, GetForegroundWindow()), "Reject a page-supplied marker.");
        Check(gate.TryOpen(proof, browserWindow, GetForegroundWindow()), "Accept confirmed foreground top-level evidence.");

        byte[] alarm = RestrictionWarning.CreateAlarm();
        File.WriteAllBytes(Path.Combine(output, "two-tone-warning.wav"), alarm);
        using var input = new BinaryReader(new MemoryStream(alarm, writable: false));
        input.BaseStream.Position = 24;
        int sampleRate = input.ReadInt32();
        input.BaseStream.Position = 40;
        double alarmSeconds = input.ReadInt32() / 2.0 / sampleRate;
        Check(alarmSeconds is >= 2.8 and <= 3.2, "Alarm duration is about three seconds.");
        Check(RestrictionWarning.Translations.Split('\n').Length == 10, "Keep the ten language rows.");

        var warning = new RestrictionWarning(browserWindow, target.IdnHost, "当前生效：高一年级网站访问规则", playAudio: true);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        warning.Closed += (_, _) => closed.TrySetResult();
        warning.Show();
        await warning.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var monitor = Screen.FromHandle(browserWindow);
        var handle = new WindowInteropHelper(warning).Handle;
        Check(GetWindowRect(handle, out var bounds) && (bounds.Left, bounds.Top, bounds.Right, bounds.Bottom) ==
            (monitor.Bounds.Left, monitor.Bounds.Top, monitor.Bounds.Right, monitor.Bounds.Bottom),
            $"Cover the triggering window's monitor. Window: {bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}. Screen: {monitor.Bounds}.");
        Check(!HasButton(warning), "Do not expose a hand-operated exit button.");
        Check(GetForegroundWindow() == browserWindow, "The dedicated test window still owns foreground.");
        CaptureWarningWindow(handle, bounds, Path.Combine(output, "01-warning-current-monitor.png"));
        foreach (var (width, height, dpi) in new[] { (1280, 720, 96.0), (1280, 720, 144.0), (900, 1200, 96.0) })
        {
            var surface = RestrictionWarning.CreateView(target.IdnHost, "当前生效：高一年级网站访问规则",
                new TextBlock { Text = "将在 30 秒后返回课堂", Foreground = Brushes.White, FontSize = 30 });
            surface.Measure(new Size(width, height));
            surface.Arrange(new Rect(0, 0, width, height));
            Capture(surface, Path.Combine(output, $"layout-{width}x{height}-{dpi}.png"), dpi);
        }
        warning.Close();
        Check(warning.IsVisible, "A manual close cannot dismiss the warning.");
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(34));
        Check(warning.ElapsedSeconds is >= 29.9 and < 33, "The warning auto-closes after about thirty seconds.");
        Check(!gate.TryOpen(proof, browserWindow, GetForegroundWindow()), "The same blocked tab stays quiet after the warning.");
        Check(gate.TryOpen(proof with { LoaderId = "blocked-2" }, browserWindow, GetForegroundWindow()), "A new top-level blocked navigation is distinct.");
        File.WriteAllText(Path.Combine(output, "self-test.json"), JsonSerializer.Serialize(new
        {
            passed = true, proofSource = "synthetic-browser-observations", languageRows = 10,
            actualMonitor = monitor.DeviceName, monitorWidth = monitor.Bounds.Width, monitorHeight = monitor.Bounds.Height,
            availableMonitors = Screen.AllScreens.Length, dpi = VisualTreeHelper.GetDpi(backdrop).PixelsPerInchX,
            alarmSeconds, audioStartRequested = warning.AudioStarted, autoCloseSeconds = warning.ElapsedSeconds,
            ignored = new[] { "typing", "subresource", "background-tab", "background-window", "non-policy-error", "page-text", "same-tab-repeat" }
        }, Json));
        backdrop.Close();
        Console.WriteLine("PASS Windows warning, monitor, 10 languages, timer and fixture gate.");
    }

    private static Uri ParseTarget(string text)
    {
        if (Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" &&
            string.IsNullOrEmpty(url.UserInfo))
            return url;
        throw new InvalidDataException("An exact HTTP(S) URL without credentials is required.");
    }

    private static async Task WatchForegroundAsync(Uri exactUrl, int? testPid = null, string? output = null)
    {
        using var stopping = new CancellationTokenSource();
        if (testPid is not null) stopping.CancelAfter(TimeSpan.FromSeconds(8));
        ConsoleCancelEventHandler stop = (_, args) => { args.Cancel = true; stopping.Cancel(); };
        Console.CancelKeyPress += stop;
        var gate = new ForegroundPolicyGate(exactUrl);
        var outcomes = new Dictionary<string, int>();
        var msaaOutcomes = new Dictionary<string, int>();
        RestrictionWarning? warning = null;
        int warningsShown = 0;
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                if (warning is { IsVisible: true })
                {
                    await Task.Delay(450, stopping.Token);
                    continue;
                }
                var (observation, msaa) = await Task.Run(() =>
                {
                    var observed = EdgeForegroundUia.Read(exactUrl, testPid);
                    return (observed, testPid is not null ? EdgeForegroundUia.ReadMsaaProbe(observed.Window, exactUrl) : null);
                }).WaitAsync(stopping.Token);
                outcomes[observation.Result] = outcomes.GetValueOrDefault(observation.Result) + 1;
                if (msaa is not null) msaaOutcomes[msaa] = msaaOutcomes.GetValueOrDefault(msaa) + 1;
                if (observation.Proof is not null && gate.TryOpen(observation.Proof, observation.Window, GetForegroundWindow()))
                {
                    warning = new RestrictionWarning(observation.Window, exactUrl.IdnHost, "当前生效：教室网站访问规则", playAudio: true);
                    warning.Show();
                    warningsShown++;
                    if (output is not null)
                    {
                        Directory.CreateDirectory(output);
                        await warning.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        Capture(warning, Path.Combine(output, "edge-policy-warning.png"));
                    }
                }
                await Task.Delay(450, stopping.Token);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        finally { Console.CancelKeyPress -= stop; }

        if (warning is { IsVisible: true })
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            warning.Closed += (_, _) => closed.TrySetResult();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(33));
        }
        if (output is not null)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "foreground-uia.json"), JsonSerializer.Serialize(new
            {
                proofSource = "foreground-window-uia", testPid, outcomes, msaaOutcomes, warningsShown
            }, Json));
        }
    }

    private static bool HasButton(DependencyObject root)
    {
        if (root is Button) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (HasButton(VisualTreeHelper.GetChild(root, i))) return true;
        return false;
    }

    private static void Capture(FrameworkElement surface, string path, double? dpi = null)
    {
        double resolution = dpi ?? VisualTreeHelper.GetDpi(surface).PixelsPerInchX;
        double scale = resolution / 96;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth * scale),
            (int)Math.Ceiling(surface.ActualHeight * scale), resolution, resolution, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void CaptureWarningWindow(nint hwnd, WindowRect bounds, string path)
    {
        int width = bounds.Right - bounds.Left;
        int height = bounds.Bottom - bounds.Top;
        using var bitmap = new System.Drawing.Bitmap(width, height);
        using (var drawing = System.Drawing.Graphics.FromImage(bitmap))
        {
            var deviceContext = drawing.GetHdc();
            try
            {
                Check(PrintWindow(hwnd, deviceContext, 2), "Read pixels from the warning's own HWND.");
            }
            finally { drawing.ReleaseHdc(deviceContext); }
        }
        var red = bitmap.GetPixel(width / 2, height / 2);
        Check(red.R > 110 && red.G < 55 && red.B < 65, "The captured warning includes its red surface.");
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out WindowRect bounds);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint window, nint deviceContext, uint flags);
}
