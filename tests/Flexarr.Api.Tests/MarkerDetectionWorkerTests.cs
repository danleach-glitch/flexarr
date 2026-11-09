using Flexarr.Api.Playback;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>When finding intros and credits waits for people watching, before each episode it analyses.</summary>
public class MarkerDetectionWorkerTests
{
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithNobodyWatching_ItDoesntWait(bool asked)
    {
        Assert.Equal(0, await MarkerDetectionWorker.GiveWayAsync(() => false, () => false, asked, Pause, most: 30, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhileSomeoneWatchesAFileAsItIs_ItPausesOnceAnEpisode(bool asked)
    {
        Assert.Equal(1, await MarkerDetectionWorker.GiveWayAsync(() => false, () => true, asked, Pause, most: 30, CancellationToken.None));
    }

    [Fact]
    public async Task WhileAStreamIsBeingConverted_TheBackgroundSweepWaitsUntilItsOver()
    {
        var checks = 0;

        var pauses = await MarkerDetectionWorker.GiveWayAsync(() => ++checks <= 4, () => true, asked: false, Pause, most: 30, CancellationToken.None);

        Assert.Equal(4, pauses);
        Assert.Equal(5, checks);
    }

    [Fact]
    public async Task OnAServerThatsAlwaysConverting_TheSweepStillGetsOnWithIt_AfterItsLongestWait()
    {
        Assert.Equal(30, await MarkerDetectionWorker.GiveWayAsync(() => true, () => true, asked: false, Pause, most: 30, CancellationToken.None));
    }

    [Fact]
    public async Task ASeasonSomeoneJustStartedWatching_OnlyWaitsOnePauseAnEpisode_EvenWhileItsBeingConverted()
    {
        Assert.Equal(1, await MarkerDetectionWorker.GiveWayAsync(() => true, () => true, asked: true, Pause, most: 30, CancellationToken.None));
    }

    [Fact]
    public async Task ShuttingDown_EndsTheWait()
    {
        using var stopping = new CancellationTokenSource();
        var waiting = MarkerDetectionWorker.GiveWayAsync(() => true, () => true, asked: false, TimeSpan.FromMinutes(1), most: 30, stopping.Token);

        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Theory]
    [InlineData(2640, 2642.64, true)] // 44 minutes at 24 and 23.976 frames a second.
    [InlineData(1320, 1321.4, true)]
    [InlineData(1320, 1325, false)]
    [InlineData(7200, 7300, false)]
    public void Versions_this_close_in_length_share_their_marks(double a, double b, bool same) =>
        Assert.Equal(same, MarkerDetectionWorker.SameEdit(a, b));

    [Fact]
    public void Shared_marks_are_stretched_to_the_versions_length()
    {
        var credits = new Flexarr.Streaming.Markers.DetectedMarker(MarkerType.Credits, 2600, 2640, "fingerprint");
        var fitted = Assert.Single(MarkerDetectionWorker.FittedTo([credits], 2640, 2642.64));
        Assert.Equal(2602.6, fitted.Start, 3);
        Assert.Equal(2642.64, fitted.End, 3);
    }

    [Fact]
    public void A_lone_version_is_compared_with_another_episode()
    {
        MediaFile Episode(Guid item) => new() { MediaItemId = item, Path = "/x.mkv" };
        var (one, two) = (Guid.NewGuid(), Guid.NewGuid());
        var (first, second) = (Episode(one), Episode(two));
        var extended = Episode(two);

        Assert.Equal([extended, first], MarkerDetectionWorker.WithPartner([extended], [first, second]));
        Assert.Equal([extended], MarkerDetectionWorker.WithPartner([extended], [second]));
        Assert.Equal([extended, first], MarkerDetectionWorker.WithPartner([extended, first], [second]));
    }

    [Fact]
    public void A_season_asked_for_again_while_it_waits_is_queued_once()
    {
        var queue = new MarkerQueue();
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());

        queue.Prioritize(first);
        queue.Prioritize(second);
        queue.Prioritize(first);

        Assert.True(queue.TryNext(out var a));
        Assert.True(queue.TryNext(out var b));
        Assert.False(queue.TryNext(out _));
        Assert.Equal([first, second], [a, b]);

        // Once it's been taken, asking again queues it again (the re-analyse button, say).
        queue.Prioritize(first);
        Assert.True(queue.TryNext(out var again));
        Assert.Equal(first, again);
    }

    [Fact]
    public async Task A_season_needs_analysing_only_while_a_readable_file_of_it_is_unscanned()
    {
        await using var factory = new FlexarrApiFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = "Shows", Type = LibraryType.TvShows };
        var show = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Series, Title = "Show", SortTitle = "show" };
        var season = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Season, Title = "Season 1", SortTitle = "season 1", ParentId = show.Id };
        var scanned = new MediaFile { Path = "/tv/show/s1e1.mkv", Size = 1, MarkersScannedAt = DateTime.UtcNow };
        var unreadable = new MediaFile { Path = "/tv/show/s1e2.mkv", Size = 1, ProbeError = "bad" };
        var waiting = new MediaFile { Path = "/tv/show/s1e3.mkv", Size = 1 };
        MediaItem Episode(int n, MediaFile file) => new()
        {
            LibraryId = library.Id, Kind = MediaKind.Episode, Title = $"Episode {n}", SortTitle = $"episode {n}", ParentId = season.Id, Files = [file],
        };
        db.Libraries.Add(library);
        db.MediaItems.AddRange(show, season, Episode(1, scanned), Episode(2, unreadable), Episode(3, waiting));
        await db.SaveChangesAsync();

        Assert.True(await MarkerDetectionWorker.NeedsAnalysisAsync(db, season.Id, CancellationToken.None));

        waiting.MarkersScannedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        Assert.False(await MarkerDetectionWorker.NeedsAnalysisAsync(db, season.Id, CancellationToken.None));
    }
}
