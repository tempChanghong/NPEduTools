using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NPEduTools.App;
public sealed record StudySeatingDraft(byte[] Png, int Width, int Height, string Hash, BitmapSource Preview);
public static class StudySeatingImage
{
    public static Task<StudySeatingDraft> LoadAsync(string path)
    {
        var completion = new TaskCompletionSource<StudySeatingDraft>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            try {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length is <= 0 or > 8388608) throw new InvalidDataException("原图应不超过 8 MiB。");
                byte[] bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
                completion.SetResult(Normalize(bytes));
            } catch (Exception e) { completion.SetException(e); }
        }) { IsBackground = true, Name = "NPEduTools.SeatingImage" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    // Decode and render once on the worker. A small frozen preview is all the dialog retains as pixels.
    public static StudySeatingDraft Normalize(byte[] source)
    {
        if (source.Length is < 8 or > 8388608 ||
            !(source.AsSpan(0,8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}) || source[0] == 255 && source[1] == 216 && source[2] == 255))
            throw new InvalidDataException("请选择 PNG 或 JPEG 图片。");
        using var input = new MemoryStream(source, false);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0];
        int w = frame.PixelWidth, h = frame.PixelHeight;
        if (w is <= 0 or > 4096 || h is <= 0 or > 4096 || (long)w*h > 6000000) throw new InvalidDataException("原图最大边为 4096 像素，总像素不超过 600 万。");
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, new Rect(0,0,w,h)); dc.DrawImage(frame,new Rect(0,0,w,h)); }
        var rendered = new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32); rendered.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(rendered));
        using var output = new MemoryStream(); encoder.Save(output);
        if (output.Length > 2097152) throw new InvalidDataException("处理后的图片超过 2 MiB，请缩小原图后重试。");
        byte[] png = output.ToArray();
        using var previewStream = new MemoryStream(png,false);
        var preview = new BitmapImage(); preview.BeginInit(); preview.CacheOption = BitmapCacheOption.OnLoad;
        preview.DecodePixelWidth = Math.Min(w,960); preview.StreamSource=previewStream; preview.EndInit(); preview.Freeze();
        return new(png,w,h,Convert.ToHexStringLower(SHA256.HashData(png)),preview);
    }
}
