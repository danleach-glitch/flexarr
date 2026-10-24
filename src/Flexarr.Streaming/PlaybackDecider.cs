using Flexarr.Core.Media;

namespace Flexarr.Streaming;

public enum PlaybackMethod
{
    /// <summary>The client plays the original file.</summary>
    DirectPlay,

    /// <summary>The server converts the file to HLS on the fly.</summary>
    Transcode,
}

/// <summary>A subtitle the player can show: an embedded stream or a sidecar file next to the video.</summary>
public sealed record SubtitleTrack(string Key, int? StreamIndex, string? ExternalPath, string Codec, string? Language, string? Title, bool IsDefault, bool IsForced)
{
    private static readonly HashSet<string> TextCodecs = new(StringComparer.OrdinalIgnoreCase) { "subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text" };

    /// <summary>Text subtitles are converted to WebVTT; image subtitles (PGS, VobSub) must be burned into the video.</summary>
    public bool IsText => TextCodecs.Contains(Codec);
}

public sealed record PlaybackOptions(int? AudioStreamIndex = null, string? SubtitleKey = null, long? MaxBitrate = null, int? MaxHeight = null, bool ForceTranscode = false);

public sealed record TranscodePlan(
    int VideoStreamIndex,
    int? AudioStreamIndex,
    int TargetWidth,
    int TargetHeight,
    long VideoBitrate,
    bool ToneMap,
    bool CopyAudio,
    int AudioChannels,
    SubtitleTrack? BurnSubtitle,
    string? SourceAudioCodec = null)
{
    /// <summary>RFC 6381 codec string for the audio in the HLS output, for the master playlist.</summary>
    public string? AudioCodecTag => AudioStreamIndex is null ? null : !CopyAudio ? "mp4a.40.2" : SourceAudioCodec switch
    {
        "ac3" => "ac-3",
        "eac3" => "ec-3",
        "mp3" => "mp4a.40.34",
        _ => "mp4a.40.2",
    };
}

public sealed record PlaybackDecision(
    PlaybackMethod Method,
    IReadOnlyList<string> Reasons,
    MediaStream? Audio,
    SubtitleTrack? Subtitle,
    TranscodePlan? Transcode);

/// <summary>Chooses between direct play and transcoding, and how to transcode.</summary>
public static class PlaybackDecider
{
    private static readonly HashSet<string> HlsAudioCodecs = new(StringComparer.OrdinalIgnoreCase) { "aac", "mp3", "ac3", "eac3" };

    public static PlaybackDecision Decide(MediaFile file, DeviceProfile profile, IReadOnlyList<SubtitleTrack> subtitles, PlaybackOptions options)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(subtitles);
        ArgumentNullException.ThrowIfNull(options);

        var audioStreams = file.Streams.Where(s => s.Type == StreamType.Audio).OrderBy(s => s.Index).ToList();
        var audio = audioStreams.FirstOrDefault(s => s.Index == options.AudioStreamIndex)
            ?? audioStreams.FirstOrDefault(s => s.IsDefault)
            ?? audioStreams.FirstOrDefault();
        var subtitle = options.SubtitleKey is null ? null : subtitles.FirstOrDefault(s => s.Key == options.SubtitleKey);
        var video = file.Streams.Where(s => s.Type == StreamType.Video).OrderBy(s => s.Index).FirstOrDefault();

        var maxHeight = Math.Min(profile.MaxHeight, options.MaxHeight ?? int.MaxValue);
        var maxBitrate = Min(profile.MaxBitrate, options.MaxBitrate ?? 0);

        var reasons = new List<string>();
        if (options.ForceTranscode)
        {
            reasons.Add("Conversion was requested");
        }

        if (!profile.Containers.Contains(ContainerName(file.Container), StringComparer.OrdinalIgnoreCase))
        {
            reasons.Add($"This device can't open {file.Container?.ToUpperInvariant() ?? "this"} files");
        }

