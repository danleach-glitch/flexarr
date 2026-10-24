using System.Net;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

public class DeleteTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    [Fact]
    public async Task TitlesSonarrAndRadarrDontManage_AreNeverDeleted()
    {
        Guid film;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var library = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies };
            var movie = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Movie, Title = "Home Video", SortTitle = "home video" };
            db.AddRange(library, movie);
            await db.SaveChangesAsync();
            film = movie.Id;
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var response = await client.DeleteAsync(new Uri($"/api/v1/items/{film}", UriKind.Relative));

        using var check = factory.Services.CreateScope();
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(await check.ServiceProvider.GetRequiredService<FlexarrDbContext>().MediaItems.AnyAsync(i => i.Id == film));
    }
}
