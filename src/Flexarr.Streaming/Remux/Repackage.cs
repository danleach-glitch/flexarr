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
public sealed record Repackage(
    IReadOnlyList<double> SegmentStarts,
    string CodecTag,
    string Codecs,
    string? SupplementalCodecs,
    string VideoRange,
    bool StripDolbyVision)
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

    /// <summary>"avc1.640028", "hvc1.2.4.L153.B0", "dvh1.05.06".</summary>
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
