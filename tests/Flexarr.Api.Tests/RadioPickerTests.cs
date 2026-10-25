using Flexarr.Api.Endpoints;

namespace Flexarr.Api.Tests;

/// <summary>The rules behind artist radio's track picker, with no server.</summary>
public class RadioPickerTests
{
    private static readonly Guid Seed = Guid.NewGuid();

    private static List<RadioCandidate> Tracks(Guid artist, int count, int sharedGenres = 0, int artistPlays = 0, int trackPlays = 0) =>
        Enumerable.Range(0, count).Select(_ => new RadioCandidate(Guid.NewGuid(), artist, sharedGenres, artistPlays, trackPlays)).ToList();

    [Fact]
    public void TheSameSeed_GivesTheSameStation_AndAnotherSeedADifferentOne()
    {
        var related = Guid.NewGuid();
        List<RadioCandidate> candidates = [.. Tracks(Seed, 40), .. Tracks(related, 200, sharedGenres: 1)];

        var first = MusicDiscovery.PickRadio(Seed, candidates, seed: 42);
        var again = MusicDiscovery.PickRadio(Seed, candidates, seed: 42);
        var other = MusicDiscovery.PickRadio(Seed, candidates, seed: 43);

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void NoTrackTwice_FiftyAtMost_AndTheSeedArtistOpens()
    {
        var related = Guid.NewGuid();
        // The same track offered twice (an artist tagged on both their own and their album's genres) still plays once.
        var duplicated = new RadioCandidate(Guid.NewGuid(), related, 1, 0, 0);
        List<RadioCandidate> candidates = [.. Tracks(Seed, 40), .. Tracks(related, 200, sharedGenres: 1), duplicated, duplicated];
        var own = candidates.Where(c => c.ArtistId == Seed).Select(c => c.TrackId).ToHashSet();

        var station = MusicDiscovery.PickRadio(Seed, candidates, seed: 1);

        Assert.Equal(MusicDiscovery.RadioLength, station.Count);
        Assert.Equal(station.Count, station.Distinct().Count());
        Assert.Contains(station[0], own);
    }

    [Fact]
    public void AboutTwoFifths_IsTheSeedArtistsOwn_WhenTheyHaveEnough()
    {
        var related = Guid.NewGuid();
        List<RadioCandidate> candidates = [.. Tracks(Seed, 100), .. Tracks(related, 100, sharedGenres: 1)];

        var station = MusicDiscovery.PickRadio(Seed, candidates, seed: 5);
        var own = candidates.Where(c => c.ArtistId == Seed).Select(c => c.TrackId).ToHashSet();

        Assert.Equal(20, station.Count(own.Contains));
        Assert.Equal(30, station.Count(t => !own.Contains(t)));
    }

    [Fact]
    public void WithNobodyRelated_TheStationIsAllTheirOwn_AndWithFewOfTheirOwn_TheRestFillIn()
    {
        var related = Guid.NewGuid();

        var alone = MusicDiscovery.PickRadio(Seed, Tracks(Seed, 100), seed: 2);
        Assert.Equal(50, alone.Count);

        List<RadioCandidate> fewOwn = [.. Tracks(Seed, 5), .. Tracks(related, 100, sharedGenres: 1)];
        var station = MusicDiscovery.PickRadio(Seed, fewOwn, seed: 2);
        Assert.Equal(50, station.Count);
        Assert.Equal(5, station.Count(t => fewOwn.Any(c => c.TrackId == t && c.ArtistId == Seed)));

        // Too few related tracks to make up the other three fifths: the seed artist's own make up the difference.
        List<RadioCandidate> fewRelated = [.. Tracks(Seed, 100), .. Tracks(related, 10, sharedGenres: 1)];
        var mostlyOwn = MusicDiscovery.PickRadio(Seed, fewRelated, seed: 2);
        Assert.Equal(50, mostlyOwn.Count);
        Assert.Equal(10, mostlyOwn.Count(t => fewRelated.Any(c => c.TrackId == t && c.ArtistId == related)));
    }

    [Fact]
    public void ArtistsYouPlay_AndArtistsCloserInGenre_GetMoreOfTheStation()
    {
        var played = Guid.NewGuid();
        var unplayed = Guid.NewGuid();
        var closer = Guid.NewGuid();
        List<RadioCandidate> byPlays = [.. Tracks(Seed, 10), .. Tracks(played, 100, sharedGenres: 1, artistPlays: 200), .. Tracks(unplayed, 100, sharedGenres: 1)];
        List<RadioCandidate> byGenre = [.. Tracks(Seed, 10), .. Tracks(closer, 100, sharedGenres: 4), .. Tracks(unplayed, 100, sharedGenres: 1)];

        // Forty of each station come from the related pair; over a few seeds the favoured artist always has the majority.
        foreach (var seed in Enumerable.Range(1, 5))
        {
            var station = MusicDiscovery.PickRadio(Seed, byPlays, seed);
            var fromPlayed = station.Count(t => byPlays.Any(c => c.TrackId == t && c.ArtistId == played));
            var fromUnplayed = station.Count(t => byPlays.Any(c => c.TrackId == t && c.ArtistId == unplayed));
            Assert.True(fromPlayed > fromUnplayed, $"seed {seed}: {fromPlayed} played vs {fromUnplayed} unplayed");

            station = MusicDiscovery.PickRadio(Seed, byGenre, seed);
            var fromCloser = station.Count(t => byGenre.Any(c => c.TrackId == t && c.ArtistId == closer));
            var fromFurther = station.Count(t => byGenre.Any(c => c.TrackId == t && c.ArtistId == unplayed));
            Assert.True(fromCloser > fromFurther, $"seed {seed}: {fromCloser} closer vs {fromFurther} further");
        }
    }

    [Fact]
    public void NothingToPlay_IsAnEmptyStation()
    {
        Assert.Empty(MusicDiscovery.PickRadio(Seed, [], seed: 1));
    }
}
