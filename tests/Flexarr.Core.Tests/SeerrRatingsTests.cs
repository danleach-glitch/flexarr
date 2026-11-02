using System.Text.Json;
using Flexarr.Integrations.Arr;

namespace Flexarr.Core.Tests;

/// <summary>Seerr's answers about a title's scores, as Discover and the metadata refresh read them.</summary>
public sealed class SeerrRatingsTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void AMovie_HasRottenTomatoesAndImdb()
    {
        var r = SeerrRatings.Parse(Json("""{"rt":{"criticsRating":"Certified Fresh","criticsScore":92.4,"audienceScore":88},"imdb":{"criticsScore":7.84}}"""), movie: true);

        Assert.Equal(new SeerrRatings(92, true, 88, 7.8), r);
    }

    [Fact]
    public void AShow_IsTheRottenTomatoesObjectItself()
    {
        var r = SeerrRatings.Parse(Json("""{"criticsRating":"Rotten","criticsScore":45,"audienceScore":61}"""), movie: false);

        Assert.Equal(new SeerrRatings(45, false, 61, null), r);
    }

    [Theory]
    [InlineData("""{"imdb":{"criticsScore":6.5}}""", null, 6.5)]
    [InlineData("""{"rt":{"criticsScore":70},"imdb":null}""", 70, null)]
    public void MissingParts_AreNull(string json, int? critics, double? imdb)
    {
        var r = SeerrRatings.Parse(Json(json), movie: true);

        Assert.Equal((critics, imdb, false), (r?.CriticScore, r?.Imdb, r?.Certified ?? false));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"rt":"nope","imdb":{"criticsScore":"8"}}""")]
    [InlineData("""{"rt":{"criticsScore":140}}""")]
    public void NothingUsable_IsNull(string json) => Assert.Null(SeerrRatings.Parse(Json(json), movie: true));

    [Fact]
    public void NoAnswer_IsNull() => Assert.Null(SeerrRatings.Parse(null, movie: true));
}
