using System.Globalization;

namespace KarSlides.Core.Configuration;

/// <summary>Frame rate as an exact fraction (29.97 = 30000/1001).</summary>
public readonly record struct FrameRate(int Numerator, int Denominator)
{
    public double PeriodMs => 1000.0 * Denominator / Numerator;

    /// <summary>NDI timecode (100 ns units) of frame <paramref name="index"/>.</summary>
    public long Timecode(long index) => index * 10_000_000L * Denominator / Numerator;

    /// <summary>
    /// Audio samples belonging to video frame <paramref name="index"/>: floor((k+1)*SR*D/N) - floor(k*SR*D/N),
    /// exact for 29.97/59.94 (1601/1602 samples alternate at 48 kHz) with no drift.
    /// </summary>
    public int SamplesInFrame(long index, int sampleRate)
    {
        var rd = (long)sampleRate * Denominator;
        return (int)((index + 1) * rd / Numerator - index * rd / Numerator);
    }

    public static FrameRate Parse(string value) => value.Trim() switch
    {
        "25" => new(25, 1),
        "29.97" => new(30000, 1001),
        "30" => new(30, 1),
        "50" => new(50, 1),
        "59.94" => new(60000, 1001),
        "60" => new(60, 1),
        _ => throw new FormatException($"Unsupported frame rate '{value}' (25, 29.97, 30, 50, 59.94, 60)"),
    };

    public override string ToString() => Denominator == 1
        ? Numerator.ToString(CultureInfo.InvariantCulture)
        : (Numerator / (double)Denominator).ToString("0.##", CultureInfo.InvariantCulture);
}
