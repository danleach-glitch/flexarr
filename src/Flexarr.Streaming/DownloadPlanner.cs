using Flexarr.Core.Media;

namespace Flexarr.Streaming;

/// <summary>
/// How a download is made: sent as it is (an MP4 the phone plays), repackaged as an MP4 with the streams copied, or
/// converted with <see cref="Convert"/>. <see cref="Subtitles"/> are the text tracks that go in with it. A track
/// (<see cref="VideoIndex"/> of -1) is sent as it is or converted to AAC in an M4A.
/// </summary>
public sealed record DownloadPlan(TranscodePlan? Convert, bool SendAsIs, int VideoIndex, int? AudioIndex, string? VideoCodec, IReadOnlyList<SubtitleTrack> Subtitles)
{
    /// <summary>A track: sound alone, kept as an audio file rather than an MP4.</summary>
    public bool IsAudioOnly => VideoIndex < 0;
}

/// <summary>Chooses how a film, episode or track is got ready for keeping on an iPhone or iPad.</summary>
public static class DownloadPlanner
{
    private static readonly HashSet<string> PhoneVideo = new(StringComparer.OrdinalIgnoreCase) { "h264", "hevc" };
    private static readonly HashSet<string> PhoneAudio = new(StringComparer.OrdinalIgnoreCase) { "aac", "mp3", "ac3", "eac3", "alac" };

    /// <summary>
    /// Music files an iPhone plays as they are, by container (as ffprobe names it) and the codecs that container may
    /// hold: AAC or ALAC in an M4A, MP3, FLAC, WAV and AIFF. Anything else (Vorbis, Opus, WMA, APE, DSD…) converts.
    /// </summary>
    private static readonly Dictionary<string, string[]> PhoneMusic = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mp4"] = ["aac", "alac"],
        ["mov"] = ["aac", "alac"],
        ["m4a"] = ["aac", "alac"],
        ["mp3"] = ["mp3"],
        ["flac"] = ["flac"],
        ["wav"] = ["pcm_s16le", "pcm_s24le", "pcm_s32le", "pcm_f32le", "pcm_u8"],
        ["aiff"] = ["pcm_s16be", "pcm_s24be", "pcm_s32be", "pcm_f32be"],
    };

    /// <summary>What a converted track is encoded at: AAC, which sounds transparent at this rate.</summary>
    public const long TrackBitrate = 256_000;

    /// <summary>The biggest picture, the bitrate cap (video and audio) and the audio channels for each size.</summary>
    public static (int MaxHeight, long MaxBitrate, int Channels) Size(DownloadQuality quality) => quality switch
    {
        DownloadQuality.High => (1080, 4_300_000, 6),
        DownloadQuality.Medium => (720, 2_300_000, 2),
        DownloadQuality.Small => (480, 1_250_000, 2),
        _ => (2160, 0, 6),
    };

    /// <summary>
    /// Whether the file's own video and first audio track play on a phone, so an Original download needs no
    /// conversion (at most repackaging as MP4).
    /// </summary>
    public static bool CanKeepOriginal(MediaFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var video = file.Streams.Where(s => s.Type == StreamType.Video).OrderBy(s => s.Index).FirstOrDefault();
        var audio = file.Streams.Where(s => s.Type == StreamType.Audio).OrderBy(s => s.Index).FirstOrDefault();
        return video is not null && PhoneVideo.Contains(video.Codec) && (audio is null || PhoneAudio.Contains(audio.Codec));
    }

    /// <summary>Whether a track's file plays on a phone as it is, so it's sent as it is rather than converted.</summary>
    public static bool CanKeepTrack(MediaFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var audio = file.Streams.Where(s => s.Type == StreamType.Audio).OrderBy(s => s.Index).FirstOrDefault();
        return audio is not null && (file.Container ?? "").Split(',').Any(c => PhoneMusic.TryGetValue(c.Trim(), out var codecs) && codecs.Contains(audio.Codec, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// How to make the download, or null for an Original the phone couldn't play (another size will do). A file with
    /// no picture is a track: sent as it is when the phone plays it, else converted to AAC, whatever size was asked.
    /// </summary>
    public static DownloadPlan? Plan(MediaFile file, DownloadQuality quality, IReadOnlyList<SubtitleTrack> subtitles)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(subtitles);
        // Captions in the video's frames travel with the picture when it's copied, and can't be mapped as a stream.
        var text = subtitles.Where(s => s.IsText && !s.InVideo).ToList();
        var video = file.Streams.Where(s => s.Type == StreamType.Video).OrderBy(s => s.Index).FirstOrDefault();
        var audio = file.Streams.Where(s => s.Type == StreamType.Audio).OrderBy(s => s.Index).FirstOrDefault();
        if (video is null)
        {
            if (audio is null)
            {
                return null;
            }

            var convert = CanKeepTrack(file)
                ? null
                : new TranscodePlan(-1, audio.Index, 0, 0, 0, false, false, Math.Clamp(audio.Channels ?? 2, 1, 2), null, audio.Codec);
            return new DownloadPlan(convert, convert is null, -1, audio.Index, null, []);
        }

        if (quality == DownloadQuality.Original)
        {
            if (!CanKeepOriginal(file))
            {
                return null;
            }

            // An MP4 already, with every audio track playable and no subtitle files beside it to bring in: send it.
            var isMp4 = file.Container?.Split(',').Any(c => c is "mp4" or "mov" or "m4v") == true;
            var allAudioPlays = file.Streams.Where(s => s.Type == StreamType.Audio).All(s => PhoneAudio.Contains(s.Codec));
            var sendAsIs = isMp4 && allAudioPlays && text.All(t => t.ExternalPath is null);
            return new DownloadPlan(null, sendAsIs, video.Index, audio?.Index, video.Codec, sendAsIs ? [] : text);
        }

        var (maxHeight, maxBitrate, channels) = Size(quality);
        var phone = new DeviceProfile
        {
            Client = "download",
            Containers = ["mp4"],
            VideoCodecs = ["h264"],
            AudioCodecs = ["aac"],
            MaxHeight = maxHeight,
            MaxAudioChannels = channels,
            MaxBitrate = maxBitrate,
        };
        var decision = PlaybackDecider.Decide(file, phone, [], new PlaybackOptions(MaxBitrate: maxBitrate, MaxHeight: maxHeight, ForceTranscode: true));
        return decision.Transcode is { } plan ? new DownloadPlan(plan, false, video.Index, plan.AudioStreamIndex, "h264", text) : null;
    }

    /// <summary>About how big the download will be, for choosing a size before it's made.</summary>
    public static long EstimateBytes(MediaFile file, DownloadQuality quality)
    {
        ArgumentNullException.ThrowIfNull(file);
        var seconds = file.DurationSeconds ?? 0;
        if (file.Streams.All(s => s.Type != StreamType.Video))
        {
            // A track: its own file, or the AAC it converts to.
            return CanKeepTrack(file) ? file.Size : (long)(TrackBitrate * seconds / 8);
        }

        if (quality == DownloadQuality.Original)
        {
            return file.Size;
        }

        var plan = Plan(file, quality, [])?.Convert;
        var video = plan?.VideoBitrate ?? Size(quality).MaxBitrate;
        var audio = plan is { AudioChannels: > 2 } ? 384_000 : 160_000;
        return (long)((video + audio) * seconds / 8);
    }
}
