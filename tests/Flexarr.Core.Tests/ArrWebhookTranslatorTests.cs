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

    [Theory]
    [InlineData(ArrKind.Radarr, "MovieFileDelete", "Upgrade", false)]
    [InlineData(ArrKind.Sonarr, "EpisodeFileDelete", "upgrade", false)]
    [InlineData(ArrKind.Radarr, "MovieFileDelete", "Manual", true)]
    [InlineData(ArrKind.Sonarr, "EpisodeFileDelete", "missingFromDisk", true)]
    [InlineData(ArrKind.Sonarr, "EpisodeFileDelete", null, true)]
    public void FileDelete_ForAnUpgrade_IsIgnored(ArrKind source, string eventType, string? reason, bool scans)
    {
        var payload = new ArrWebhookPayload
        {
            EventType = eventType,
            DeleteReason = reason,
            Series = new("Severance", "/tv/Severance"),
            Movie = new("Dune", "/movies/Dune (2021)"),
        };

        Assert.Equal(scans, ArrWebhookTranslator.ToScanRequests(source, payload).Count == 1);
    }

    [Fact]
    public void Requests_CarryTheTitlesTmdbOrTvdbId()
    {
        var options = System.Text.Json.JsonSerializerOptions.Web;
        var radarr = System.Text.Json.JsonSerializer.Deserialize<ArrWebhookPayload>(
            """{"eventType":"Download","movie":{"id":3,"title":"Arrival","folderPath":"/movies/Arrival (2016)","tmdbId":329865,"imdbId":"tt2543164"}}""", options)!;
        var sonarr = System.Text.Json.JsonSerializer.Deserialize<ArrWebhookPayload>(
            """{"eventType":"Download","series":{"id":5,"title":"Severance","path":"/tv/Severance","tvdbId":371980,"tmdbId":95396}}""", options)!;

        var film = Assert.Single(ArrWebhookTranslator.ToScanRequests(ArrKind.Radarr, radarr));
        var show = Assert.Single(ArrWebhookTranslator.ToScanRequests(ArrKind.Sonarr, sonarr));

        Assert.Equal((329865, (int?)null), (film.TmdbId, film.TvdbId));
        Assert.Equal(((int?)null, 371980), (show.TmdbId, show.TvdbId));
    }

    [Fact]
    public void Payload_ReadsDeleteReason()
    {
        var payload = System.Text.Json.JsonSerializer.Deserialize<ArrWebhookPayload>(
            """{"eventType":"MovieFileDelete","deleteReason":"upgrade","movie":{"title":"Dune","folderPath":"/movies/Dune (2021)"}}""",
            System.Text.Json.JsonSerializerOptions.Web);

        Assert.Equal("upgrade", payload!.DeleteReason);
        Assert.Empty(ArrWebhookTranslator.ToScanRequests(ArrKind.Radarr, payload));
    }

    [Fact]
    public void Paths_AreComposedUnicode()
    {
        var payload = new ArrWebhookPayload { EventType = "Download", Series = new("Léon", "/tv/Léon") };

        var request = Assert.Single(ArrWebhookTranslator.ToScanRequests(ArrKind.Sonarr, payload));

        Assert.Equal("/tv/Léon", request.Path);
    }

    [Fact]
    public void LidarrAlbumDownload_ScansArtistFolder()
    {
        var payload = new ArrWebhookPayload { EventType = "AlbumDownload", Artist = new("Radiohead", "/music/Radiohead") };

        var request = Assert.Single(ArrWebhookTranslator.ToScanRequests(ArrKind.Lidarr, payload));

        Assert.Equal("/music/Radiohead", request.Path);
    }

    [Fact]
    public void LidarrRetag_ScansArtistFolder()
    {
        // Lidarr names its On Track Retag event "Retag".
        var payload = new ArrWebhookPayload { EventType = "Retag", Artist = new("Radiohead", "/music/Radiohead") };

        var request = Assert.Single(ArrWebhookTranslator.ToScanRequests(ArrKind.Lidarr, payload));

        Assert.Equal(("/music/Radiohead", "Lidarr Retag"), (request.Path, request.Reason));
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
