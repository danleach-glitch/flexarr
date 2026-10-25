using Flexarr.Api.Endpoints;

namespace Flexarr.Api.Tests;

/// <summary>How daily mixes are planned (clustered and named) and their tracks picked, with no server.</summary>
public class MixPlannerTests
{
    private static MixArtist Artist(string name, int plays, params string[] genres) => new(Guid.NewGuid(), name, plays, genres);

    [Fact]
    public void GenresLead_TheOnesYouPlayMostFirst_AndArtistsNoGenreGathers_GetAMixOfTheirOwn()
    {
        var direStraits = Artist("Dire Straits", 30, "Rock");
        var pinkFloyd = Artist("Pink Floyd", 20, "Rock", "Progressive Rock");
        var radiohead = Artist("Radiohead", 10, "Rock", "Alternative");
        var muse = Artist("Muse", 8, "Rock", "Alternative");
        var mozart = Artist("Mozart", 9, "Classical");
        var bach = Artist("Bach", 5, "Classical");
        var miles = Artist("Miles Davis", 4, "Jazz");
        var unplayed = Artist("Nobody", 0, "Rock");

        var plans = MusicMixes.Plan([miles, bach, unplayed, muse, mozart, radiohead, pinkFloyd, direStraits]);

        // Rock accounts for the most listening, then Alternative (half of Rock's artists: different enough), then
        // Classical. Progressive Rock and Jazz have one artist each, so Miles Davis is left for a mix of his own.
        Assert.Equal(["Rock Mix", "Alternative Mix", "Classical Mix", "Miles Davis Mix"], plans.Select(p => p.Name));
        Assert.Equal(["Dire Straits, Pink Floyd & more", "Radiohead & Muse", "Mozart & Bach", "Made from the artists you play"], plans.Select(p => p.Description));
        Assert.Equal([direStraits.Id, pinkFloyd.Id, radiohead.Id, muse.Id], plans[0].ArtistIds);
        Assert.Equal("Rock", plans[0].Genre);
        Assert.Null(plans[3].Genre);
        Assert.Equal([miles.Id], plans[3].ArtistIds);
        Assert.DoesNotContain(plans, p => p.ArtistIds.Contains(unplayed.Id));
    }

    [Fact]
    public void AGenreThatWouldMostlyRepeatAnEarlierMix_IsSkipped()
    {
        List<MixArtist> artists = [Artist("A", 10, "Rock", "Classic Rock"), Artist("B", 9, "Rock", "Classic Rock"), Artist("C", 8, "Rock", "Classic Rock"), Artist("D", 7, "Rock")];

        var plans = MusicMixes.Plan(artists);

        // Classic Rock has three of Rock's four artists: the same mix twice.
        Assert.Equal(["Rock Mix"], plans.Select(p => p.Name));
    }

    [Fact]
    public void ArtistsWithoutGenres_AreGatheredAFewAtATime_MostPlayedFirst_UpToFourMixes()
    {
        var artists = Enumerable.Range(1, 8).Select(n => Artist($"Artist {n}", 100 - n)).ToList();

        var plans = MusicMixes.Plan(artists);

        Assert.Equal(["Artist 1, Artist 2 & more", "Artist 7 & Artist 8"], plans.Select(p => p.Name));
        Assert.Equal(6, plans[0].ArtistIds.Count);

        var many = Enumerable.Range(1, 12).SelectMany(n => new[] { Artist($"A{n}", 20 - n, $"Genre {n}"), Artist($"B{n}", 20 - n, $"Genre {n}") }).ToList();
        Assert.Equal(MusicMixes.MaxMixes, MusicMixes.Plan(many).Count);
        Assert.Equal(["Genre 1 Mix", "Genre 2 Mix", "Genre 3 Mix", "Genre 4 Mix"], MusicMixes.Plan(many).Select(p => p.Name));
    }

