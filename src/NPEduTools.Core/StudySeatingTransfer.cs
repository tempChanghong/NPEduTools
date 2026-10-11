using System.Security.Cryptography;
using System.Text;
namespace NPEduTools.Core;

public static class StudySeatingTransfer
{
    public static string DirectoryFor(string pipe) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NPEduTools", "seating-transfer", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(pipe))));
    public static string PathFor(string pipe, Guid upload) => Path.Combine(DirectoryFor(pipe), upload.ToString("N") + ".png");
    public static void Write(string pipe, Guid upload, byte[] png)
    {
        if (upload == Guid.Empty || png.Length is <= 0 or > 2097152) throw new InvalidDataException();
        var directory = DirectoryFor(pipe); Directory.CreateDirectory(directory); CheckPlain(directory);
        // Own files only, non-recursive, bounded cleanup after an abandoned App.
        foreach (var file in Directory.EnumerateFiles(directory, "*.png").Take(64))
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out _) && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1))
                try { CheckPlain(file); File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        using var stream = new FileStream(PathFor(pipe, upload), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(png); stream.Flush(true);
    }
    public static byte[] Read(string pipe, Guid upload, int count, string hash)
    {
        if (upload == Guid.Empty) throw new InvalidDataException();
        string path = PathFor(pipe, upload); CheckPlain(DirectoryFor(pipe)); CheckPlain(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (count is <= 0 or > 2097152 || stream.Length != count) throw new InvalidDataException();
        byte[] png = new byte[count]; stream.ReadExactly(png);
        if (Convert.ToHexStringLower(SHA256.HashData(png)) != hash) throw new InvalidDataException();
        return png;
    }
    public static void Delete(string pipe, Guid upload)
    {
        if (upload == Guid.Empty) return;
        try { string path = PathFor(pipe, upload); if (File.Exists(path)) { CheckPlain(DirectoryFor(pipe)); CheckPlain(path); File.Delete(path); } }
        catch (IOException) { } catch (UnauthorizedAccessException) { } catch (InvalidDataException) { }
    }
    private static void CheckPlain(string path)
    { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse points are not transfer files."); }
}
