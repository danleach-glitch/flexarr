using System.Globalization;

namespace Flexarr.Streaming.Live;

/// <summary>The ffmpeg arguments that turn a channel's stream into a live HLS playlist with a rewind window.</summary>
public static class LiveCommand
{
    /// <summary>Segment length. Copied video is cut at the source's keyframes, so its segments can run a little longer.</summary>
    public const int SegmentSeconds = 4;

    /// <param name="rewind">How far back viewers can go: the playlist keeps this much, and older segments are deleted.</param>
    /// <param name="resume">A restart after the source dropped: carry on the same playlist, marking the join.</param>
    public static IReadOnlyList<string> Hls(LiveSource source, LivePlan plan, string outputDirectory, TranscodeSettings settings, TimeSpan rewind, bool resume)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(settings);

        var hw = plan.CopyVideo ? HardwareAcceleration.None : settings.Resolved;
        List<string> args =
        [
            "-hide_banner", "-v", "error", "-nostdin", "-y",
            // Broadcast streams arrive with gaps and the odd broken packet; rebuild timestamps and drop what's corrupt.
            "-fflags", "+genpts+discardcorrupt",
            "-protocol_whitelist", "http,https,tcp,tls",
            "-reconnect", "1", "-reconnect_streamed", "1", "-reconnect_on_network_error", "1", "-reconnect_delay_max", "10",
            "-rw_timeout", "20000000",
        ];
        if (hw == HardwareAcceleration.Vaapi)
        {
            args.AddRange(["-vaapi_device", settings.VaapiDevice]);
        }

        args.AddRange(["-i", source.Url, "-map", "0:v:0", "-map", "0:a:0?"]);
        AddVideo(args, plan, hw, settings);

        args.AddRange(plan.CopyAudio ? ["-c:a", "copy"] : ["-c:a", "aac", "-ac", "2", "-b:a", plan.Reduced ? "128k" : "192k"]);

        var flags = "delete_segments+temp_file+independent_segments+program_date_time+omit_endlist" + (resume ? "+append_list+discont_start" : "");
        var listSize = Math.Max(3, (int)Math.Ceiling(rewind.TotalSeconds / SegmentSeconds));
        args.AddRange([
            "-sn", "-dn", "-map_metadata", "-1",
            "-max_muxing_queue_size", "2048",
            "-f", "hls",
            "-hls_time", SegmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-hls_list_size", listSize.ToString(CultureInfo.InvariantCulture),
            "-hls_delete_threshold", "3",
            "-hls_flags", flags,
            "-hls_segment_type", "mpegts",
            "-hls_segment_filename", Path.Combine(outputDirectory, "seg%d.ts"),
            Path.Combine(outputDirectory, LiveSession.PlaylistName),
        ]);
        return args;
    }

    private static void AddVideo(List<string> args, LivePlan plan, HardwareAcceleration hw, TranscodeSettings settings)
    {
        if (plan.CopyVideo)
        {
            args.AddRange(["-c:v", "copy"]);
            return;
        }

        // Decoded in software (broadcast MPEG-2 and odd H.264 trip the hardware decoders), deinterlaced to a frame per
        // field so motion stays smooth, then encoded in hardware where there is some.
        var height = plan.TargetHeight.ToString(CultureInfo.InvariantCulture);
        var filters = new List<string>();
        if (plan.Deinterlace)
        {
            filters.Add("bwdif=mode=send_field:deint=interlaced");
        }

        filters.Add($"scale=-2:'min({height},ih)'");
        filters.Add(hw == HardwareAcceleration.Vaapi ? "format=nv12,hwupload" : "format=yuv420p");
        args.AddRange(["-vf", string.Join(',', filters)]);

        var bitrate = plan.VideoBitrate.ToString(CultureInfo.InvariantCulture);
        var maxrate = ((long)(plan.VideoBitrate * 1.3)).ToString(CultureInfo.InvariantCulture);
        var bufsize = (plan.VideoBitrate * 2).ToString(CultureInfo.InvariantCulture);
        args.AddRange(hw switch
        {
            HardwareAcceleration.VideoToolbox => ["-c:v", "h264_videotoolbox", "-profile:v", "high", "-realtime", "1", "-b:v", bitrate, "-maxrate", maxrate, "-bufsize", bufsize, "-allow_sw", "1"],
            HardwareAcceleration.Vaapi => ["-c:v", "h264_vaapi", "-b:v", bitrate, "-maxrate", maxrate, "-bufsize", bufsize],
            HardwareAcceleration.Qsv => ["-c:v", "h264_qsv", "-preset", "veryfast", "-b:v", bitrate, "-maxrate", maxrate, "-bufsize", bufsize],
            HardwareAcceleration.Nvenc => ["-c:v", "h264_nvenc", "-preset", "p4", "-b:v", bitrate, "-maxrate", maxrate, "-bufsize", bufsize],
            _ => ["-c:v", "libx264", "-preset", settings.SoftwarePreset, "-tune", "zerolatency", "-crf", "21", "-maxrate", maxrate, "-bufsize", bufsize],
        });

        // A keyframe at every segment boundary, whatever the frame rate.
        args.AddRange(["-force_key_frames", $"expr:gte(t,n_forced*{SegmentSeconds})"]);
    }
}
