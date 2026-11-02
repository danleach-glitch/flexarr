using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Endpoints;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>IMDb and Rotten Tomatoes on movies and shows, as the apps read them.</summary>
public class ScoresTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    [Theory]
    [InlineData(92, true, RottenTomatoesState.CertifiedFresh)]
    [InlineData(92, false, RottenTomatoesState.Fresh)]
    [InlineData(60, false, RottenTomatoesState.Fresh)]
    [InlineData(59, false, RottenTomatoesState.Rotten)]
    [InlineData(40, true, RottenTomatoesState.Rotten)] // a stale certificate on a rotten score isn't shown
    public void TheTomato_FollowsTheScore(int score, bool certified, RottenTomatoesState state) =>
        Assert.Equal(new RottenTomatoesDto(score, state), RottenTomatoesDto.Of(score, certified));

    [Fact]
    public void NoScore_NoTomato() => Assert.Null(RottenTomatoesDto.Of(null, certified: true));

    [Fact]
    public async Task AMoviesPage_HasBothScores_AndOneWithoutThemHasNeither()
    {
        Guid rated, unrated;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var library = new Library { Name = $"Movies {Guid.NewGuid():N}", Type = LibraryType.Movies };
            var dune = new MediaItem
            {
                LibraryId = library.Id, Kind = MediaKind.Movie, Title = "Dune", SortTitle = "dune",
                CommunityRating = 8.0, ImdbRating = 8.1, CriticRating = 84, CriticCertified = true,
            };
            var home = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Movie, Title = "Holiday", SortTitle = "holiday", CommunityRating = 6.2 };
            db.AddRange(library, dune, home);
            await db.SaveChangesAsync();
            (rated, unrated) = (dune.Id, home.Id);
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);

        var item = (await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/items/{rated}", UriKind.Relative))).GetProperty("item");
        Assert.Equal(8.1, item.GetProperty("imdbRating").GetDouble());
        Assert.Equal(84, item.GetProperty("rottenTomatoes").GetProperty("score").GetInt32());
        Assert.Equal("CertifiedFresh", item.GetProperty("rottenTomatoes").GetProperty("state").GetString());
        Assert.Equal(84, item.GetProperty("criticRating").GetInt32()); // older apps still read this

        var plain = (await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/items/{unrated}", UriKind.Relative))).GetProperty("item");
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("imdbRating").ValueKind);
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("rottenTomatoes").ValueKind);
        Assert.Equal(6.2, plain.GetProperty("communityRating").GetDouble());
    }
}
