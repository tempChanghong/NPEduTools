using System.Diagnostics;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NPEduTools.App;
using Screen = System.Windows.Forms.Screen;

namespace NPEduTools.EdgeRestrictionPrototype;

internal sealed class RestrictionWarning : Window
{
    internal const string Translations = "ACCESS BLOCKED\nアクセスがブロックされました\nACCÈS BLOQUÉ\nZUGRIFF BLOCKIERT\n访问已被阻止\nACCESO BLOQUEADO\nACCESSO BLOCCATO\n접근이 차단되었습니다\nДОСТУП ЗАБЛОКИРОВАН\nACESSO BLOQUEADO";
    internal const int Seconds = 30;
    private readonly TextBlock _countdown = WhiteText(30);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private SoundPlayer? _player;
    private long _shownAt;
    private bool _done;

    internal RestrictionWarning(nint tabWindow, string host, string policy, bool playAudio)
    {
        var screen = Screen.FromHandle(tabWindow).Bounds;
        double scale = Math.Max(96, GetDpiForWindow(tabWindow)) / 96.0;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = screen.Left / scale;
        Top = screen.Top / scale;
        Width = screen.Width / scale;
        Height = screen.Height / scale;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Background = ClassroomWarningSurface.WindowBackground;
        Title = "NPEduTools · 网站访问已被阻止";
        Content = CreateView(host, policy, _countdown);
        bool placed = false;
        SourceInitialized += (_, _) =>
            placed = SetWindowPos(new WindowInteropHelper(this).Handle, -1,
                screen.Left, screen.Top, screen.Width, screen.Height, 0x0010);
        _timer.Tick += (_, _) => UpdateCountdown();
        Closing += (_, args) => args.Cancel = !_done;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _player?.Dispose();
        };
        Loaded += (_, _) =>
        {
            if (!placed)
            {
                Finish();
                return;
            }
            _shownAt = Stopwatch.GetTimestamp();
            UpdateCountdown();
            _timer.Start();
            if (playAudio) PlayWarning();
        };
    }

    internal bool AudioStarted { get; private set; }
    internal double ElapsedSeconds => _shownAt == 0 ? 0 : Stopwatch.GetElapsedTime(_shownAt).TotalSeconds;

    internal static FrameworkElement CreateView(string host, string policy, TextBlock countdown)
    {
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 40, 62, 40) };
        right.Children.Add(WhiteText(29, "本教室大屏已限制访问此网站"));
        var site = WhiteText(57, host);
        site.FontWeight = FontWeights.SemiBold;
        site.Margin = new(0, 30, 0, 20);
        right.Children.Add(site);
        right.Children.Add(WhiteText(29, policy));
        countdown.Margin = new(0, 52, 0, 0);
        right.Children.Add(countdown);
        return ClassroomWarningSurface.Create(Translations, right, emphasizedLine: 4);
    }

    private static TextBlock WhiteText(double size, string? value = null) => new()
    {
        Text = value ?? "", Foreground = Brushes.White, FontSize = size, TextWrapping = TextWrapping.Wrap
    };

    private void UpdateCountdown()
    {
        int remaining = (int)Math.Ceiling(Seconds - ElapsedSeconds);
        if (remaining <= 0) Finish();
        else _countdown.Text = $"将在 {remaining} 秒后返回课堂";
    }

    private void PlayWarning()
    {
        try
        {
            _player = new SoundPlayer(new MemoryStream(CreateAlarm(), writable: false));
            _player.Load();
            _player.Play();
            AudioStarted = true;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
        {
            // Playback failure does not change the visual warning or its timeout.
            _player?.Dispose();
            _player = null;
        }
    }

    internal static byte[] CreateAlarm()
    {
        const int rate = 22050, pulse = 13230, silence = 2205, betweenPairs = 6615;
        int sampleCount = 4 * pulse + 2 * silence + betweenPairs;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + sampleCount * 2); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(sampleCount * 2);
        for (int i = 0; i < 4; i++)
        {
            int hz = i % 2 == 0 ? 950 : 1310;
            for (int n = 0; n < pulse; n++)
            {
                double envelope = Math.Min(1, Math.Min(n, pulse - 1 - n) / 256.0);
                writer.Write((short)(19000 * envelope * Math.Sin(2 * Math.PI * hz * n / rate)));
            }
            int pause = i == 1 ? betweenPairs : i == 3 ? 0 : silence;
            for (int n = 0; n < pause; n++) writer.Write((short)0);
        }
        return stream.ToArray();
    }

    private void Finish()
    {
        if (_done) return;
        _done = true;
        Close();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint handle, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
