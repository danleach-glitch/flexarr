using Flexarr.Streaming;
using Flexarr.Streaming.Live;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Core.Tests;

public sealed class LiveRewindBudgetTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-live-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>As ffmpeg writes a live window: a date before each segment, and a join where the source dropped.</summary>
    private static string Playlist(int first, int count, int? discontinuityAt = null)
    {
        var lines = new List<string> { "#EXTM3U", "#EXT-X-VERSION:6", "#EXT-X-TARGETDURATION:4", $"#EXT-X-MEDIA-SEQUENCE:{first}", "#EXT-X-INDEPENDENT-SEGMENTS" };
        for (var n = first; n < first + count; n++)
        {
            if (n == discontinuityAt)
            {
                lines.Add("#EXT-X-DISCONTINUITY");
            }

            lines.Add($"#EXT-X-PROGRAM-DATE-TIME:2026-11-15T20:00:{n % 60:00}.000+0000");
            lines.Add("#EXTINF:4.000000,");
            lines.Add($"seg{n}.ts");
        }

        return string.Join('\n', lines) + "\n";
    }

    [Fact]
    public void DroppedSegments_LeaveThePlaylist_WithTheirTags_AndTheSequencesMoveOn()
    {
        var playlist = Playlist(10, 6, discontinuityAt: 11);

        var trimmed = LiveSession.WithoutDropped(playlist, 12);

        Assert.Equal([13, 14, 15], LiveSession.SegmentNumbers(trimmed));
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:13\n", trimmed, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:13\n#EXT-X-DISCONTINUITY-SEQUENCE:1\n", trimmed, StringComparison.Ordinal);
        Assert.DoesNotContain("#EXT-X-DISCONTINUITY\n", trimmed, StringComparison.Ordinal);
        Assert.DoesNotContain("20:00:12", trimmed, StringComparison.Ordinal);
        Assert.StartsWith("#EXTM3U\n#EXT-X-VERSION:6\n#EXT-X-TARGETDURATION:4\n", trimmed, StringComparison.Ordinal);
        Assert.EndsWith("seg15.ts\n", trimmed, StringComparison.Ordinal);

        // Nothing dropped: as ffmpeg wrote it. And the last few always stay, to go on playing.
        Assert.Equal(playlist, LiveSession.WithoutDropped(playlist, -1));
        Assert.Equal([13, 14, 15], LiveSession.SegmentNumbers(LiveSession.WithoutDropped(playlist, 99)));
    }

    [Fact]
    public async Task AWindowOverItsBudget_DropsItsOldestSegments_AndStopsServingThem()
    {
        await using var session = Session();
        await WriteAsync(session, first: 0, count: 10, bytes: 1000);
        session.RewindBudget = 4500;
        session.FreeBytes = _ => null;

        Assert.Equal(6, session.KeepWithinBudget());

        Assert.Equal([6, 7, 8, 9], LiveSession.SegmentNumbers(session.ReadPlaylist()!));
        Assert.Null(session.SegmentPath("seg5.ts"));
        Assert.NotNull(session.SegmentPath("seg6.ts"));
        // Emptied, not deleted: ffmpeg still finds it to delete when it slides past.
        Assert.Equal(0, new FileInfo(Path.Combine(session.Directory, "seg5.ts")).Length);
        Assert.Equal(1000, new FileInfo(Path.Combine(session.Directory, "seg6.ts")).Length);

        // Within budget, nothing more goes.
        Assert.Equal(0, session.KeepWithinBudget());
    }

    [Fact]
    public async Task WithTheDiskNearlyFull_AWindowKeepsOnlyWhatItCanSpare_ButAlwaysTheLastFew()
    {
        await using var session = Session();
        await WriteAsync(session, first: 20, count: 8, bytes: 1000);
        // 2,500 bytes over the least the disk must keep free.
        session.FreeBytes = _ => TranscodeManager.MinFreeBytes - 2500;

        Assert.Equal(3, session.KeepWithinBudget());
        Assert.Equal([23, 24, 25, 26, 27], LiveSession.SegmentNumbers(session.ReadPlaylist()!));

        // However short of room, the newest three stay.
        session.FreeBytes = _ => 0;
        session.KeepWithinBudget();
        Assert.Equal([25, 26, 27], LiveSession.SegmentNumbers(session.ReadPlaylist()!));
    }

    [Fact]
    public async Task AChannelWithinBudget_KeepsItsWholeWindow()
    {
        await using var session = Session();
        await WriteAsync(session, first: 0, count: 10, bytes: 1000);
        session.FreeBytes = _ => 100L << 30;

        Assert.Equal(0, session.KeepWithinBudget());
        Assert.Equal(Enumerable.Range(0, 10), LiveSession.SegmentNumbers(session.ReadPlaylist()!));
    }

    private LiveSession Session() => new("channel", new LiveSource("http://dispatcharr/live/1.ts", ""), LivePlan.For(new LiveFormat("h264", 1920, 1080, false, 25, "aac", 2)),
        TimeSpan.FromHours(1), new TranscodeSettings { CacheDirectory = _dir, FfmpegPath = "ffmpeg" }, NullLogger.Instance);

    private static async Task WriteAsync(LiveSession session, int first, int count, int bytes)
    {
        for (var n = first; n < first + count; n++)
        {
            await File.WriteAllBytesAsync(Path.Combine(session.Directory, $"seg{n}.ts"), new byte[bytes]);
        }

        await File.WriteAllTextAsync(Path.Combine(session.Directory, LiveSession.PlaylistName), Playlist(first, count));
    }
}
