using System.Globalization;
using Flexarr.Streaming.Remux;

namespace Flexarr.Streaming;

/// <summary>Builds ffmpeg arguments for HLS transcodes that can start at any segment.</summary>
public static class FfmpegCommand
{
    public const int SegmentSeconds = 6;

    /// <summary>
    /// How far ahead of real time a stream is produced: this much in one burst, then at playback speed, so a viewer
    /// has a buffer to seek within without the server encoding the whole film at once.
    /// </summary>
    public const int RunAheadSeconds = 180;

    /// <summary>ffmpeg's own pacing: the burst, then real time (ffmpeg 6 and later for the burst).</summary>
    private static readonly string[] Pacing = ["-readrate", "1.0", "-readrate_initial_burst", RunAheadSeconds.ToString(CultureInfo.InvariantCulture)];

    /// <summary>
    /// Arguments to produce segments <paramref name="startSegment"/>.. of <paramref name="input"/> into
    /// <paramref name="outputDirectory"/>. Keyframes are forced every <see cref="SegmentSeconds"/> so every
    /// restart lines up with the pre-generated VOD playlist, and timestamps are offset to the segment's real
    /// position so segments from different runs stitch together.
    /// </summary>
    public static IReadOnlyList<string> Hls(string input, TranscodePlan plan, int startSegment, string outputDirectory, TranscodeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(settings);

        if (plan.Repackage is { } repackage)
        {
            return Repackaged(input, plan, repackage, startSegment, outputDirectory);
        }

        // Audio-only plans (music) have no video stream, so nothing to decode on the GPU.
        var audioOnly = plan.VideoStreamIndex < 0;
        var hw = audioOnly ? HardwareAcceleration.None : settings.Resolved;
        var start = (startSegment * SegmentSeconds).ToString(CultureInfo.InvariantCulture);
        var burn = plan.BurnSubtitle;
        var args = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-y" };

        // Decode on the GPU when it can (H.264 and HEVC); an AVI's MPEG-4, VC-1 and the like are decoded in software
        // and still encoded in hardware. Keep frames on the GPU unless subtitles need burning in (overlay runs on the CPU).
        switch (DecodesInHardware(hw, plan.SourceVideoCodec))
        {
            case HardwareAcceleration.VideoToolbox:
                args.AddRange(["-hwaccel", "videotoolbox"]);
                if (burn is null)
                {
                    args.AddRange(["-hwaccel_output_format", "videotoolbox_vld"]);
                }

                break;
            case HardwareAcceleration.Vaapi:
                args.AddRange(["-hwaccel", "vaapi", "-hwaccel_device", settings.VaapiDevice]);
                if (burn is null)
                {
                    args.AddRange(["-hwaccel_output_format", "vaapi"]);
                }

                break;
            case HardwareAcceleration.Qsv:
                args.AddRange(["-hwaccel", "qsv"]);
                break;
            case HardwareAcceleration.Nvenc:
                args.AddRange(["-hwaccel", "cuda"]);
                if (burn is null)
                {
                    args.AddRange(["-hwaccel_output_format", "cuda"]);
                }

                break;
        }

        args.AddRange([.. Pacing, "-ss", start, "-i", input]);
        if (burn?.ExternalPath is { } subtitleFile)
        {
            args.AddRange(["-ss", start, "-i", subtitleFile]);
        }

        // ---- Video
        if (!audioOnly)
        {
            AddVideo(args, plan, burn, hw, settings);
        }

        AddAudioAndOutput(args, plan, start, startSegment, outputDirectory);
        return args;
    }

    /// <summary>
    /// Arguments to copy the video as it is from segment <paramref name="startSegment"/>'s keyframe, into fragmented MP4
    /// cut at every keyframe (frag0.m4s, frag1.m4s…). The session finds each fragment's place by its own timestamp and
    /// serves a segment as the fragments between two of <see cref="Repackage.SegmentStarts"/>.
    /// </summary>
    private static List<string> Repackaged(string input, TranscodePlan plan, Repackage repackage, int startSegment, string outputDirectory)
    {
        var start = repackage.SegmentStarts[Math.Clamp(startSegment, 0, repackage.SegmentStarts.Count - 1)].ToString("0.###", CultureInfo.InvariantCulture);
        List<string> args = ["-hide_banner", "-v", "error", "-nostdin", "-y", .. Pacing, "-ss", start, "-i", input];
        args.AddRange(["-map", $"0:{plan.VideoStreamIndex}", "-c:v", "copy", "-tag:v", repackage.CodecTag]);
        if (repackage.StripDolbyVision)
        {
            args.AddRange(["-bsf:v", "dovi_rpu=strip=1"]);
        }

        // Keep the file's own timeline (from its start), so fragments from any run line up with the index; "unofficial"
        // lets the MP4 muxer write the Dolby Vision configuration box.
        args.AddRange(["-copyts", "-start_at_zero", "-avoid_negative_ts", "disabled", "-strict", "unofficial"]);
        AddAudio(args, plan);
        args.AddRange([
            "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1",
            "-max_muxing_queue_size", "4096",
            "-f", "hls",
            "-hls_time", "0.001",
            "-hls_list_size", "0",
            "-hls_playlist_type", "event",
            "-start_number", "0",
            "-hls_segment_type", "fmp4", "-hls_fmp4_init_filename", "init.mp4", "-hls_segment_options", "movflags=+frag_discont",
            "-hls_segment_filename", Path.Combine(outputDirectory, "frag%d.m4s"),
            Path.Combine(outputDirectory, "ffmpeg.m3u8"),
        ]);
        return args;
    }

