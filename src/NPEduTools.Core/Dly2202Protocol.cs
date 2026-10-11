using System.Buffers.Binary;

namespace NPEduTools.Core;

public enum SoundMeterWeighting { A, C }
public enum SoundMeterTimeWeighting { Slow, Fast }

/// <summary>A reading reported by the reference meter, not a conversion of microphone dBFS.</summary>
public sealed record SoundMeterReading(double Decibels, SoundMeterWeighting Weighting,
    SoundMeterTimeWeighting TimeWeighting, bool MaximumHold, bool LowBattery, int Range)
{
    public string Unit => Weighting == SoundMeterWeighting.A ? "dB(A)" : "dB(C)";
}

/// <summary>
/// Minimal read protocol observed in the user's SoundLab2 installation. Hardware compatibility
/// remains unverified. No configuration, calibration, or history-write commands are exposed.
/// </summary>
public static class Dly2202Protocol
{
    public const ushort VendorId = 0x64BD;
    public const ushort ProductId = 0x74E3;
    public const byte ReportId = 0;
    public const byte ReadCommand = 0xB3;

    public static bool SupportsReportLength(int length) => length is 9 or 65;

    public static byte[] CreateReadReport(int outputLength)
    {
        if (!SupportsReportLength(outputLength))
            throw new ArgumentOutOfRangeException(nameof(outputLength), "Unverified HID report length.");
        var report = new byte[outputLength];
        // The main program polls with 9-byte zero padding; its protocol DLL uses 65-byte '#' padding.
        if (outputLength == 65) report.AsSpan(2).Fill(0x23);
        report[0] = ReportId;
        report[1] = ReadCommand;
        return report;
    }

    public static bool TryDecode(ReadOnlySpan<byte> report, out SoundMeterReading? reading,
        out string error)
    {
        reading = null;
        error = "";
        if (!SupportsReportLength(report.Length) || report[0] != ReportId)
        { error = "UnsupportedReport"; return false; }
        int range = report[3] & 0x0F;
        if (range > 4) { error = "InvalidRange"; return false; }
        double level = BinaryPrimitives.ReadInt16BigEndian(report.Slice(1, 2)) / 10.0;
        bool cWeighted = (report[3] & 0x10) != 0;
        (double minimum, double maximum) = range switch
        {
            0 => (30, 130), 1 => (30, 80), 2 => (50, 100),
            3 => (60, 110), 4 => (80, 130), _ => throw new InvalidOperationException()
        };
        if (cWeighted) minimum = Math.Max(35, minimum);
        // OVER / UNDER representation is not established by the vendor parser. Never accept an
        // out-of-range number as a calibration reference or invent a saturated reading.
        if (level < minimum || level > maximum)
        { error = "OutOfRange"; return false; }
        reading = new(level, cWeighted ? SoundMeterWeighting.C : SoundMeterWeighting.A,
            (report[3] & 0x40) != 0 ? SoundMeterTimeWeighting.Fast : SoundMeterTimeWeighting.Slow,
            (report[3] & 0x20) != 0, (report[3] & 0x80) != 0, range);
        return true;
    }
}
