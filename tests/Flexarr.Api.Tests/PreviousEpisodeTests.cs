using Flexarr.Api.Playback;

namespace Flexarr.Api.Tests;

/// <summary>The player's Previous Episode: the mirror of Next.</summary>
public class PreviousEpisodeTests
{
    private static readonly Guid Series = Guid.NewGuid();
    private static readonly Guid Season1 = Guid.NewGuid();
    private static readonly Guid Season2 = Guid.NewGuid();
    private static readonly Guid Specials = Guid.NewGuid();

    private static PlaybackService.EpisodeRef Episode(Guid season, int seasonNumber, int number, int? end = null, DateOnly? aired = null, string? title = null) =>
        new(Guid.NewGuid(), Series, season, seasonNumber, number, end, aired, title ?? $"S{seasonNumber:00}E{number:00}", null);

    [Fact]
    public void Previous_IsTheOneBeforeInTheSeason_ThenTheLastOfTheSeasonBefore_AndNothingBeforeTheFirst()
    {
        var s1e1 = Episode(Season1, 1, 1);
        var s1e2 = Episode(Season1, 1, 2);
        var s1e3 = Episode(Season1, 1, 3);
        var special = Episode(Specials, 0, 1);
        var s2e1 = Episode(Season2, 2, 1);
        var episodes = new[] { s2e1, special, s1e3, s1e1, s1e2 };

        Assert.Equal(s1e2.Id, PlaybackService.PreviousBefore(episodes, s1e3)!.Id);
        Assert.Equal(s1e3.Id, PlaybackService.PreviousBefore(episodes, s2e1)!.Id);
        Assert.Null(PlaybackService.PreviousBefore(episodes, s1e1));
    }

    [Fact]
    public void ADoubleEpisode_CountsAsOne_InBothDirections()
    {
        var s1e1 = Episode(Season1, 1, 1);
        var s1e2e3 = Episode(Season1, 1, 2, end: 3);
        var s1e4 = Episode(Season1, 1, 4);
        var episodes = new[] { s1e1, s1e2e3, s1e4 };

        Assert.Equal(s1e2e3.Id, PlaybackService.PreviousBefore(episodes, s1e4)!.Id);
        Assert.Equal(s1e1.Id, PlaybackService.PreviousBefore(episodes, s1e2e3)!.Id);
        Assert.Equal(s1e4.Id, PlaybackService.NextAfter(episodes, s1e2e3)!.Id);
    }

    [Fact]
    public void DailyEpisodes_GoByAirDate_ThenTitle()
    {
        var monday = Episode(Season1, 1, 0, aired: new DateOnly(2026, 3, 2), title: "Monday");
        var tuesdayA = Episode(Season1, 1, 0, aired: new DateOnly(2026, 3, 3), title: "A");
        var tuesdayB = Episode(Season1, 1, 0, aired: new DateOnly(2026, 3, 3), title: "B");
        var episodes = new[] { tuesdayB, monday, tuesdayA };

        Assert.Equal(tuesdayA.Id, PlaybackService.PreviousBefore(episodes, tuesdayB)!.Id);
        Assert.Equal(monday.Id, PlaybackService.PreviousBefore(episodes, tuesdayA)!.Id);
        Assert.Null(PlaybackService.PreviousBefore(episodes, monday));
    }
}
