using System.Net;
using System.Text;
using Flexarr.Api.Endpoints;
using Flexarr.Api.Endpoints.MusicRequests;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;

namespace Flexarr.Api.Tests;

public class MusicMatcherTests
{
    private static readonly ArrInstance Lidarr = new() { Name = "Lidarr", Kind = ArrKind.Lidarr, BaseUrl = new Uri("http://lidarr.test"), ApiKey = "k" };

    private static ArrClient Answering(Dictionary<string, string> answers) => new(new HttpClient(new Canned(answers)));

    [Fact]
    public async Task OneArtistOfTheName_IsTheMatch_WhateverTheCase()
    {
        var client = Answering(new() { ["artist/lookup"] = """[{"id":0,"artistName":"WET LEG","foreignArtistId":"wl"},{"id":0,"artistName":"Wet Legs","foreignArtistId":"other"}]""" });

        var artist = await MusicMatcher.MatchArtistAsync(client, Lidarr, "Wet Leg", [], default);

        Assert.Equal("wl", artist?.ForeignArtistId);
    }

    [Fact]
    public async Task SameNamedArtists_PreferTheOneLidarrHas()
    {
        var client = Answering(new() { ["artist/lookup"] = """[{"id":0,"artistName":"Nirvana","foreignArtistId":"uk"},{"id":5,"artistName":"Nirvana","foreignArtistId":"us"}]""" });

        Assert.Equal("us", (await MusicMatcher.MatchArtistAsync(client, Lidarr, "Nirvana", [], default))?.ForeignArtistId);
    }

    [Fact]
    public async Task SameNamedArtists_AreToldApartByAnAlbum_OrNotMatchedAtAll()
    {
        var client = Answering(new()
        {
            ["artist/lookup"] = """[{"id":0,"artistName":"Nirvana","foreignArtistId":"uk"},{"id":0,"artistName":"Nirvana","foreignArtistId":"us"}]""",
            ["album/lookup"] = """[{"title":"Nevermind (Deluxe Edition)","foreignAlbumId":"nm","artist":{"artistName":"Nirvana","foreignArtistId":"us"}}]""",
        });

        Assert.Equal("us", (await MusicMatcher.MatchArtistAsync(client, Lidarr, "Nirvana", ["Nevermind"], default))?.ForeignArtistId);
        Assert.Null(await MusicMatcher.MatchArtistAsync(client, Lidarr, "Nirvana", ["Bleach"], default));
    }

    [Fact]
    public async Task AnAlbum_IsMatchedAmongLidarrsOwn_ThenItsLookup_NearestInYear()
    {
        var client = Answering(new()
        {
            ["album"] = """[{"id":1,"title":"Rumours","foreignAlbumId":"old","releaseDate":"1977-02-04T00:00:00Z"},{"id":2,"title":"Tusk","foreignAlbumId":"tusk"}]""",
            ["album/lookup"] = """[{"title":"Rumours Live","foreignAlbumId":"live","artist":{"artistName":"Fleetwood Mac","foreignArtistId":"fm"}},{"title":"Say You Will","foreignAlbumId":"syw","artist":{"artistName":"Fleetwood Mac","foreignArtistId":"fm"}},{"title":"Say You Will","foreignAlbumId":"tribute","artist":{"artistName":"Tribute Band","foreignArtistId":"tb"}}]""",
        });
        var artist = new LidarrArtist { Id = 3, ArtistName = "Fleetwood Mac", ForeignArtistId = "fm" };

        Assert.Equal("old", (await MusicMatcher.MatchAlbumAsync(client, Lidarr, artist, "Rumours (Super Deluxe)", 2013, default))?.ForeignAlbumId);
        Assert.Equal("syw", (await MusicMatcher.MatchAlbumAsync(client, Lidarr, artist, "Say You Will", 2003, default))?.ForeignAlbumId);
        Assert.Null(await MusicMatcher.MatchAlbumAsync(client, Lidarr, artist, "Mirage", 1982, default));
    }

    [Fact]
    public void Availability_ReadsLibraryRequestsAndFollows_ByNormalisedNames()
    {
        var owned = new OwnedMusic();
        var beatles = Guid.NewGuid();
        owned.Artists["beatles"] = beatles;
        owned.Albums[OwnedMusic.AlbumKey("The Beatles", "Abbey Road")] = Guid.NewGuid();
        owned.RequestedAlbums.Add(OwnedMusic.AlbumKey("Wet Leg", "moisturizer"));
        owned.FollowedArtists.Add("wet leg");

        Assert.Equal(beatles, owned.ArtistId("The Beatles"));
        Assert.Equal(AvailabilityDto.Available, owned.AlbumAvailability("Beatles", "Abbey Road (Super Deluxe Edition)"));
        Assert.Equal(AvailabilityDto.Requested, owned.AlbumAvailability("Wet Leg", "moisturizer"));
        Assert.Equal(AvailabilityDto.None, owned.AlbumAvailability("Wet Leg", "Wet Leg"));
        Assert.Equal(AvailabilityDto.Requested, owned.ArtistAvailability("Wet Leg"));
        Assert.Equal(AvailabilityDto.Available, owned.ArtistAvailability("the beatles"));
    }

    /// <summary>Answers by the path after /api/v1/, query aside.</summary>
    private sealed class Canned(Dictionary<string, string> answers) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath["/api/v1/".Length..];
            return Task.FromResult(answers.TryGetValue(path, out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
