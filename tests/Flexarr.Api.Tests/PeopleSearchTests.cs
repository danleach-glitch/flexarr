using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>People found by name from the search index, ranked by the titles the profile can see them in.</summary>
public class PeopleSearchTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    [Fact]
    public async Task People_StartingWithTheWordsComeFirst_ThenByTitlesSeen_WithALinkedTitleCountedOnce()
    {
        var word = $"zq{Guid.NewGuid():N}"[..10];
        var expected = new List<Guid>();
        Guid doubled;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var library = new Library { Name = $"Films {word}", Type = LibraryType.Movies };
            db.Libraries.Add(library);
            MediaItem Film(int n, TitleGroup? group = null)
            {
                var film = new MediaItem
                {
                    LibraryId = library.Id, Kind = MediaKind.Movie, Title = $"Film {n}", SortTitle = $"film {n:0000}", AddedAt = DateTime.UtcNow, LinkGroup = group,
                };
                db.MediaItems.Add(film);
                return film;
            }

            var films = Enumerable.Range(0, 60).Select(n => Film(n)).ToList();
            // 30 people whose names start with the word, credited in 2, 4, … 60 films.
            var starting = new List<Person>();
            for (var i = 0; i < 30; i++)
            {
                var person = new Person { Name = $"{word} Starts {i.ToString(CultureInfo.InvariantCulture)}" };
                db.People.Add(person);
                starting.Add(person);
                db.Credits.AddRange(films.Take(2 * (i + 1)).Select((f, order) => new Credit { MediaItemId = f.Id, PersonId = person.Id, Role = CreditRole.Actor, Order = order }));
            }

            // 30 with the word inside their names, in every film: they come after everyone starting with it.
            for (var i = 0; i < 30; i++)
            {
                var person = new Person { Name = $"Ann {word} Inside {i.ToString(CultureInfo.InvariantCulture)}" };
                db.People.Add(person);
                db.Credits.AddRange(films.Select((f, order) => new Credit { MediaItemId = f.Id, PersonId = person.Id, Role = CreditRole.Actor, Order = order }));
            }

            // One in 47 titles, each in two libraries' copies: 94 credits, but 47 titles seen.
            var twice = new Person { Name = $"{word} Twice" };
            db.People.Add(twice);
            for (var n = 0; n < 47; n++)
            {
                var group = new TitleGroup();
                db.Credits.Add(new Credit { MediaItem = Film(1000 + n, group), PersonId = twice.Id, Role = CreditRole.Actor });
                db.Credits.Add(new Credit { MediaItem = Film(2000 + n, group), PersonId = twice.Id, Role = CreditRole.Actor });
            }

            await db.SaveChangesAsync();
            doubled = twice.Id;
            // 60, 58, … 48 titles, then the one in 47.
            expected.AddRange(Enumerable.Range(23, 7).Reverse().Select(i => starting[i].Id));
            expected.Add(twice.Id);
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var results = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/search?q={word}", UriKind.Relative));
        var people = results.GetProperty("people").EnumerateArray().ToList();

        Assert.Equal(expected, people.Select(p => p.GetProperty("id").GetGuid()));
        Assert.Equal(47, people.Single(p => p.GetProperty("id").GetGuid() == doubled).GetProperty("credits").GetInt32());
    }
}