    [Fact]
    public void AMixsId_ComesFromWhatItsBuiltFrom_SoItHoldsFromDayToDay()
    {
        List<MixArtist> artists = [Artist("A", 10, "Rock"), Artist("B", 9, "Rock"), Artist("C", 3)];

        var first = MusicMixes.Plan(artists);
        var again = MusicMixes.Plan(artists);

        Assert.Equal(first.Select(p => p.Id), again.Select(p => p.Id));
        Assert.Equal(2, first.Select(p => p.Id).Distinct().Count());
        Assert.NotEqual(first[0].Id, MusicMixes.Plan([Artist("A", 10, "Jazz"), Artist("B", 9, "Jazz")])[0].Id);
    }

    [Fact]
    public void NothingPlayed_MeansNoMixes()
    {
        Assert.Empty(MusicMixes.Plan([]));
        Assert.Empty(MusicMixes.Plan([Artist("A", 0, "Rock"), Artist("B", 0, "Rock")]));
    }

    [Fact]
    public void ListNames_ReadsNaturally_AndStopsAtThree()
    {
        Assert.Equal("", MusicMixes.ListNames([]));
        Assert.Equal("Pulp", MusicMixes.ListNames(["Pulp"]));
        Assert.Equal("Pulp & Blur", MusicMixes.ListNames(["Pulp", "Blur"]));
        Assert.Equal("Pulp, Blur & Oasis", MusicMixes.ListNames(["Pulp", "Blur", "Oasis"]));
        Assert.Equal("Pulp, Blur & more", MusicMixes.ListNames(["Pulp", "Blur", "Oasis", "Suede"]));
    }

    private static List<RadioCandidate> Tracks(Guid artist, int count, int artistPlays = 0, int trackPlays = 0) =>
        Enumerable.Range(0, count).Select(_ => new RadioCandidate(Guid.NewGuid(), artist, 0, artistPlays, trackPlays)).ToList();

    [Fact]
    public void TheSameSeed_GivesTheSameMix_WithNoTrackTwice_AndFortyAtMost()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var twice = new RadioCandidate(Guid.NewGuid(), b, 0, 0, 0);
        List<RadioCandidate> candidates = [.. Tracks(a, 60, artistPlays: 50), .. Tracks(b, 60), twice, twice];

        var mix = MusicMixes.Pick(candidates, seed: 9);

        Assert.Equal(MusicMixes.MixLength, mix.Count);
        Assert.Equal(mix.Count, mix.Distinct().Count());
        Assert.Equal(mix, MusicMixes.Pick(candidates, seed: 9));
        Assert.NotEqual(mix, MusicMixes.Pick(candidates, seed: 10));
        Assert.Contains(mix, t => candidates.Any(c => c.TrackId == t && c.ArtistId == a));
        Assert.Contains(mix, t => candidates.Any(c => c.TrackId == t && c.ArtistId == b));
    }

    [Fact]
    public void NoArtistTakesMoreThanTwiceTheirShare_HoweverMuchYouPlayThem()
    {
        var favourite = Guid.NewGuid();
        var others = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        List<RadioCandidate> candidates = [.. Tracks(favourite, 200, artistPlays: 1000, trackPlays: 20), .. others.SelectMany(o => Tracks(o, 10))];

        foreach (var seed in Enumerable.Range(1, 5))
        {
            var mix = MusicMixes.Pick(candidates, seed);
            Assert.Equal(40, mix.Count);
            // Four artists share forty tracks: a fair share is ten, so twenty is the most any one of them gets.
            Assert.Equal(20, mix.Count(t => candidates.Any(c => c.TrackId == t && c.ArtistId == favourite)));
        }
    }

    [Fact]
    public void FewerTracksThanAMix_AreAllOfThem_AndNoneIsAnEmptyMix()
    {
        var a = Guid.NewGuid();
        Assert.Equal(12, MusicMixes.Pick(Tracks(a, 12), seed: 1).Count);
        Assert.Empty(MusicMixes.Pick([], seed: 1));
    }
}
