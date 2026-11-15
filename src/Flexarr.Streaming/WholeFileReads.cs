namespace Flexarr.Streaming;

/// <summary>
/// One at a time for the background jobs that read a whole file (scrubbing previews, a keyframe scan): on a big remux
/// over a network share each takes minutes of reading, and two at once, beside the stream being watched, compete for
/// the drive as well as the processor (which <c>BelowNormal</c> alone doesn't help with). Subtitles a viewer is waiting
/// for don't queue here.
/// </summary>
public static class WholeFileReads
{
    public static SemaphoreSlim Gate { get; } = new(1, 1);
}