    /// <summary>
    /// Arguments to make one MP4 of <paramref name="input"/> for keeping on a phone: converted by <paramref name="plan"/>,
    /// or with no plan the video and <paramref name="copyAudioIndex"/> copied as they are (an Original download that
    /// needs repackaging). Text subtitles go in as tracks of their own; progress is written to stdout for the caller.
    /// </summary>
    public static IReadOnlyList<string> Mp4(string input, TranscodePlan? plan, int videoIndex, int? copyAudioIndex, string? videoCodec,
        IReadOnlyList<SubtitleTrack> textSubtitles, string output, TranscodeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(textSubtitles);
        ArgumentNullException.ThrowIfNull(settings);

        var hw = plan is null || plan.VideoStreamIndex < 0 ? HardwareAcceleration.None : settings.Resolved;
        var args = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-y", "-progress", "pipe:1", "-nostats" };
        switch (DecodesInHardware(hw, plan?.SourceVideoCodec))
        {
            case HardwareAcceleration.VideoToolbox:
                args.AddRange(["-hwaccel", "videotoolbox", "-hwaccel_output_format", "videotoolbox_vld"]);
                break;
            case HardwareAcceleration.Vaapi:
                args.AddRange(["-hwaccel", "vaapi", "-hwaccel_device", settings.VaapiDevice, "-hwaccel_output_format", "vaapi"]);
                break;
            case HardwareAcceleration.Qsv:
                args.AddRange(["-hwaccel", "qsv"]);
                break;
            case HardwareAcceleration.Nvenc:
                args.AddRange(["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"]);
                break;
        }

        args.AddRange(["-i", input]);
        var sidecars = textSubtitles.Where(t => t.ExternalPath is not null).ToList();
        foreach (var sidecar in sidecars)
        {
            args.AddRange(["-i", sidecar.ExternalPath!]);
        }

        if (plan is not null)
        {
            if (plan.VideoStreamIndex >= 0)
            {
                AddVideo(args, plan, null, hw, settings);
            }

            if (plan.AudioStreamIndex is { } audioIndex)
            {
                args.AddRange(["-map", $"0:{audioIndex}"]);
                args.AddRange(plan.CopyAudio
                    ? ["-c:a", "copy"]
                    : ["-c:a", "aac", "-ac", plan.AudioChannels.ToString(CultureInfo.InvariantCulture), "-b:a", plan.AudioChannels > 2 ? "384k" : "160k"]);
            }
        }
        else
        {
            args.AddRange(["-map", $"0:{videoIndex}", "-c:v", "copy"]);
            // Apple's players only open HEVC in an MP4 tagged hvc1.
            if (videoCodec == "hevc")
            {
                args.AddRange(["-tag:v", "hvc1"]);
            }

            if (copyAudioIndex is { } audioIndex)
            {
                args.AddRange(["-map", $"0:{audioIndex}", "-c:a", "copy"]);
            }
        }

        // ---- Subtitles: each text track as a track of the MP4, with its language, for the player's menu.
        var n = 0;
        foreach (var track in textSubtitles)
        {
            var source = track.ExternalPath is null ? $"0:{track.StreamIndex}" : $"{sidecars.IndexOf(track) + 1}:0";
            args.AddRange(["-map", source]);
            if (track.Language is { Length: > 0 } language)
            {
                args.AddRange([$"-metadata:s:s:{n}", $"language={language}"]);
            }

            if (track.IsForced)
            {
                args.AddRange([$"-disposition:s:{n}", "forced"]);
            }

            n++;
        }

        if (n > 0)
        {
            args.AddRange(["-c:s", "mov_text"]);
        }

        args.AddRange(["-dn", "-map_metadata", "-1", "-map_chapters", "-1", "-max_muxing_queue_size", "2048", "-movflags", "+faststart", "-f", "mp4", output]);
        return args;
    }

