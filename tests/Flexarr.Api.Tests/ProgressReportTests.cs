using System.Net.Http.Json;
using Flexarr.Api.Playback;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>
/// What a player's progress reports do to the watched state: the report a player sends as it closes after an ended one,
/// a scrub that overshoots into the credits, and reports that change nothing.
/// </summary>
public class ProgressReportTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private HttpClient Admin()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    /// <summary>An episode of 10 minutes, with its credits at 9 when <paramref name="credits"/>, playing (no stream behind it).</summary>
    private async Task<(Guid Item, string Session)> PlayingAsync(bool credits = true, double duration = 600, string? remoteUrl = null, PlaybackState? state = null)
    {
        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var library = new Library { Name = $"Shows {Guid.NewGuid():N}", Type = LibraryType.TvShows };
            var episode = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Episode, Title = "Pilot", SortTitle = "pilot" };
            db.AddRange(library, episode);
            if (state is not null)
            {
                state.MediaItemId = episode.Id;
                db.PlaybackStates.Add(state);
            }

            await db.SaveChangesAsync();
            id = episode.Id;
        }

        var session = new PlaybackSession
        {
            Id = Guid.NewGuid().ToString("N"),
            ItemId = id,
            ProfileId = PlaybackState.DefaultUser,
            FileId = Guid.NewGuid(),
            FilePath = "",
            RemoteUrl = remoteUrl,
            Container = null,
            DurationSeconds = duration,
            Method = PlaybackMethod.DirectPlay,
            Subtitles = [],
            CreditsStart = credits ? 540 : null,
        };
        factory.Services.GetRequiredService<PlaybackSessions>().Add(session);
        return (id, session.Id);
    }

    private async Task ReportAsync(string session, double position, bool paused = false, bool ended = false) =>
        (await Admin().PostAsJsonAsync(Api($"/playback/{session}/progress"), new { positionSeconds = position, paused, ended })).EnsureSuccessStatusCode();

    private async Task<PlaybackState> StateAsync(Guid item)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        return await db.PlaybackStates.AsNoTracking().SingleAsync(p => p.MediaItemId == item && p.UserId == PlaybackState.DefaultUser);
    }

    [Fact]
    public async Task TheReportAPlayerSendsAsItClosesAfterTheEnd_LeavesItWatched()
    {
        // "Play now" on Next Episode at 7 minutes, before the credits and the watched point; and a podcast whose feed
        // gives no length, heard to the end.
        var (episode, session) = await PlayingAsync();
        await ReportAsync(session, 200);
        await ReportAsync(session, 420, paused: true, ended: true);
        await ReportAsync(session, 420, paused: true);

        var state = await StateAsync(episode);
        Assert.True(state.Watched);
        Assert.Equal((0, 1), (state.PositionSeconds, state.PlayCount));

        var (podcast, heard) = await PlayingAsync(credits: false, duration: 0, remoteUrl: "https://example.com/episode.mp3");
        await ReportAsync(heard, 1795);
        await ReportAsync(heard, 1800, ended: true);
        await ReportAsync(heard, 1800, paused: true);
        Assert.True((await StateAsync(podcast)).Watched);
    }

    [Fact]
    public async Task GoingClearlyBackAfterTheEnd_IsWatchingAgain()
    {
        var (episode, session) = await PlayingAsync();
        await ReportAsync(session, 420, ended: true);
        await ReportAsync(session, 100);

        var state = await StateAsync(episode);
        Assert.False(state.Watched);
        Assert.Equal(100, state.PositionSeconds);
    }

    [Fact]
    public async Task AScrubThatOvershootsIntoTheCredits_AndComesBack_CountsNoPlay()
    {
        var (episode, session) = await PlayingAsync();
        await ReportAsync(session, 20);
        await ReportAsync(session, 560); // overshot
        await ReportAsync(session, 300); // and back

        var state = await StateAsync(episode);
        Assert.False(state.Watched);
        Assert.Equal((300, 0), (state.PositionSeconds, state.PlayCount));
    }

    [Fact]
    public async Task AScrubIntoTheCredits_ThatStaysThere_IsWatched()
    {
        var (episode, session) = await PlayingAsync();
        await ReportAsync(session, 20);
        await ReportAsync(session, 560);
        Assert.False((await StateAsync(episode)).Watched);

        await ReportAsync(session, 565);
        var state = await StateAsync(episode);
        Assert.True(state.Watched);
        Assert.Equal(1, state.PlayCount);
    }
}
