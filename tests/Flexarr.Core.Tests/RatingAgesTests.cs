using Flexarr.Core.Media;

namespace Flexarr.Core.Tests;

/// <summary>Every country's ratings turned into the UK's age steps, for profiles with an age limit.</summary>
public class RatingAgesTests
{
    [Theory]
    // UK
    [InlineData("U", 0)]
    [InlineData("PG", 8)]
    [InlineData("12A", 12)]
    [InlineData("12", 12)]
    [InlineData("15", 15)]
    [InlineData("18", 18)]
    [InlineData("R18", 18)]
    // US films
    [InlineData("G", 0)]
    [InlineData("PG-13", 12)]
    [InlineData("R", 18)]
    [InlineData("NC-17", 18)]
    // US TV
    [InlineData("TV-Y", 0)]
    [InlineData("TV-G", 0)]
    [InlineData("TV-Y7", 8)]
    [InlineData("TV-PG", 8)]
    [InlineData("TV-14", 15)]
    [InlineData("TV-MA", 18)]
    // What the Mini's library holds besides
    [InlineData("16", 18)]
    [InlineData("-16", 18)]
    [InlineData("-12", 12)]
    [InlineData("16+", 18)]
    [InlineData("18+", 18)]
    [InlineData("12+", 12)]
    [InlineData("MA15+", 15)]
    [InlineData("R18+", 18)]
    [InlineData("M", 15)]
    // Written other ways
    [InlineData("tv-ma", 18)]
    [InlineData(" pg-13 ", 12)]
    [InlineData("Rated PG-13", 12)]
    [InlineData("UK:15", 15)]
    [InlineData("gb/12A", 12)]
    [InlineData("FSK 16", 18)]
    [InlineData("FSK6", 8)]
    [InlineData("L", 0)]
    [InlineData("10", 12)] // up to the next step
    [InlineData("13", 15)]
    [InlineData("Rated R for 2 scenes of violence", 18)] // the rating is the first word
    [InlineData("PG-13 for some language", 12)]
    [InlineData("7", 8)]
    [InlineData("Explicit", 18)]
    public void KnownRatings_GoToTheUkStepAtOrAboveThem(string rating, int expected) => Assert.Equal(expected, RatingAges.Of(rating));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NR")]
    [InlineData("Not Rated")]
    [InlineData("Unrated")]
    [InlineData("Approved")]
    [InlineData("Banned in Narnia")]
    [InlineData("123")]
    [InlineData("Not suitable for 2 year olds")]
    public void UnknownOrMissingRatings_AreUnrated(string? rating) => Assert.Null(RatingAges.Of(rating));

    [Fact]
    public void CleanPodcasts_AreForEveryone_ButUnratedFilmsAreNot()
    {
        Assert.Equal(0, RatingAges.For(MediaKind.Podcast, null));
        Assert.Equal(0, RatingAges.For(MediaKind.PodcastEpisode, null));
        Assert.Equal(18, RatingAges.For(MediaKind.PodcastEpisode, "Explicit"));
        Assert.Null(RatingAges.For(MediaKind.Movie, null));
        Assert.Null(RatingAges.For(MediaKind.Track, null));
    }

    [Fact]
    public void Labels_AreTheUkNames() =>
        Assert.Equal(["U", "PG", "12", "15", "18"], RatingAges.Steps.Select(RatingAges.Label));
}
