using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Endpoints;

public static class PeopleEndpoints
{
    /// <summary>A person and everything they're in that's in your library.</summary>
    public static RouteGroupBuilder MapPeopleEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/people/{id:guid}", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var person = await db.People.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            if (person is null)
            {
                return Results.NotFound();
            }

            var credits = await db.Credits.Where(c => c.PersonId == id).Select(c => new { c.MediaItemId, c.Role, c.Character }).ToListAsync(ct);
            var itemIds = credits.Select(c => c.MediaItemId).Distinct().ToList();
            var items = await db.MediaItems.Where(i => itemIds.Contains(i.Id)).Summaries(profile.Id).ToDictionaryAsync(i => i.Id, ct);

            var list = credits
                .Where(c => items.ContainsKey(c.MediaItemId))
                .Select(c => new PersonCreditDto(items[c.MediaItemId], c.Role, c.Character))
                .OrderByDescending(c => c.Item.Year)
                .ToList();
            return Results.Ok(new PersonDto(person.Id, person.Name, ArtworkUrls.Person(person.Id, person.ImageUrl != null), list));
        }).WithTags("People");

        return api;
    }
}
