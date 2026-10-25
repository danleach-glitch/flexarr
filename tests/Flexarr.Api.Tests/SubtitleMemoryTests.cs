using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>
/// What a player's subtitle choice does to the next sitting: a pick holds, off stays off, and forced subtitles the
/// server turns on by itself aren't a preference that brings the full subtitles on next time.
/// </summary>
public class SubtitleMemoryTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private HttpClient Client()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    [Fact]
    public async Task WhatTheViewerPicksInThePlayer_IsWhatTheNextSittingStartsWith_OffIncluded()
    {
        var movie = await AddMovieAsync(("eng", false), ("fre", false));
        var client = Client();

        var first = await StartAsync(client, movie);
        Assert.Null(first.Subtitle); // nothing forced, no preference yet

        // The player switched to French on the device and says so.
        var picked = await client.PutAsJsonAsync(Api($"/playback/{first.Session}/subtitle"), new { key = "e3" });
        Assert.Equal(HttpStatusCode.NoContent, picked.StatusCode);
        await client.DeleteAsync(Api($"/playback/{first.Session}"));

        var second = await StartAsync(client, movie);
        Assert.Equal("e3", second.Subtitle);

        // Then turned them off: the next sitting starts without.
        var off = await client.PutAsJsonAsync(Api($"/playback/{second.Session}/subtitle"), new { key = "off" });
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        await client.DeleteAsync(Api($"/playback/{second.Session}"));

        var third = await StartAsync(client, movie);
        Assert.Null(third.Subtitle);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(Api($"/playback/{third.Session}/subtitle"), new { key = "e9" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(Api("/playback/no-such-session/subtitle"), new { key = "off" })).StatusCode);
        await client.DeleteAsync(Api($"/playback/{third.Session}"));
    }

    [Fact]
    public async Task ForcedSubtitlesTheServerTurnsOn_DontBecomeAPreferenceForTheFullOnes()
    {
        var movie = await AddMovieAsync(("eng", true), ("eng", false));
        var client = Client();

        var first = await StartAsync(client, movie);
        Assert.Equal("e2", first.Subtitle); // the forced track, for the foreign-language lines

        // Watching for a bit records the sitting; a player carrying the forced track over explicitly changes nothing.
        (await client.PostAsJsonAsync(Api($"/playback/{first.Session}/progress"), new { positionSeconds = 60 })).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync(Api($"/playback/{first.Session}/subtitle"), new { key = "e2" })).EnsureSuccessStatusCode();
        await client.DeleteAsync(Api($"/playback/{first.Session}"));

        var second = await StartAsync(client, movie);
        Assert.Equal("e2", second.Subtitle);
        await client.DeleteAsync(Api($"/playback/{second.Session}"));
    }

    private async Task<Guid> AddMovieAsync(params (string Language, bool Forced)[] subtitles)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies };
        var movie = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Movie, Title = "Film", SortTitle = "film" };
        var file = new MediaFile
        {
            MediaItemId = movie.Id,
            Path = Path.Combine(factory.MediaDirectory, $"{movie.Id:N}.mkv"),
            Container = "matroska,webm",
            DurationSeconds = 600,
            VideoCodec = "hevc",
            Width = 1920,
            Height = 1080,
            AudioCodec = "aac",
            AudioChannels = 2,
            Streams =
            [
                new MediaStream { Index = 0, Type = StreamType.Video, Codec = "hevc", Width = 1920, Height = 1080 },
                new MediaStream { Index = 1, Type = StreamType.Audio, Codec = "aac", Channels = 2, Language = "eng", IsDefault = true },
                .. subtitles.Select((s, i) => new MediaStream { Index = 2 + i, Type = StreamType.Subtitle, Codec = "subrip", Language = s.Language, IsForced = s.Forced }),
            ],
        };
        db.AddRange(library, movie, file);
        await db.SaveChangesAsync();
        return movie.Id;
    }

    private static async Task<(string Session, string? Subtitle)> StartAsync(HttpClient client, Guid item)
    {
        var response = await client.PostAsJsonAsync(Api("/playback"), new { itemId = item, profile = new { }, forceTranscode = true });
        response.EnsureSuccessStatusCode();
        var info = await response.Content.ReadFromJsonAsync<JsonElement>();
        var subtitle = info.GetProperty("selectedSubtitleKey");
        return (info.GetProperty("sessionId").GetString()!, subtitle.ValueKind == JsonValueKind.Null ? null : subtitle.GetString());
    }
}