    /// <summary>The GPU decodes H.264 and HEVC; asked to decode anything else, VideoToolbox fails outright, so those go
    /// through the software decoder (cheap: they're old, small formats) and only the encoder runs in hardware.</summary>
    internal static HardwareAcceleration DecodesInHardware(HardwareAcceleration hw, string? sourceCodec) =>
        sourceCodec is null or "h264" or "hevc" ? hw : HardwareAcceleration.None;

    private static void AddVideo(List<string> args, TranscodePlan plan, SubtitleTrack? burn, HardwareAcceleration hw, TranscodeSettings settings)
    {
        var height = plan.TargetHeight.ToString(CultureInfo.InvariantCulture);
        var width = plan.TargetWidth.ToString(CultureInfo.InvariantCulture);
        var decodedInSoftware = DecodesInHardware(hw, plan.SourceVideoCodec) == HardwareAcceleration.None && hw != HardwareAcceleration.None;
        if (burn is not null)
        {
            var subtitleInput = burn.ExternalPath is null ? $"[0:{burn.StreamIndex}]" : "[1:0]";
            var tail = hw == HardwareAcceleration.Vaapi ? ",format=nv12,hwupload" : ",format=yuv420p";
            args.AddRange(["-filter_complex", $"[0:{plan.VideoStreamIndex}]{subtitleInput}overlay,scale={width}:{height}{tail}[v]", "-map", "[v]"]);
        }
        else if (decodedInSoftware)
        {
            // Software frames scaled (and HDR mapped to SDR) on the CPU, then handed to the hardware encoder.
            args.AddRange(["-map", $"0:{plan.VideoStreamIndex}"]);
            var tonemap = plan.ToneMap ? "zscale=transfer=linear:npl=100,tonemap=hable,zscale=transfer=bt709:matrix=bt709:primaries=bt709," : "";
            var tail = hw == HardwareAcceleration.Vaapi ? "format=nv12,hwupload" : "format=yuv420p";
            args.AddRange(["-vf", $"{tonemap}scale={width}:{height},{tail}"]);
        }
        else
        {
            args.AddRange(["-map", $"0:{plan.VideoStreamIndex}"]);
            var filter = hw switch
            {
                // scale_vt also converts HDR (PQ/HLG) to SDR BT.709 on the media engine.
                HardwareAcceleration.VideoToolbox => $"scale_vt=w={width}:h={height}" + (plan.ToneMap ? ":color_matrix=bt709:color_primaries=bt709:color_transfer=bt709" : ""),
                HardwareAcceleration.Vaapi => $"scale_vaapi=w={width}:h={height}:format=nv12" + (plan.ToneMap ? ",tonemap_vaapi=format=nv12:t=bt709:m=bt709:p=bt709" : ""),
                HardwareAcceleration.Nvenc => $"scale_cuda={width}:{height}:format=yuv420p",
                _ => $"scale={width}:{height},format=yuv420p",
            };
            args.AddRange(["-vf", filter]);
        }

        var bitrate = plan.VideoBitrate.ToString(CultureInfo.InvariantCulture);
        var maxrate = ((long)(plan.VideoBitrate * 1.3)).ToString(CultureInfo.InvariantCulture);
        var bufsize = (plan.VideoBitrate * 2).ToString(CultureInfo.InvariantCulture);
        args.AddRange(hw switch
        {
            HardwareAcceleration.VideoToolbox => ["-c:v", "h264_videotoolbox", "-profile:v", "high", "-b:v", bitrate, "-maxrate", maxrate, "-bufsize", bufsize, "-allow_sw", "1"],
            HardwareAcceleration.Vaapi => ["-c:v", "h264_vaapi", "-b:v", bitrate, "-maxrate", maxrate, "-bufsize", bufsize],
            HardwareAcceleration.Qsv => ["-c:v", "h264_qsv", "-preset", "veryfast", "-b:v", bitrate, "-maxrate", maxrate, "-bufsize", bufsize],
            HardwareAcceleration.Nvenc => ["-c:v", "h264_nvenc", "-preset", "p4", "-b:v", bitrate, "-maxrate", maxrate, "-bufsize", bufsize],
            _ => ["-c:v", "libx264", "-preset", settings.SoftwarePreset, "-crf", "21", "-maxrate", maxrate, "-bufsize", bufsize, "-sc_threshold", "0"],
        });
        args.AddRange(["-force_key_frames", $"expr:gte(t,n_forced*{SegmentSeconds})", "-g", "1000", "-keyint_min", "1"]);
    }

