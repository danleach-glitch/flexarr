using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Flexarr.Streaming.Live;

/// <summary>
/// Where a channel's stream comes from. <paramref name="Secret"/> is the part of <paramref name="Url"/> that signs in
/// (the source's password), kept so it can be blanked out of anything logged or shown.
/// </summary>
public sealed record LiveSource(string Url, string Secret)
{
    public string Redact(string text) => Secret.Length == 0 ? text : text.Replace(Secret, "***", StringComparison.Ordinal)
        .Replace(Uri.EscapeDataString(Secret), "***", StringComparison.Ordinal);
}

/// <summary>What a channel sends: its first video and audio streams.</summary>
public sealed record LiveFormat(string? VideoCodec, int Width, int Height, bool Interlaced, double FrameRate, string? AudioCodec, int AudioChannels);

/// <summary>
/// How a channel is turned into HLS for every viewer at once: one output that browsers and Apple's players can all
/// play (H.264 and AAC), copied as it comes when it already is.
/// </summary>
public sealed record LivePlan(bool CopyVideo, bool Deinterlace, int TargetHeight, long VideoBitrate, bool CopyAudio)
{
    /// <summary>Highest picture made when converting; a "4K" channel that isn't H.264 comes out at 1080p.</summary>
    public const int MaxHeight = 1080;

    public static LivePlan For(LiveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        // H.264 plays everywhere, at any size, as long as it isn't interlaced (browsers show the comb).
        var copyVideo = format.VideoCodec == "h264" && !format.Interlaced;
        var height = format.Height is > 0 and var h ? Math.Min(h, MaxHeight) : 720;
        var bitrate = height switch
        {
            >= 1080 => 6_000_000L,
            >= 720 => 4_000_000L,
            _ => 2_500_000L,
        };

        // ADTS AAC copies straight into the segments; LATM (DVB), MP2, AC-3 and the rest become stereo AAC.
        return new LivePlan(copyVideo, format.Interlaced, height, bitrate, format.AudioCodec == "aac");
    }

    public string Describe() => CopyVideo && CopyAudio ? "Original"
        : CopyVideo ? "Original video, converted sound"
        : $"Converted to {TargetHeight}p" + (Deinterlace ? ", deinterlaced" : "");
}

/// <summary>Reads a channel's format with ffprobe, over the web protocols only.</summary>
public static class LiveProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public static async Task<LiveFormat> ProbeAsync(string ffprobePath, LiveSource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        var start = new ProcessStartInfo(ffprobePath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[]
        {
            "-v", "error", "-protocol_whitelist", "http,https,tcp,tls", "-rw_timeout", "15000000",
            "-analyzeduration", "4000000", "-probesize", "5000000",
            "-print_format", "json", "-show_streams", "-i", source.Url,
        })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new LiveStreamException($"Could not start {ffprobePath}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw LiveStreamException.FromError(source.Redact((await stderr.ConfigureAwait(false)).Trim()));
            }

            return Parse(await stdout.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LiveStreamException("The channel didn't start sending within 20 seconds.");
        }
        finally
        {
            ProcessSignals.KillTree(process);
        }
    }

    internal static LiveFormat Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        JsonElement? video = null, audio = null;
        if (doc.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                if (type == "video" && video is null && !IsAttachedPicture(stream))
                {
                    video = stream;
                }
                else if (type == "audio" && audio is null)
                {
                    audio = stream;
                }
            }
        }

        if (video is null && audio is null)
        {
            throw new LiveStreamException("The channel sent nothing that plays.");
        }

        var fieldOrder = video is { } v && v.TryGetProperty("field_order", out var f) ? f.GetString() : null;
        return new LiveFormat(
            video is { } vc ? Text(vc, "codec_name") : null,
            video is { } vw ? Number(vw, "width") : 0,
            video is { } vh ? Number(vh, "height") : 0,
            fieldOrder is "tt" or "bb" or "tb" or "bt",
            video is { } vr ? Rate(Text(vr, "avg_frame_rate") ?? Text(vr, "r_frame_rate")) : 0,
            audio is { } ac ? Text(ac, "codec_name") : null,
            audio is { } an ? Number(an, "channels") : 0);

        static bool IsAttachedPicture(JsonElement s) =>
            s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var a) && a.GetInt32() == 1;

        static string? Text(JsonElement s, string name) => s.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        static int Number(JsonElement s, string name) => s.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;

        static double Rate(string? fraction)
        {
            var parts = fraction?.Split('/');
            return parts is [var n, var d]
                && double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
                && double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out var den) && den > 0
                ? num / den
                : 0;
        }
    }
}

/// <summary>A channel that couldn't be started, with why in words for the viewer.</summary>
public sealed partial class LiveStreamException(string message) : Exception(message)
{
    /// <summary>The source had no connection free (the provider's limit), rather than anything being wrong.</summary>
    public bool Busy { get; init; }

    /// <summary>From what ffmpeg or ffprobe said about the input.</summary>
    internal static LiveStreamException FromError(string error) => new(Explain(error)) { Busy = IsBusy(Address().Replace(error, "")) };

    private static bool IsBusy(string error) =>
        error.Contains("5XX", StringComparison.Ordinal) || error.Contains("503", StringComparison.Ordinal) || error.Contains("429", StringComparison.Ordinal);

    /// <summary>ffmpeg's last words about an input, as the viewer would put it, without the address it names.</summary>
    internal static string Explain(string error) => Address().Replace(error, "") switch
    {
        var e when IsBusy(e)
            => "No connection free: the TV provider allows only so many streams at once, and they're in use.",
        var e when e.Contains("401", StringComparison.Ordinal) || e.Contains("403", StringComparison.Ordinal)
            => "The Live TV source refused Flexarr's sign-in. Check Dispatcharr in Settings › Connections.",
        var e when e.Contains("404", StringComparison.Ordinal) => "The Live TV source doesn't have this channel any more.",
        var e when e.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) => "Flexarr can't reach the Live TV source.",
        var e when e.Contains("Input/output error", StringComparison.Ordinal) || e.Contains("End of file", StringComparison.Ordinal)
            => "The provider isn't sending this channel at the moment. Try again later.",
        { Length: > 0 } e => $"The channel couldn't be played: {e.Trim()}",
        _ => "The channel couldn't be played.",
    };

    [System.Text.RegularExpressions.GeneratedRegex(@"[a-z][a-z0-9+.-]*://\S+?(:\s|\s|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex Address();
}
