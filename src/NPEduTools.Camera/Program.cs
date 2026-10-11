using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace NPEduTools.Camera;
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // A driver hang cannot keep an orphan holding the camera indefinitely.
        new Thread(() => { Thread.Sleep(6000); Environment.Exit(124); }) { IsBackground = true }.Start();
        try
        {
            byte[] pixels; int width, height;
            if (args.SequenceEqual(["--synthetic"]))
            {
                width = 640; height = 360; pixels = new byte[width * height * 4];
                for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = (byte)(i / 4 % 256); pixels[i + 1] = 80; pixels[i + 2] = 160; }
            }
            else
            {
                if (args.Length != 0) return 2;
                using var input = Console.OpenStandardInput(); using var buffer = new MemoryStream();
                int b; while ((b = input.ReadByte()) != -1) { if (buffer.Length >= 8192) return 2; buffer.WriteByte((byte)b); }
                using var json = JsonDocument.Parse(buffer.ToArray());
                if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.EnumerateObject().Count() != 1
                    || !json.RootElement.TryGetProperty("device", out var d) || d.ValueKind != JsonValueKind.String
                    || d.GetString() is not { Length: > 0 and <= 2048 } device || device.Any(char.IsControl)) return 2;
                (pixels, width, height) = MediaFoundation.Capture(device);
            }
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
            double scale = Math.Min(1, 1280d / Math.Max(width, height));
            byte[]? bytes = null; BitmapSource? image = null;
            for (int attempt = 0; attempt < 3 && bytes is null; attempt++, scale *= 0.75)
            {
                image = scale == 1 ? source : new TransformedBitmap(source, new ScaleTransform(scale, scale));
                foreach (int quality in new[] {80, 60, 40})
                {
                    var encoder = new JpegBitmapEncoder { QualityLevel = quality }; encoder.Frames.Add(BitmapFrame.Create(image));
                    using var encoded = new MemoryStream(); encoder.Save(encoded);
                    if (encoded.Length is > 0 and <= 262144) { bytes = encoded.ToArray(); break; }
                }
            }
            if (bytes is null || image is null) return 3;
            var header = new byte[12]; BinaryPrimitives.WriteInt32LittleEndian(header, image.PixelWidth);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), image.PixelHeight); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), bytes.Length);
            using var output = Console.OpenStandardOutput(); output.Write(header); output.Write(bytes); output.Flush(); return 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { Console.Error.WriteLine("PHOTO_CAMERA_FAILED"); return 4; }
    }
}
