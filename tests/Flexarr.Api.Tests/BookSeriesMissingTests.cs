using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Books;
using Flexarr.Core.Books;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Metadata.Books;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>A series page's books that the library doesn't have: what counts as had.</summary>
public class BookSeriesMissingTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    [Fact]
    public async Task ASeriesAudibleDoesntKnow_SaysSo_AndAnUnknownOneIsntFound()
    {
        var library = new Library { Name = $"Books {Guid.NewGuid():N}", Type = LibraryType.Audiobooks };
        var series = new BookSeries { LibraryId = library.Id, Name = "Local Only", SortName = "local only" };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            db.AddRange(library, series);
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var found = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/book-series/{series.Id}/missing", UriKind.Relative));
        var unknown = await client.GetAsync(new Uri($"/api/v1/book-series/{Guid.NewGuid()}/missing", UriKind.Relative));

        Assert.False(found.GetProperty("answered").GetBoolean());
        Assert.Empty(found.GetProperty("books").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task AnAgeLimitedProfile_SeesNoneOfIt_AndAudibleIsntAsked()
    {
        using var server = new FlexarrApiFactory();
        var audible = new CountingHandler();
        using var app = server.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddHttpClient("books").ConfigurePrimaryHttpMessageHandler(() => audible)));
        var library = new Library { Name = "Family books", Type = LibraryType.Audiobooks, FineForKids = true };
        var series = new BookSeries { LibraryId = library.Id, Name = "Thursday Murder Club", SortName = "thursday murder club", ExternalId = "B0SERIES" };
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            db.AddRange(library, series);
            await db.SaveChangesAsync();
        }

        var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var teen = (await (await admin.PostAsJsonAsync(new Uri("/api/v1/profiles", UriKind.Relative), new { name = "Sam", maxRatingAge = 15 })).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var token = (await (await admin.PostAsJsonAsync(new Uri("/api/v1/session/device", UriKind.Relative), new { deviceName = "Kitchen" })).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var device = app.CreateClient();
        device.DefaultRequestHeaders.Add("X-Api-Key", token);
        await device.PostAsJsonAsync(new Uri($"/api/v1/profiles/{teen}/select", UriKind.Relative), new { });

        var found = await device.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/book-series/{series.Id}/missing", UriKind.Relative));

        Assert.Empty(found.GetProperty("books").EnumerateArray());
        Assert.Equal(0, audible.Requests);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static SeriesBook Listed(string asin, string title, string? sequence) => new(asin, title, sequence, ["Richard Osman"], 2021, null);

    [Fact]
    public void ABookIsHad_ByItsAsin_ItsNumber_OrItsTitle()
    {
        IReadOnlyList<SeriesBook> listed =
        [
            Listed("A1", "The Thursday Murder Club", "1"),
            Listed("A2", "The Man Who Died Twice", "2"),
            Listed("A3", "The Bullet That Missed", "3"),
            Listed("A4", "The Last Devil to Die", "4"),
            Listed("A5", "The Impossible Fortune", "5"),
        ];

        var missing = BookSeriesMissing.Missing(listed,
        [
            new("A1", "1", "The Thursday Murder Club"),
            new("X2", "2", "Man Who Died Twice (Unabridged)"), // another edition, at its number
            new(null, null, "The Bullet That Missed: A Novel"), // tagged without a number
        ]);

        Assert.Equal(["A4", "A5"], missing.Select(m => m.Asin));
    }

    [Fact]
    public void Novellas_BetweenNumbers_AreTheirOwnBooks()
    {
        var missing = BookSeriesMissing.Missing([Listed("N", "A Short One", "1.5"), Listed("B", "Book Two", "2")], [new(null, "2", "Book Two")]);

        Assert.Equal("1.5", Assert.Single(missing).Sequence);
    }
}
