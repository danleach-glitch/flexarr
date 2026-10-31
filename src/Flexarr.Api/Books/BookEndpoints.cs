using Flexarr.Api.Endpoints;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Books;

/// <summary>Audiobook libraries: their shelves, lists, series, authors and narrators.</summary>
public static class BookEndpoints
{
    private const int MaxPage = 200;

    public static RouteGroupBuilder MapBookEndpoints(this RouteGroupBuilder api)
    {
        var library = api.MapGroup("/libraries/{id:guid}").WithTags("Audiobooks");

        library.MapGet("/book-shelves", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
            await IsBooksAsync(db, id, ct) ? Results.Ok(await BookQueries.ShelvesAsync(db, id, profile.Id, ct)) : Results.NotFound());

        library.MapGet("/books", async (Guid id, [AsParameters] BookFilter filter, int? skip, int? take, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (!await IsBooksAsync(db, id, ct))
            {
                return Results.NotFound();
            }

            var books = BookQueries.Filter(BookQueries.Books(db, id), filter, profile.Id);
            var total = await books.CountAsync(ct);
            var items = await books.Skip(Math.Max(0, skip ?? 0)).Take(Math.Clamp(take ?? 60, 1, MaxPage)).Summaries(profile.Id).ToListAsync(ct);
            return Results.Ok(new PagedDto<ItemSummaryDto>(total, items));
        });

        library.MapGet("/book-series", async (Guid id, string? search, int? skip, int? take, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
            await IsBooksAsync(db, id, ct)
                ? Results.Ok(await BookQueries.SeriesListAsync(db, id, search, Math.Max(0, skip ?? 0), Math.Clamp(take ?? 60, 1, MaxPage), profile.Id, ct))
                : Results.NotFound());

        // role: "author" (the default) or "narrator".
        library.MapGet("/book-people", async (Guid id, string? role, string? search, int? skip, int? take, FlexarrDbContext db, CancellationToken ct) =>
            await IsBooksAsync(db, id, ct)
                ? Results.Ok(await BookQueries.PeopleAsync(db, id, role == "narrator" ? CreditRole.Narrator : CreditRole.Author, search,
                    Math.Max(0, skip ?? 0), Math.Clamp(take ?? 60, 1, MaxPage), ct))
                : Results.NotFound());

        api.MapGet("/book-series/{id:guid}", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
            await BookQueries.SeriesAsync(db, id, profile.Id, ct) is { } series && await IsBooksAsync(db, series.LibraryId, ct)
                ? Results.Ok(series)
                : Results.NotFound())
            .WithTags("Audiobooks");

        return api;
    }

    /// <summary>The library is an audiobooks library this profile can see (the context's filter hides the rest).</summary>
    private static Task<bool> IsBooksAsync(FlexarrDbContext db, Guid libraryId, CancellationToken ct) =>
        db.Libraries.AnyAsync(l => l.Id == libraryId && l.Type == LibraryType.Audiobooks, ct);
}
