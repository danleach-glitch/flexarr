using System.Collections.Concurrent;
using System.Data.Common;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Books;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>
/// What starting playback answers for a film, an episode and a book, and how many statements it runs to get there:
/// the episode's trail, its next and previous episodes and its show's speed come from fewer round trips.
/// </summary>
public sealed class PlaybackStartQueryTests : IAsyncLifetime
{
    private readonly FlexarrApiFactory _factory = new();
    private readonly Recorder _sql = new();
    private WebApplicationFactory<Program> _server = null!;

    public Task InitializeAsync()
    {
        _server = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.ConfigureDbContext<FlexarrDbContext>(o => o.AddInterceptors(_sql))));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _factory.DisposeAsync();
    }

    private sealed class Recorder : DbCommandInterceptor
    {
        public ConcurrentQueue<string> Statements { get; } = new();

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Statements.Enqueue(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Statements.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private static readonly object Player = new { containers = new[] { "matroska,webm" }, videoCodecs = new[] { "h264" }, audioCodecs = new[] { "aac", "mp3" } };

    [Fact]
    public async Task AnEpisode_GetsItsTrailNeighboursAndSpeed_InFewerStatements()
    {
        var show = await SeedShowAsync();

        var (info, statements) = await StartAsync(show.S02E01);

        Assert.Equal(show.S02E01, Id(info.GetProperty("item")));
        Assert.Equal(new[] { show.Series, show.Season2 }, info.GetProperty("ancestors").EnumerateArray().Select(Id));
        Assert.Equal(new[] { "Series", "Season" }, info.GetProperty("ancestors").EnumerateArray().Select(a => a.GetProperty("kind").GetString()));
        Assert.Equal(show.S02E02, Id(info.GetProperty("next")));
        Assert.Equal(show.S01E02, Id(info.GetProperty("previous")));
        Assert.Equal(1.25, info.GetProperty("speed").GetDouble());
        Assert.Equal(120, info.GetProperty("startPositionSeconds").GetDouble());
        // The audio language last used anywhere in the show is chosen again.
        Assert.Equal(2, info.GetProperty("selectedAudioIndex").GetInt32());
        Assert.True(info.GetProperty("preferences").GetProperty("autoPlayNext").ValueKind is JsonValueKind.True or JsonValueKind.False);
        // 22 before the trail, the neighbours and the summaries were read together. One of these asks whether the show is
        // in another library too.
        Assert.InRange(statements, 1, 13);

        // The last episode has nothing after it; the first nothing before it.
        var (last, _) = await StartAsync(show.S02E02);
        Assert.Equal(JsonValueKind.Null, last.GetProperty("next").ValueKind);
        Assert.Equal(show.S02E01, Id(last.GetProperty("previous")));
        var (first, _) = await StartAsync(show.S01E01);
        Assert.Equal(show.S01E02, Id(first.GetProperty("next")));
        Assert.Equal(JsonValueKind.Null, first.GetProperty("previous").ValueKind);
        Assert.Equal(new[] { show.Series, show.Season1 }, first.GetProperty("ancestors").EnumerateArray().Select(Id));
    }

    [Fact]
    public async Task AFilm_HasNoTrailOrNeighbours()
    {
        var film = await SeedFilmAsync();

        var (info, statements) = await StartAsync(film);

        Assert.Equal(film, Id(info.GetProperty("item")));
        Assert.Empty(info.GetProperty("ancestors").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, info.GetProperty("next").ValueKind);
        Assert.Equal(JsonValueKind.Null, info.GetProperty("previous").ValueKind);
        Assert.Equal(JsonValueKind.Null, info.GetProperty("speed").ValueKind);
        Assert.Equal(300, info.GetProperty("startPositionSeconds").GetDouble());
        // The subtitles last chosen for it are on again.
        Assert.NotEqual(JsonValueKind.Null, info.GetProperty("selectedSubtitleKey").ValueKind);
        Assert.InRange(statements, 1, 10);
    }

    [Fact]
    public async Task ABook_StartsWhereItWasLeft()
    {
        var book = await SeedBookAsync();
        using (var client = Client())
        {
            (await client.PutAsJsonAsync(Api($"/items/{book}/progress"), new { positionSeconds = 700, device = "Kitchen speaker" })).EnsureSuccessStatusCode();
        }

        var (info, _) = await StartAsync(book, deviceName: "Phone");

        Assert.Equal(book, Id(info.GetProperty("item")));
        Assert.Equal(700, info.GetProperty("startPositionSeconds").GetDouble());
        Assert.Equal(1200, info.GetProperty("durationSeconds").GetDouble());
        Assert.Equal(2, info.GetProperty("book").GetProperty("files").GetArrayLength());
        Assert.Empty(info.GetProperty("ancestors").EnumerateArray());
    }

    private static Guid Id(JsonElement summary) => summary.GetProperty("id").GetGuid();

    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private HttpClient Client()
    {
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    /// <summary>Starts playback, ends the session, and gives back the answer with how many statements the start ran.</summary>
    private async Task<(JsonElement Info, int Statements)> StartAsync(Guid item, string? deviceName = null)
    {
        using var client = Client();
        _sql.Statements.Clear();
        var response = await client.PostAsJsonAsync(Api("/playback"), new { itemId = item, profile = Player, deviceName });
        var statements = _sql.Statements.Count;
        response.EnsureSuccessStatusCode();
        var info = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = info.GetProperty("sessionId").GetString()!;
        await client.DeleteAsync(Api($"/playback/{id}"));
        return (info, statements);
    }

    private sealed record Show(Guid Series, Guid Season1, Guid Season2, Guid S01E01, Guid S01E02, Guid S02E01, Guid S02E02);

    private async Task<Show> SeedShowAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Shows {Guid.NewGuid():N}", Type = LibraryType.TvShows };
        var series = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Series, Title = "Show", SortTitle = "show" };
        db.AddRange(library, series);
        var ids = new List<Guid>();
        var seasons = new List<Guid>();
        for (var s = 1; s <= 2; s++)
        {
            var season = new MediaItem { LibraryId = library.Id, ParentId = series.Id, Kind = MediaKind.Season, Title = $"Season {s}", SortTitle = $"season {s}", SeasonNumber = s };
            db.Add(season);
            seasons.Add(season.Id);
            for (var n = 1; n <= 2; n++)
            {
                var episode = new MediaItem
                {
                    LibraryId = library.Id, ParentId = season.Id, Kind = MediaKind.Episode, Title = $"Episode {s}x{n}", SortTitle = $"episode {s}x{n}",
                    SeasonNumber = s, EpisodeNumber = n,
                };
                db.AddRange(episode, FileFor(episode.Id));
                ids.Add(episode.Id);
            }
        }

        // French last time, on another episode of the show; this one was left two minutes in; the show is watched fast.
        db.PlaybackStates.Add(new PlaybackState { MediaItemId = ids[0], PositionSeconds = 600, Watched = true, AudioLanguage = "fre", LastPlayedAt = DateTime.UtcNow.AddDays(-2) });
        db.PlaybackStates.Add(new PlaybackState { MediaItemId = ids[2], PositionSeconds = 120, LastPlayedAt = DateTime.UtcNow.AddDays(-3) });
        db.ShowPlaybackSpeeds.Add(new ShowPlaybackSpeed { SeriesId = series.Id, Speed = 1.25 });
        await db.SaveChangesAsync();
        return new Show(series.Id, seasons[0], seasons[1], ids[0], ids[1], ids[2], ids[3]);
    }

    private async Task<Guid> SeedFilmAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies };
        var film = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Movie, Title = "Film", SortTitle = "film", RuntimeMinutes = 10 };
        db.AddRange(library, film, FileFor(film.Id));
        db.PlaybackStates.Add(new PlaybackState { MediaItemId = film.Id, PositionSeconds = 300, SubtitleLanguage = "eng" });
        await db.SaveChangesAsync();
        return film.Id;
    }

    private async Task<Guid> SeedBookAsync()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_factory.MediaDirectory, Guid.NewGuid().ToString("N"))).FullName;
        foreach (var name in new[] { "01.mp3", "02.mp3" })
        {
            File.WriteAllText(Path.Combine(folder, name), name);
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Books {Guid.NewGuid():N}", Type = LibraryType.Audiobooks };
        var book = new MediaItem
        {
            LibraryId = library.Id, Kind = MediaKind.Audiobook, Title = "A Book", SortTitle = "book", Path = folder,
            Book = new BookDetails { DurationSeconds = 1200 },
            Files = [.. new[] { "01.mp3", "02.mp3" }.Select((f, i) => new MediaFile { Path = Path.Combine(folder, f), AudioCodec = "mp3", DurationSeconds = 600, BookOrder = i, Size = 10 })],
        };
        db.AddRange(library, book);
        await db.SaveChangesAsync();
        return book.Id;
    }

    private MediaFile FileFor(Guid item) => new()
    {
        MediaItemId = item,
        Path = Path.Combine(_factory.MediaDirectory, $"{item:N}.mkv"),
        Container = "matroska,webm",
        DurationSeconds = 600,
        VideoCodec = "h264",
        Width = 1920,
        Height = 1080,
        AudioCodec = "aac",
        AudioChannels = 2,
        Streams =
        [
            new MediaStream { Index = 0, Type = StreamType.Video, Codec = "h264", Width = 1920, Height = 1080 },
            new MediaStream { Index = 1, Type = StreamType.Audio, Codec = "aac", Channels = 2, Language = "eng", IsDefault = true },
            new MediaStream { Index = 2, Type = StreamType.Audio, Codec = "aac", Channels = 2, Language = "fre" },
            new MediaStream { Index = 3, Type = StreamType.Subtitle, Codec = "subrip", Language = "eng" },
        ],
    };
}
