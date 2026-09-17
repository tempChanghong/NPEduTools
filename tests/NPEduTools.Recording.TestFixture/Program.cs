using System.Diagnostics;
using System.Media;

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
string title = args.FirstOrDefault(a => a.StartsWith("title=", StringComparison.Ordinal))?[6..] ?? "NPEduTools recording fixture";
using var sound = new SoundPlayer();
using var wav = new MemoryStream();
if (args.Contains("--tone"))
{
    using var writer = new BinaryWriter(wav, System.Text.Encoding.ASCII, true);
    const int samples = 48000 * 4;
    writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8); writer.Write(16);
    writer.Write((short)1); writer.Write((short)1); writer.Write(48000); writer.Write(96000); writer.Write((short)2); writer.Write((short)16);
    writer.Write("data"u8); writer.Write(samples * 2);
    for (int i = 0; i < samples; i++) writer.Write((short)(Math.Sin(i * Math.PI * 2 * 880 / 48000) * 650));
    wav.Position = 0; sound.Stream = wav; sound.PlayLooping();
}
var clock = Stopwatch.StartNew();
using var form = new Form { Text = title, ClientSize = new Size(960, 540), StartPosition = FormStartPosition.CenterScreen,
    TopMost = true, BackColor = Color.FromArgb(24, 64, 90) };
using var timer = new System.Windows.Forms.Timer { Interval = 100 };
form.Paint += (_, e) =>
{
    using var font = new Font("Segoe UI", 28);
    e.Graphics.DrawString("NPEduTools recording test\n" + clock.Elapsed.ToString(@"mm\:ss\.f"), font, Brushes.White, 60, 60);
    e.Graphics.FillRectangle(Brushes.Coral, 60 + (int)(clock.Elapsed.TotalSeconds * 80 % 700), 340, 80, 80);
};
timer.Tick += (_, _) => form.Invalidate(); timer.Start();
Application.Run(form);
sound.Stop();