        if (file.VideoCodec is { } codec && !profile.VideoCodecs.Contains(codec, StringComparer.OrdinalIgnoreCase))
        {
            reasons.Add($"This device can't decode {codec.ToUpperInvariant()} video");
        }

        if (file.VideoRange != VideoRange.Sdr && !profile.Hdr)
        {
            reasons.Add("This display doesn't support HDR");
        }

        if (Math.Max(file.Height ?? 0, (file.Width ?? 0) * 9 / 16) > maxHeight)
        {
            reasons.Add($"Resolution is above {maxHeight}p");
        }

        if (maxBitrate > 0 && file.Bitrate > maxBitrate)
        {
            reasons.Add("Bitrate is above the quality limit");
        }

        if (audio is not null && !profile.AudioCodecs.Contains(audio.Codec, StringComparer.OrdinalIgnoreCase))
        {
            reasons.Add($"This device can't decode {audio.Codec.ToUpperInvariant()} audio");
        }

        // Browsers can't switch audio tracks in a plain <video>; any track but the first needs conversion.
        if (audio is not null && audioStreams.Count > 1 && audio.Index != audioStreams[0].Index)
        {
            reasons.Add("The chosen audio track isn't the file's first");
        }

        if (subtitle is { IsText: false })
        {
            reasons.Add("Picture-based subtitles have to be burned in");
        }

        if (reasons.Count == 0 || video is null)
        {
            return new PlaybackDecision(PlaybackMethod.DirectPlay, reasons, audio, subtitle, null);
        }

        // Size by the 16:9 equivalent so a 1920×800 scope film counts as 1080p, not 800p.
        var sourceWidth = file.Width ?? 1920;
        var sourceHeight = file.Height ?? 1080;
        var equivalent = Math.Max(sourceHeight, sourceWidth * 9 / 16);
        var scale = Math.Min(1.0, (double)maxHeight / equivalent);
        var targetHeight = Even(sourceHeight * scale);
        var targetWidth = Even(sourceWidth * scale);
        var bitrate = BitrateFor(Math.Max(targetHeight, targetWidth * 9 / 16));
        if (maxBitrate > 0)
        {
            bitrate = Math.Min(bitrate, maxBitrate - 256_000);
        }

        if (file.Bitrate is > 0 and var source)
        {
            // Re-encoding above the source's bitrate wastes bandwidth without improving quality.
            bitrate = Math.Min(bitrate, Math.Max((long)(source * 1.1), 1_500_000));
        }

        var copyAudio = audio is not null
            && HlsAudioCodecs.Contains(audio.Codec)
            && profile.AudioCodecs.Contains(audio.Codec, StringComparer.OrdinalIgnoreCase)
            && (audio.Channels ?? 2) <= profile.MaxAudioChannels;

        var plan = new TranscodePlan(
            video.Index,
            audio?.Index,
            targetWidth,
            targetHeight,
            Math.Max(bitrate, 500_000),
            ToneMap: file.VideoRange != VideoRange.Sdr,
            copyAudio,
            Math.Clamp(Math.Min(audio?.Channels ?? 2, profile.MaxAudioChannels), 1, 6),
            subtitle is { IsText: false } ? subtitle : null,
            audio?.Codec);

        return new PlaybackDecision(PlaybackMethod.Transcode, reasons, audio, subtitle, plan);
    }

    /// <summary>Target H.264 bitrates by output height.</summary>
    public static long BitrateFor(int height) => height switch
    {
        >= 2000 => 30_000_000,
        >= 1400 => 18_000_000,
        >= 1000 => 10_000_000,
        >= 700 => 5_000_000,
        >= 500 => 2_500_000,
        _ => 1_500_000,
    };

    private static string ContainerName(string? container) => container switch
    {
        "mov" or "m4v" => "mp4",
        null => "unknown",
        _ => container,
    };

    private static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);

    private static long Min(long a, long b) => a <= 0 ? b : b <= 0 ? a : Math.Min(a, b);
}
