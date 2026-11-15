using Flexarr.Core.Localization;

namespace Flexarr.Api.Playback;

/// <summary>How a stream's quality is chosen for someone watching away from home (see docs/fast-start.md).</summary>
public enum AwayFromHomeQuality
{
    /// <summary>Lowered only when the file needs more than the viewer's connection carries, by the speed their app measured.</summary>
    Automatic,

    /// <summary>As at home: the file as it is, whenever the device can play it.</summary>
    Original,
}

/// <summary>Whether a viewer is away from home, and the cap that fits their connection then.</summary>
public static class AwayFromHome
{
    /// <summary>A stream's busiest stretches are held to this share of the measured speed, leaving room for the speed to dip.</summary>
    public const double Headroom = 0.75;

    /// <summary>
    /// How far above its average bitrate a stream is taken to run in its busy stretches. A file's bitrate is only its
    /// average: a 1080p episode averaging 4.9 Mbps ran 1.3 times that in its busiest tenth and twice it at its peaks, and
    /// a converted stream may run 1.3 times its own (FFmpeg's maxrate).
    /// </summary>
    public const double PeakFactor = 1.5;

    /// <summary>Never capped below this: lower than this looks worse than a short wait.</summary>
    public const long MinBitrate = 1_500_000;

    /// <summary>
    /// The cap for a stream of a <paramref name="fileBitrate"/> file, or null for none: only with Automatic, a
    /// <paramref name="remote"/> viewer and a speed their app measured that the file (when its bitrate is known) needs
    /// more than. A file needs its busy stretches to fit (<see cref="PeakFactor"/> times its average, within
    /// <see cref="Headroom"/> of the speed), and the cap is the average whose busy stretches fit, so it's always below
    /// the file's own and the file is converted (unless that's under <see cref="MinBitrate"/>).
    /// </summary>
    public static long? Cap(AwayFromHomeQuality quality, bool remote, long? measuredBitrate, long? fileBitrate)
    {
        if (quality != AwayFromHomeQuality.Automatic || !remote || measuredBitrate is not > 0)
        {
            return null;
        }

        // A file of unknown bitrate isn't capped: it may well fit, and capping it would give up repackaging.
        var fits = measuredBitrate.Value * Headroom;
        return fileBitrate is > 0 and var bitrate && bitrate * PeakFactor > fits
            ? Math.Max(MinBitrate, (long)(fits / PeakFactor))
            : null;
    }

    /// <summary>Why a file was converted when only the connection was too slow for it.</summary>
    public static readonly string Reason = Loc.Mark("Your connection is slower than this file needs");

    /// <summary>
    /// Away from home: anything the server's own network rules don't count as the home network (<see cref="Security.NetworkZones"/>),
    /// Tailscale included, as those devices may be anywhere.
    /// </summary>
    public static bool IsRemote(HttpContext http) => Security.NetworkZones.Of(http) != Security.NetworkZone.Local;
}