    private static void AddAudioAndOutput(List<string> args, TranscodePlan plan, string start, int startSegment, string outputDirectory)
    {
        AddAudio(args, plan);

        // Fragmented MP4: the MP4 muxer starts each run's timestamps at zero unless told every fragment may jump, so
        // segments from a run started mid-film line up with the rest.
        var (segmentFile, fmp4) = plan.Fmp4
            ? ("seg%d.m4s", (string[])["-hls_segment_type", "fmp4", "-hls_fmp4_init_filename", "init.mp4", "-hls_segment_options", "movflags=+frag_discont"])
            : ("seg%d.ts", []);

        // ---- HLS output
        args.AddRange([
            "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1",
            "-max_muxing_queue_size", "2048",
            "-output_ts_offset", start,
            "-f", "hls",
            "-hls_time", SegmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-hls_list_size", "0",
            "-hls_playlist_type", "event",
            "-start_number", startSegment.ToString(CultureInfo.InvariantCulture),
            .. fmp4,
            "-hls_segment_filename", Path.Combine(outputDirectory, segmentFile),
            Path.Combine(outputDirectory, "ffmpeg.m3u8"),
        ]);
    }

    private static void AddAudio(List<string> args, TranscodePlan plan)
    {
        if (plan.AudioStreamIndex is { } audioIndex)
        {
            var channels = plan.AudioChannels.ToString(CultureInfo.InvariantCulture);
            args.AddRange(["-map", $"0:{audioIndex}"]);
            if (plan.CopyAudio)
            {
                args.AddRange(["-c:a", "copy"]);
            }
            else if (plan.LosslessAudio)
            {
                // FLAC at 48 kHz, 24-bit from a lossless source (16 is plenty for a lossy one). Its block size is
                // set because the MP4 muxer otherwise hands the encoder one it refuses.
                args.AddRange(["-c:a", "flac", "-ac", channels, "-ar", "48000", "-frame_size", "4096"]);
                args.AddRange(plan.HighResolutionAudio ? ["-sample_fmt", "s32", "-bits_per_raw_sample", "24"] : ["-sample_fmt", "s16"]);
            }
            else
            {
                args.AddRange(["-c:a", "aac", "-ac", channels, "-b:a", plan.AudioChannels > 2 ? "384k" : "192k"]);
            }
        }
    }

    /// <summary>
    /// A full VOD playlist of fixed-length segments, so players can seek anywhere immediately. With
    /// <paramref name="startSeconds"/>, it says where to open (EXT-X-START), so a player resuming partway fetches from
    /// there rather than from the beginning first.
    /// </summary>
    public static string MediaPlaylist(double durationSeconds, double startSeconds = 0, bool fmp4 = false)
    {
        var count = SegmentCount(durationSeconds);
        var builder = new System.Text.StringBuilder();
        builder.Append(fmp4 ? "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:" : "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:")
            .Append(SegmentSeconds + 1)
            .Append("\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-INDEPENDENT-SEGMENTS\n");
        if (fmp4)
        {
            builder.Append("#EXT-X-MAP:URI=\"init.mp4\"\n");
        }

        if (startSeconds > 0 && startSeconds < durationSeconds)
        {
            builder.Append(CultureInfo.InvariantCulture, $"#EXT-X-START:TIME-OFFSET={startSeconds:F3},PRECISE=YES\n");
        }

        for (var i = 0; i < count; i++)
        {
            var length = Math.Min(SegmentSeconds, durationSeconds - (i * SegmentSeconds));
            builder.Append(CultureInfo.InvariantCulture, $"#EXTINF:{length:F3},\n{i}.{(fmp4 ? "m4s" : "ts")}\n");
        }

        return builder.Append("#EXT-X-ENDLIST\n").ToString();
    }

    /// <summary>A VOD playlist of a repackaged video's segments, which start at its keyframes and so vary in length.</summary>
    public static string MediaPlaylist(IReadOnlyList<double> segmentStarts, double durationSeconds, double startSeconds = 0)
    {
        ArgumentNullException.ThrowIfNull(segmentStarts);
        var lengths = segmentStarts.Select((start, i) => (i + 1 < segmentStarts.Count ? segmentStarts[i + 1] : durationSeconds) - start).ToList();
        var builder = new System.Text.StringBuilder("#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:");
        builder.Append(CultureInfo.InvariantCulture, $"{(int)Math.Ceiling(lengths.Max())}\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-INDEPENDENT-SEGMENTS\n");
        if (startSeconds > 0 && startSeconds < durationSeconds)
        {
            builder.Append(CultureInfo.InvariantCulture, $"#EXT-X-START:TIME-OFFSET={startSeconds:F3},PRECISE=YES\n");
        }

        builder.Append("#EXT-X-MAP:URI=\"init.mp4\"\n");
        for (var i = 0; i < lengths.Count; i++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"#EXTINF:{lengths[i]:F3},\n{i}.m4s\n");
        }

        return builder.Append("#EXT-X-ENDLIST\n").ToString();
    }

    public static int SegmentCount(double durationSeconds) => Math.Max(1, (int)Math.Ceiling(durationSeconds / SegmentSeconds));
}
