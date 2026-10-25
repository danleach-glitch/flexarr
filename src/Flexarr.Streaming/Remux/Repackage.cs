using System.Globalization;

namespace Flexarr.Streaming.Remux;

/// <summary>
/// How a video is copied rather than converted: where its segments start (keyframes, on the timeline from the file's
/// start) and how it's described to the player.
/// </summary>
/// <param name="CodecTag">The MP4 sample entry: "avc1", "hvc1", or "dvh1" for Dolby Vision without a fallback.</param>
/// <param name="Codecs">RFC 6381, for the master playlist's CODECS.</param>
/// <param name="SupplementalCodecs">Dolby Vision over an HDR10 or HLG base: what a Dolby Vision display can use.</param>
/// <param name="VideoRange">"SDR", "PQ" or "HLG".</param>
/// <param name="StripDolbyVision">Profile 7 (Blu-ray's two layers), which Apple can't play: its HDR10 base layer instead.</param>
/// <param name="FrameRate">The video's frame rate, for the master playlist's FRAME-RATE.</param>
public sealed record Repackage(
    IReadOnlyList<double> SegmentStarts,
    string CodecTag,
    string Codecs,
    string? SupplementalCodecs,
    string VideoRange,
    bool StripDolbyVision,
    double? FrameRate = null)
{
    /// <summary>Segments last at least this long (keyframes can be a frame apart), up to the longest gap between keyframes.</summary>
    public const double MinSegmentSeconds = 3;

    /// <summary>Picks segment starts from the keyframes: each at least <see cref="MinSegmentSeconds"/> after the last, and
    /// never just after another keyframe (whose fragment would be hard to tell apart from it).</summary>
    public static List<double> SegmentStartsFrom(IReadOnlyList<double> keyframes, double startTime, double duration)
    {
        ArgumentNullException.ThrowIfNull(keyframes);
        var starts = new List<double> { 0 };
        for (var i = 1; i < keyframes.Count; i++)
        {
            var time = keyframes[i] - startTime;
            var clearOfPrevious = time - (keyframes[i - 1] - startTime) >= 0.6;
            if (time - starts[^1] >= MinSegmentSeconds && clearOfPrevious && duration - time >= 1)
            {
                starts.Add(Math.Round(time, 6));
            }
        }

        return starts;
    }

    /// <summary>
    /// The highest HEVC level a master playlist declares (5.1, which covers 2160p60). Encoders stamp 5.2 on 1080p
    /// streams, and an Apple TV 4K refuses a declared 5.2 outright while decoding the same bytes happily.
    /// </summary>
    private const int MaxDeclaredHevcLevel = 153;

    /// <summary>
    /// The CODECS string read off the file's own hvcC record (ISO 14496-15 Annex E), so the master playlist and the
    /// init segment agree: an Apple TV checks them against each other and refuses the stream when they differ. A
    /// Main 10 record with constraint flags 0x90 gives "hvc1.2.4.L153.90".
    /// </summary>
    public static string? HevcCodecs(string hevcConfigHex)
    {
        ArgumentNullException.ThrowIfNull(hevcConfigHex);
        byte[] record;
        try
        {
            record = Convert.FromHexString(hevcConfigHex);
        }
        catch (FormatException)
        {
            return null;
        }

        if (record.Length < 13 || record[0] != 1)
        {
            return null;
        }

        var profileSpace = (record[1] >> 6) & 0x3;
        var tier = ((record[1] >> 5) & 0x1) == 1 ? "H" : "L";
        var profileIdc = record[1] & 0x1F;
        var compatibility = (uint)(record[2] << 24 | record[3] << 16 | record[4] << 8 | record[5]);
        // The compatibility flags are written in reverse bit order (flag 31 first), as hex without leading zeroes: a
        // Main 10 record stores 0x20000000 and prints "4".
        uint reversed = 0;
        for (var i = 0; i < 32; i++)
        {
            if ((compatibility >> (31 - i) & 1) == 1)
            {
                reversed |= 1u << i;
            }
        }

        var constraints = record[6..12].Reverse().SkipWhile(b => b == 0).Reverse().ToArray();
        var space = profileSpace switch { 1 => "A", 2 => "B", 3 => "C", _ => "" };
        var declaredLevel = Math.Min((int)record[12], MaxDeclaredHevcLevel);
        var codecs = string.Create(CultureInfo.InvariantCulture, $"hvc1.{space}{profileIdc}.{reversed:x}.{tier}{declaredLevel}");
        return constraints.Length == 0 ? codecs : codecs + "." + string.Join('.', constraints.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }

    /// <summary>"avc1.640028", "hvc1.2.4.L153.90", "dvh1.05.06".</summary>
    public static string CodecsFor(VideoFacts video)
    {
        ArgumentNullException.ThrowIfNull(video);
        var level = video.Level ?? 0;
        if (video.DolbyVisionProfile == 5)
        {
            return string.Create(CultureInfo.InvariantCulture, $"dvh1.05.{video.DolbyVisionLevel ?? 6:00}");
        }

        if (video.Codec == "hevc")
        {
            if (video.HevcConfig is { } config && HevcCodecs(config) is { } fromRecord)
            {
                return fromRecord;
            }

            level = Math.Min(level, MaxDeclaredHevcLevel);
            return video.Profile == "Main 10"
                ? string.Create(CultureInfo.InvariantCulture, $"hvc1.2.4.L{level}.B0")
                : string.Create(CultureInfo.InvariantCulture, $"hvc1.1.6.L{level}.B0");
        }

        var profile = video.Profile switch
        {
            "Baseline" or "Constrained Baseline" => "42",
            "Main" => "4D",
            _ => "64",
        };
        return string.Create(CultureInfo.InvariantCulture, $"avc1.{profile}00{level:X2}");
    }
}
