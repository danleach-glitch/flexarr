using System.Globalization;
using System.Text.RegularExpressions;

namespace Flexarr.Core.Parsing;

/// <summary>
/// How loud a track is, as its tags say. Gains are in dB and bring the track (or its whole album, so tracks keep their
/// relative levels) to the ReplayGain reference loudness; peaks are the loudest sample as a fraction of full scale, so a
/// player can tell when applying the gain would clip. Each is null when the file doesn't carry that tag.
/// </summary>
public sealed record Loudness(double? TrackGain, double? AlbumGain, double? TrackPeak, double? AlbumPeak)
{
    public static readonly Loudness Unknown = new(null, null, null, null);
}

/// <summary>
/// Reads ReplayGain from a file's tags: the usual <c>REPLAYGAIN_TRACK_GAIN</c> family (Vorbis comments, ID3 TXXX frames
/// and MP4 freeform atoms all come out of ffprobe under that name) and, for Opus, the <c>R128_TRACK_GAIN</c> pair, which
/// is the same idea in fixed-point units relative to a quieter reference.
/// </summary>
public static partial class ReplayGain
{
    /// <summary>EBU R128's reference is -23 LUFS; ReplayGain 2.0's is -18, so an R128 gain is 5 dB short of a ReplayGain one.</summary>
    private const double R128ToReplayGainDb = 5.0;

    public static Loudness FromTags(IReadOnlyDictionary<string, string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var trackGain = Gain(Find(tags, "replaygain_track_gain")) ?? R128(Find(tags, "r128_track_gain"));
        var albumGain = Gain(Find(tags, "replaygain_album_gain")) ?? R128(Find(tags, "r128_album_gain"));
        return new Loudness(trackGain, albumGain, Peak(Find(tags, "replaygain_track_peak")), Peak(Find(tags, "replaygain_album_peak")));
    }

    /// <summary>"-7.52 dB" (or "+1.2", "-7.52 LU"): the number, in dB.</summary>
    private static double? Gain(string? value) =>
        value is not null && GainPattern().Match(value) is { Success: true } m
        && double.TryParse(m.Groups["n"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var db) && double.IsFinite(db)
            ? db
            : null;

    /// <summary>Opus keeps gains as Q7.8 integers: 256 per dB, relative to the R128 reference.</summary>
    private static double? R128(string? value) =>
        value is not null && int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var q) ? q / 256.0 + R128ToReplayGainDb : null;

    /// <summary>"0.988525": a fraction of full scale. Zero or negative means the tagger didn't really measure it.</summary>
    private static double? Peak(string? value) =>
        value is not null && double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var peak) && double.IsFinite(peak) && peak > 0 ? peak : null;

    /// <summary>
    /// The tag by its name, whatever its case, or under an MP4 freeform name ("----:com.apple.iTunes:replaygain_track_gain")
    /// when the tagger wrote it that way.
    /// </summary>
    private static string? Find(IReadOnlyDictionary<string, string> tags, string name)
    {
        if (tags.TryGetValue(name, out var exact))
        {
            return exact;
        }

        return tags.FirstOrDefault(t => t.Key.EndsWith(":" + name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    [GeneratedRegex(@"^\s*(?<n>[+-]?\d+(?:\.\d+)?)\s*(dB|LU)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex GainPattern();
}
