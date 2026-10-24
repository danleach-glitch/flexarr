using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Webhooks;

namespace Flexarr.Core.Tests;

public class ArrWebhookTranslatorTests
{
    [Fact]
    public void SonarrDownload_ScansSeriesFolder()
    {
        var payload = new ArrWebhookPayload
        {
            EventType = "Download",
            Series = new("Severance", "/tv/Severance"),
            EpisodeFile = new("/tv/Severance/Season 02/Severance - S02E01.mkv", "Season 02/Severance - S02E01.mkv"),
        };

        var request = Assert.Single(ArrWebhookTranslator.ToScanRequests(ArrKind.Sonarr, payload));

        Assert.Equal("/tv/Severance", request.Path);
        Assert.Equal("Sonarr Download", request.Reason);
    }

    [Fact]
    public void RadarrMovieDelete_ScansMovieFolder()
    {
        var payload = new ArrWebhookPayload { EventType = "MovieDelete", Movie = new("Dune", "/movies/Dune (2021)") };

        var request = Assert.Single(ArrWebhookTranslator.ToScanRequests(ArrKind.Radarr, payload));

        Assert.Equal("/movies/Dune (2021)", request.Path);
    }

    [Fact]
    public void LidarrAlbumDownload_ScansArtistFolder()
    {
        var payload = new ArrWebhookPayload { EventType = "AlbumDownload", Artist = new("Radiohead", "/music/Radiohead") };

        var request = Assert.Single(ArrWebhookTranslator.ToScanRequests(ArrKind.Lidarr, payload));

        Assert.Equal("/music/Radiohead", request.Path);
    }

    [Theory]
    [InlineData("Grab")]
    [InlineData("Health")]
    [InlineData("Test")]
    public void NonLibraryEvents_ProduceNoScans(string eventType)
    {
        var payload = new ArrWebhookPayload { EventType = eventType, Series = new("Severance", "/tv/Severance") };

        Assert.Empty(ArrWebhookTranslator.ToScanRequests(ArrKind.Sonarr, payload));
    }
}
