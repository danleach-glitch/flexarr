using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

public class HealthScoreTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    [Fact]
    public async Task MissingArtwork_OnlyLowersTheScore_WhenRadarrOrSonarrCouldFillItIn()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var library = new Library { Name = "Films", Type = LibraryType.Movies };
            db.AddRange(library, new MediaItem { LibraryId = library.Id, Kind = MediaKind.Movie, Title = "Home Video", SortTitle = "home video" });
            await db.SaveChangesAsync();
        }

        static async Task<JsonElement> ScoreAsync(HttpClient client)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
            return (await client.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/library-health", UriKind.Relative))).GetProperty("score");
        }

        static (int, int, int) Read(JsonElement score) =>
            (score.GetProperty("score").GetInt32(), score.GetProperty("missingArtwork").GetInt32(), score.GetProperty("artworkNeedsConnection").GetInt32());

        var alone = await ScoreAsync(factory.CreateClient());
        using var app = new FakeArr().Serve(factory);
        var connected = await ScoreAsync(app.CreateClient());

        Assert.Equal((100, 0, 1), Read(alone));
        Assert.Equal((0, 1, 0), Read(connected));
    }
}
