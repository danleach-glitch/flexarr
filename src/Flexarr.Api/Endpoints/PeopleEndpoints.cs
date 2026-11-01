using Flexarr.Api.Profiles;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Flexarr.Api.Endpoints;

public static class PeopleEndpoints
{
    /// <summary>How long a person's candidate photos and biographies are remembered (and their images served).</summary>
    private static readonly TimeSpan CandidatesFor = TimeSpan.FromMinutes(10);

    /// <summary>The longest biography an admin can write.</summary>
    private const int MaxBiography = 20_000;

    /// <summary>A person's candidates, as last found for an admin.</summary>
    internal sealed record Candidates(List<AuthorCandidate> Photos, List<AuthorCandidate> Biographies);

    internal static string CandidatesKey(Guid id) => $"person-candidates:{id}";

    /// <summary>A person and everything they're in that's in your library.</summary>
    public static RouteGroupBuilder MapPeopleEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/people/{id:guid}", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var person = await db.People.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            return person is null ? Results.NotFound() : Results.Ok(await ToDtoAsync(db, person, profile, ct));
        }).WithTags("People");

        var admin = api.MapGroup("/people/{id:guid}").WithTags("People").RequireAuthorization(FlexarrClaims.AdminPolicy);

        // Every photo and biography the server can find for them, to choose from.
        admin.MapGet("/candidates", async (Guid id, FlexarrDbContext db, AuthorInfo authors, IMemoryCache memory, CancellationToken ct) =>
        {
            var person = await db.People.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            if (person is null)
            {
                return Results.NotFound();
            }

            var found = await FindCandidatesAsync(person, authors, memory, ct);
            return Results.Ok(new PersonCandidatesDto(
                [.. found.Photos.Select(p => new PersonPhotoCandidateDto(p.Source, $"/api/v1/images/people/{id}/candidates/{Uri.EscapeDataString(p.Source)}"))],
                [.. found.Biographies.Select(b => new PersonBiographyCandidateDto(b.Source, b.Biography!))]));
        });

        admin.MapPut("/", async (Guid id, UpdatePersonRequest request, FlexarrDbContext db, AuthorInfo authors, IMemoryCache memory, CurrentProfile profile,
            CancellationToken ct) =>
        {
            var person = await db.People.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (person is null)
            {
                return Results.NotFound();
            }

            if (request.Biography is { Length: > MaxBiography })
            {
                return Results.Problem($"Biographies can be up to {MaxBiography:N0} characters.", statusCode: 400);
            }

            var now = DateTime.UtcNow;
            var byHand = false;
            if (request.Photo is { } source)
            {
                var found = await FindCandidatesAsync(person, authors, memory, ct);
                if (found.Photos.FirstOrDefault(p => p.Source == source) is not { Image: { } image })
                {
                    return Results.Problem("That photo can't be found any more. Look again.", statusCode: 409);
                }

                PersonSources.SetPhoto(person, image, source == AuthorInfo.Current ? person.PhotoSource : source, now);
                byHand = true;
            }

            if (request.BiographyGiven)
            {
                var text = string.IsNullOrWhiteSpace(request.Biography) ? null : request.Biography.Trim();
                if (text != person.Biography)
                {
                    // One of the candidates, unchanged, keeps its source; anything else was written here.
                    var known = memory.TryGetValue(CandidatesKey(id), out Candidates? cached) ? cached : null;
                    person.Biography = text;
                    person.BiographySource = text is null ? null
                        : known?.Biographies.FirstOrDefault(b => b.Biography == text && b.Source != AuthorInfo.Current)?.Source ?? PersonSources.Manual;
                }

                byHand = true;
            }

            person.InfoLocked = request.Locked ?? (byHand || person.InfoLocked);
            if (request.Locked == false && !byHand)
            {
                // Unlocked: looked up again now (filling what's missing, and a folder photo), not in a month.
                person.InfoCheckedAt = null;
                await authors.RefreshPersonAsync(person, ct);
            }

            await db.SaveChangesAsync(ct);
            memory.Remove(ImageEndpoints.PersonSourceKey(id));
            memory.Remove(CandidatesKey(id));
            return Results.Ok(await ToDtoAsync(db, person, profile, ct));
        });

        // The photo is the request body: a JPEG, PNG or WebP.
        admin.MapPut("/image", async (Guid id, HttpRequest http, FlexarrDbContext db, PersonImages images, IMemoryCache memory, CurrentProfile profile,
            CancellationToken ct) =>
        {
            var person = await db.People.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (person is null)
            {
                return Results.NotFound();
            }

            if (http.ContentLength is not > 0 || http.ContentLength > PersonImages.MaxUploadBytes)
            {
                return Results.Problem("Photos can be up to 10 MB.", statusCode: 413);
            }

            if (!await images.SaveAsync(id, http.Body, ct))
            {
                return Results.Problem("That file isn't a picture Flexarr can read. Try a JPEG or PNG.", statusCode: 400);
            }

            // Always a new ?v=: the file is replaced in place, so the URL alone wouldn't change.
            person.ImageUrl = images.UrlFor(id);
            person.PhotoSource = PersonSources.Upload;
            person.ImageChangedAt = DateTime.UtcNow;
            person.InfoLocked = true;
            await db.SaveChangesAsync(ct);
            memory.Remove(ImageEndpoints.PersonSourceKey(id));
            memory.Remove(CandidatesKey(id));
            return Results.Ok(await ToDtoAsync(db, person, profile, ct));
        });

        return api;
    }

    /// <summary>A person's candidates, from memory for ten minutes so their images can be served from the same list.</summary>
    private static async Task<Candidates> FindCandidatesAsync(Person person, AuthorInfo authors, IMemoryCache memory, CancellationToken ct)
    {
        if (memory.TryGetValue(CandidatesKey(person.Id), out Candidates? cached) && cached is not null)
        {
            return cached;
        }

        var (photos, biographies) = await authors.CandidatesAsync(person, ct);
        var found = new Candidates(photos, biographies);
        memory.Set(CandidatesKey(person.Id), found, CandidatesFor);
        return found;
    }

    /// <summary>The photo a person's candidates found from <paramref name="source"/>; null if there's none, or they've been forgotten.</summary>
    internal static string? CandidatePhoto(IMemoryCache memory, Guid id, string source) =>
        memory.TryGetValue(CandidatesKey(id), out Candidates? cached) ? cached?.Photos.FirstOrDefault(p => p.Source == source)?.Image : null;

    private static async Task<PersonDto> ToDtoAsync(FlexarrDbContext db, Person person, CurrentProfile profile, CancellationToken ct)
    {
        var credits = await db.Credits.Where(c => c.PersonId == person.Id).Select(c => new { c.MediaItemId, c.Role, c.Character }).ToListAsync(ct);
        var itemIds = credits.Select(c => c.MediaItemId).Distinct().ToList();
        var items = await db.MediaItems.Where(i => itemIds.Contains(i.Id)).Summaries(profile.Id).ToDictionaryAsync(i => i.Id, ct);

        var list = credits
            .Where(c => items.ContainsKey(c.MediaItemId))
            .Select(c => new PersonCreditDto(items[c.MediaItemId], c.Role, c.Character))
            .OrderByDescending(c => c.Item.Year)
            .ToList();
        return new PersonDto(person.Id, person.Name, ArtworkUrls.Person(person.Id, person.ImageUrl != null, person.ImageChangedAt), list, person.Biography,
            person.InfoLocked, person.PhotoSource, person.BiographySource);
    }
}
