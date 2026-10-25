using System.Globalization;
using Flexarr.Core.Media;
using Flexarr.Streaming.Remux;

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
    string? SourceAudioCodec = null,
    bool Fmp4 = false,
    string? SourceVideoCodec = null,
    bool LosslessAudio = false,
    bool HighResolutionAudio = false)
{
    /// <summary>The video is copied as it is (repackaged), cut at <see cref="Repackage.SegmentStarts"/>; null when it's converted.</summary>
    public Repackage? Repackage { get; init; }

    /// <summary>RFC 6381 codec string for the audio in the HLS output, for the master playlist.</summary>
    public string? AudioCodecTag => AudioStreamIndex is null ? null : LosslessAudio ? "fLaC" : !CopyAudio ? "mp4a.40.2" : SourceAudioCodec switch
    {
        "ac3" => "ac-3",
        "eac3" => "ec-3",
        "mp3" => "mp4a.40.34",
        "flac" => "fLaC",
        "alac" => "alac",
        _ => "mp4a.40.2",
    };

    /// <summary>About how many bits per second the audio takes, for the master playlist's BANDWIDTH.</summary>
    public long AudioBitrate => LosslessAudio ? AudioChannels * (HighResolutionAudio ? 600_000L : 400_000L) : AudioChannels > 2 ? 640_000 : 192_000;
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

    /// <summary>What fragmented MP4 carries as it is: MPEG-TS's codecs and lossless FLAC and ALAC.</summary>
    private static readonly HashSet<string> Fmp4AudioCodecs = new(StringComparer.OrdinalIgnoreCase) { "aac", "mp3", "ac3", "eac3", "flac", "alac" };

    /// <summary>Below this cap (a remote connection, say), lossless audio is too big and converts to AAC instead.</summary>
    private const long LosslessMinBitrate = 20_000_000;

    /// <summary>TrueHD, DTS-HD Master Audio, PCM and the lossless codecs: worth keeping lossless.</summary>
    private static bool IsLossless(MediaStream audio) =>
        audio.Codec is "truehd" or "mlp" or "flac" or "alac" || audio.Codec.StartsWith("pcm_", StringComparison.Ordinal)
        || (audio.Codec == "dts" && audio.Profile?.Contains("MA", StringComparison.Ordinal) == true);

    /// <summary>
    /// The conversion <paramref name="plan"/> with the video copied as it is instead, when this device can play the
    /// file's video and only its container (or sound) stood in the way: the original picture, HDR and Dolby Vision
    /// included, for a fraction of the work. Null when the video has to be converted after all.
    /// </summary>
    public static TranscodePlan? Repackaged(TranscodePlan plan, MediaFile file, DeviceProfile profile, PlaybackOptions options, RemuxInfo info)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(info);

        var video = info.Video;
        var maxHeight = Math.Min(profile.MaxHeight, options.MaxHeight ?? int.MaxValue);
        var maxBitrate = Min(profile.MaxBitrate, options.MaxBitrate ?? 0);
        var playable = video.Codec switch
        {
            "h264" => video.Profile is "Baseline" or "Constrained Baseline" or "Main" or "High",
            "hevc" => video.Profile is "Main" or "Main 10",
            _ => false,
        };
        var dolbyVision = video.DolbyVisionProfile;
        var range = dolbyVision == 5 || video.ColorTransfer == "smpte2084" ? "PQ" : video.ColorTransfer == "arib-std-b67" ? "HLG" : "SDR";
        // What's copied as it is: SDR; Dolby Vision profile 5 on a Dolby Vision display; HDR10, and Dolby Vision 7 and
        // 8 (which ride on an HDR10 base layer), on a display showing HDR10; HLG on one showing HLG. The profile's Hdr10
        // and Hlg say what the display shows; a player that doesn't say (older apps) gets HDR converted.
        var presentable = range switch
        {
            "SDR" => true,
            _ when dolbyVision == 5 => profile.Hdr && profile.DolbyVision,
            "PQ" => profile.Hdr && profile.Hdr10 == true,
            "HLG" => profile.Hdr && profile.Hlg == true,
            _ => false,
        };
        if (!profile.Fmp4 || options.ForceTranscode || plan.BurnSubtitle is not null || !playable || video.Interlaced
            || !profile.VideoCodecs.Contains(video.Codec, StringComparer.OrdinalIgnoreCase)
            || Math.Max(video.Height, video.Width * 9 / 16) > maxHeight
            || (maxBitrate > 0 && (file.Bitrate ?? long.MaxValue) > maxBitrate)
            || !presentable
            || dolbyVision is not (null or 5 or 7 or 8)
            || info.Keyframes.Count < 2)
        {
            return null;
        }

        var duration = file.DurationSeconds ?? info.Keyframes[^1];
        var supplemental = dolbyVision == 8 && profile.DolbyVision
            ? video.DolbyVisionCompatibility switch
            {
                1 => string.Create(CultureInfo.InvariantCulture, $"dvh1.08.{video.DolbyVisionLevel ?? 6:00}/db1p"),
                4 => string.Create(CultureInfo.InvariantCulture, $"dvh1.08.{video.DolbyVisionLevel ?? 6:00}/db4h"),
                _ => null,
            }
            : null;
        var repackage = new Repackage(
            Repackage.SegmentStartsFrom(info.Keyframes, video.StartTime, duration),
            CodecTag: dolbyVision == 5 ? "dvh1" : video.Codec == "hevc" ? "hvc1" : "avc1",
            Repackage.CodecsFor(video),
            supplemental,
            range,
            StripDolbyVision: dolbyVision == 7,
            FrameRate: video.FrameRate);
        return plan with
        {
            TargetWidth = video.Width,
            TargetHeight = video.Height,
            VideoBitrate = file.Bitrate ?? plan.VideoBitrate,
            ToneMap = false,
            Fmp4 = true,
            Repackage = repackage,
        };
    }

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
        if (audio is not null && audioStreams.Count > 1 && audio.Index != audioStreams[0].Index && !profile.SelectsTracksItself)
        {
            reasons.Add("The chosen audio track isn't the file's first");
        }

        if (subtitle is { IsText: false } && !profile.SelectsTracksItself)
        {
            reasons.Add("Picture-based subtitles have to be burned in");
        }

        if (reasons.Count == 0)
        {
            return new PlaybackDecision(PlaybackMethod.DirectPlay, reasons, audio, subtitle, null);
        }

        if (video is null)
        {
            // Audio only (music): convert to AAC when the client can't play the original.
            return audio is null
                ? new PlaybackDecision(PlaybackMethod.DirectPlay, reasons, audio, subtitle, null)
                : new PlaybackDecision(PlaybackMethod.Transcode, reasons, audio, subtitle,
                    new TranscodePlan(-1, audio.Index, 0, 0, 0, false, false, Math.Clamp(Math.Min(audio.Channels ?? 2, profile.MaxAudioChannels), 1, 6), null, audio.Codec));
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

        // Fragmented MP4 (Apple's players) passes Dolby Digital Plus through with its Atmos, and FLAC up to 7.1.
        var fmp4 = profile.Fmp4;
        var channels = audio?.Channels ?? 2;
        var copyAudio = audio is not null
            && (fmp4 ? Fmp4AudioCodecs : HlsAudioCodecs).Contains(audio.Codec)
            && profile.AudioCodecs.Contains(audio.Codec, StringComparer.OrdinalIgnoreCase)
            && channels <= profile.MaxAudioChannels;
        // What can't go as it is goes lossless (FLAC) when it's lossless or surround to begin with, so a DTS or
        // TrueHD track loses nothing more on the way; stereo, or a tight bitrate cap, gets AAC as before.
        var lossless = !copyAudio && audio is not null && fmp4
            && profile.AudioCodecs.Contains("flac", StringComparer.OrdinalIgnoreCase)
            && (maxBitrate == 0 || maxBitrate >= LosslessMinBitrate)
            && (IsLossless(audio) || channels > 2);

        var plan = new TranscodePlan(
            video.Index,
            audio?.Index,
            targetWidth,
            targetHeight,
            Math.Max(bitrate, 500_000),
            ToneMap: file.VideoRange != VideoRange.Sdr,
            copyAudio,
            Math.Clamp(Math.Min(channels, profile.MaxAudioChannels), 1, lossless || copyAudio ? 8 : 6),
            subtitle is { IsText: false } ? subtitle : null,
            audio?.Codec,
            fmp4,
            video.Codec,
            lossless,
            HighResolutionAudio: lossless && IsLossless(audio!));

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
