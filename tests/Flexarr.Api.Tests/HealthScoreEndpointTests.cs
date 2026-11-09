using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>Library health's score alone, kept between visits to Settings (audit perf-60).</summary>
public sealed class HealthScoreEndpointTests : IDisposable
{
    private readonly FlexarrApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    private async Task<Guid> AddFilmAsync(string title, int files)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = await db.Libraries.FirstOrDefaultAsync() ?? new Library { Name = "Films", Type = LibraryType.Movies };
        if (db.Entry(library).State == EntityState.Detached)
        {
            db.Libraries.Add(library);
        }

        var film = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Movie, Title = title, SortTitle = SortTitle.For(title), PosterUrl = "poster.jpg" };
        for (var i = 0; i < files; i++)
        {
            film.Files.Add(new MediaFile { Path = $"/films/{title}/{i}.mkv", Size = 1000 + i, Width = 1920, Height = 1080 });
        }

        db.MediaItems.Add(film);
        await db.SaveChangesAsync();
        return library.Id;
    }

    private async Task ScanFinishedAsync(Guid library)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        await db.Libraries.Where(l => l.Id == library).ExecuteUpdateAsync(u => u.SetProperty(l => l.LastScannedAt, DateTime.UtcNow.AddSeconds(1)));
    }

    private async Task<JsonElement> ScoreAsync() => await Client().GetFromJsonAsync<JsonElement>(new Uri("/api/v1/library-health/score", UriKind.Relative));

    private async Task<JsonElement> ReportAsync() => await Client().GetFromJsonAsync<JsonElement>(new Uri("/api/v1/library-health", UriKind.Relative));

    [Fact]
    public async Task The_score_is_the_reports_own_and_the_report_is_as_it_was()
    {
        await AddFilmAsync("Heat", 1);
        await AddFilmAsync("Alien", 2);

        var report = await ReportAsync();
        var score = await ScoreAsync();

        Assert.Equal(report.GetProperty("score").GetRawText(), score.GetRawText());
        Assert.Equal(50, score.GetProperty("score").GetInt32());
        Assert.Equal(1, score.GetProperty("duplicates").GetInt32());
        // The report keeps every part it had.
        Assert.Equal(1, report.GetProperty("duplicates").GetArrayLength());
        Assert.True(report.TryGetProperty("held", out _));
    }

    [Fact]
    public async Task The_score_is_kept_until_a_scan_finishes_or_the_report_is_built()
    {
        var library = await AddFilmAsync("Heat", 1);
        Assert.Equal(100, (await ScoreAsync()).GetProperty("score").GetInt32());

        // Changed without a scan: the kept score stands.
        await AddFilmAsync("Alien", 2);
        Assert.Equal(100, (await ScoreAsync()).GetProperty("score").GetInt32());

        // A scan finished: worked out afresh.
        await ScanFinishedAsync(library);
        Assert.Equal(50, (await ScoreAsync()).GetProperty("score").GetInt32());

        // The full report (Library health, after a fix) replaces what's kept.
        await AddFilmAsync("Brazil", 1);
        Assert.Equal(50, (await ScoreAsync()).GetProperty("score").GetInt32());
        Assert.Equal(66, (await ReportAsync()).GetProperty("score").GetProperty("score").GetInt32());
        Assert.Equal(66, (await ScoreAsync()).GetProperty("score").GetInt32());
    }

    [Fact]
    public async Task The_score_is_for_admins_only()
    {
        var created = await (await Client().PostAsJsonAsync(new Uri("/api/v1/profiles", UriKind.Relative), new { name = "Claire" })).Content.ReadFromJsonAsync<JsonElement>();
        var token = (await (await Client().PostAsJsonAsync(new Uri($"/api/v1/profiles/{created.GetProperty("id").GetString()}/select", UriKind.Relative), new { }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var other = Client();
        other.DefaultRequestHeaders.Add("X-Flexarr-Profile", token);

        Assert.Equal((await other.GetAsync(new Uri("/api/v1/library-health", UriKind.Relative))).StatusCode,
            (await other.GetAsync(new Uri("/api/v1/library-health/score", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task AVersion_IsNotASpare_AndALeftoverOfADifferentResolutionIsKept()
    {
        Guid film;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var library = new Library { Name = "Films", Type = LibraryType.Movies };
            var movie = new MediaItem
            {
                LibraryId = library.Id, Kind = MediaKind.Movie, Title = "Film", SortTitle = "film", PosterUrl = "poster.jpg", ArrId = 7, MetadataSource = "Radarr",
                Files =
                [
                    new MediaFile { Path = "/films/Film/Film 2160p.mkv", Size = 4000, Width = 3840, Height = 2160, Bitrate = 40 },
                    new MediaFile { Path = "/films/Film/Film 720p.mkv", Size = 700, Width = 1280, Height = 720, Bitrate = 7 },
                ],
            };
            db.AddRange(library, movie);
            await db.SaveChangesAsync();
            film = movie.Id;
        }

        var health = await Client().GetFromJsonAsync<JsonElement>(new Uri("/api/v1/library-health", UriKind.Relative));
        Assert.Empty(health.GetProperty("duplicates").EnumerateArray());
        Assert.Equal(0, health.GetProperty("reclaimableBytes").GetInt64());
        Assert.Equal(100, health.GetProperty("score").GetProperty("score").GetInt32());
        Assert.Equal(0, health.GetProperty("score").GetProperty("duplicates").GetInt32());
        var versions = Assert.Single(health.GetProperty("versions").EnumerateArray());
        Assert.Equal(film, versions.GetProperty("item").GetProperty("id").GetGuid());
        Assert.Equal(["4K", "720p"], versions.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("label").GetString()));
        Assert.Equal(4700, health.GetProperty("versionsBytes").GetInt64());

        // Remove all leaves it alone, without needing to ask Radarr.
        var removed = await Client().PostAsync(new Uri($"/api/v1/items/{film}/remove-duplicates", UriKind.Relative), null);
        removed.EnsureSuccessStatusCode();
        Assert.Equal(0, (await removed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("removed").GetInt32());
    }

    [Fact]
    public async Task ATrueDuplicate_IsASpare_BesideAVersion()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var library = new Library { Name = "Films", Type = LibraryType.Movies };
            db.AddRange(library, new MediaItem
            {
                LibraryId = library.Id, Kind = MediaKind.Movie, Title = "Film", SortTitle = "film", PosterUrl = "poster.jpg",
                Files =
                [
                    new MediaFile { Path = "/films/Film/a 2160p.mkv", Size = 4000, Width = 3840, Height = 2160, Bitrate = 40 },
                    new MediaFile { Path = "/films/Film/b 1080p.mkv", Size = 2000, Width = 1920, Height = 1080, Bitrate = 20 },
                    new MediaFile { Path = "/films/Film/c 1080p.mkv", Size = 1000, Width = 1920, Height = 1080, Bitrate = 10 },
                ],
            });
            await db.SaveChangesAsync();
        }

        var health = await Client().GetFromJsonAsync<JsonElement>(new Uri("/api/v1/library-health", UriKind.Relative));
        var duplicate = Assert.Single(health.GetProperty("duplicates").EnumerateArray());
        Assert.Equal(1000, duplicate.GetProperty("reclaimableBytes").GetInt64());
        // The two 1080p files; the 4K version has nothing to do with it.
        Assert.Equal(["/films/Film/b 1080p.mkv", "/films/Film/c 1080p.mkv"], duplicate.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()));
        Assert.Single(health.GetProperty("versions").EnumerateArray());
    }
}
