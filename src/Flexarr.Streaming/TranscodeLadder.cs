namespace Flexarr.Streaming;

/// <summary>
/// One playback's conversions at several qualities, for a player that moves between them by itself (the ladder,
/// docs/remote-streaming.md). Each quality is a <see cref="TranscodeSession"/> of its own, started by the first request
/// for one of its segments, and all of them cut at the same moments (<see cref="FfmpegCommand"/>'s grid), so a player
/// can switch at any segment. Together they count as one slot of the server's limit: the
/// <see cref="TranscodeManager"/> holds only the <see cref="Lead"/>, and ending it ends them all.
/// </summary>
public sealed class TranscodeLadder
{
    /// <summary>A quality nobody has fetched for this long has its ffmpeg stopped (the one being watched keeps the usual two minutes).</summary>
    public static readonly TimeSpan IdleQuality = TimeSpan.FromSeconds(30);

    internal TranscodeLadder(IReadOnlyList<TranscodeSession> variants)
    {
        if (variants.Count < 2)
        {
            throw new ArgumentException("A ladder has at least two qualities.", nameof(variants));
        }

        Variants = variants;
        foreach (var variant in variants)
        {
            variant.Ladder = this;
        }
    }

    /// <summary>The qualities, lowest first, as the master playlist lists them (<c>v/{k}/…</c>).</summary>
    public IReadOnlyList<TranscodeSession> Variants { get; }

    /// <summary>The top quality: the session the playback and the manager know it by, whose id is the playback's.</summary>
    public TranscodeSession Lead => Variants[^1];

    /// <summary>The quality a player fetched most recently (the lowest before any has been): what's being sent.</summary>
    public TranscodeSession Current => Variants.Where(v => v.LastFetched is not null).MaxBy(v => v.LastFetched) ?? Variants[0];

    /// <summary>Whether any of its qualities has ffmpeg running, or about to.</summary>
    internal bool AnyRunsOrStarting => Variants.Any(v => v.RunsOrStarting);
}
