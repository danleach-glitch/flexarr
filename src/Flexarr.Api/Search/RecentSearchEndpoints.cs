using Flexarr.Api.Endpoints;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Search;

/// <summary>The profile's last few searches, newest first, and what they opened from them (absent before 0.56).</summary>
public sealed record RecentSearchesDto(IReadOnlyList<string> Queries, IReadOnlyList<ItemSummaryDto> Items);

/// <param name="Query">What was searched for. Absent when an item was reopened from the recent list itself.</param>
/// <param name="ItemId">What was opened from the results, when it was a title, album, episode or track (not a person or genre).</param>
public sealed record RecentSearchRequest(string? Query, Guid? ItemId);

public static class RecentSearchEndpoints
{
    public const int Queries = 5;
    public const int Items = 10;

    /// <summary>Rows kept per profile: plenty to find five different searches and ten different items in.</summary>
    private const int Kept = 100;

    /// <summary>
    /// Recent searches, kept per profile on the server so a search made on the phone is there on the TV. Apps record
    /// one when something is picked from the results, not as people type, so half-typed words never show up.
    /// </summary>
    public static RouteGroupBuilder MapRecentSearchEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/search/recent", async (FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var rows = await RowsAsync(db, profile.Id, ct);
            var queries = rows.Where(r => r.Query is not null).Select(r => r.Query!)
                .DistinctBy(q => q, StringComparer.OrdinalIgnoreCase).Take(Queries).ToList();
            var ids = rows.Where(r => r.ItemId is not null).Select(r => r.ItemId!.Value).Distinct().ToList();
            // Hidden libraries and missing files drop out here, so ask for them all and keep the first ten left.
            var items = await db.MediaItems.Where(i => ids.Contains(i.Id)).Summaries(profile.Id).ToListAsync(ct);
            return new RecentSearchesDto(queries, [.. items.OrderBy(i => ids.IndexOf(i.Id)).Take(Items)]);
        }).WithTags("Discovery");

        api.MapPost("/search/recent", async (RecentSearchRequest request, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var query = request.Query?.Trim();
            if (query is { Length: > RecentSearch.MaxQueryLength })
            {
                query = query[..RecentSearch.MaxQueryLength];
            }

            var itemId = request.ItemId is { } id && await db.MediaItems.AnyAsync(i => i.Id == id, ct) ? id : (Guid?)null;
            if (string.IsNullOrEmpty(query) && itemId is null)
            {
                return Results.BadRequest();
            }

            db.RecentSearches.Add(new RecentSearch { ProfileId = profile.Id, Query = string.IsNullOrEmpty(query) ? null : query, ItemId = itemId });
            await db.SaveChangesAsync(ct);
            await db.RecentSearches.Where(r => r.ProfileId == profile.Id).OrderByDescending(r => r.SearchedAt).ThenByDescending(r => r.Id)
                .Skip(Kept).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        }).WithTags("Discovery");

        // One search (every time it was made, and what was opened from it), one item, or with neither, everything.
        api.MapDelete("/search/recent", async (string? query, Guid? itemId, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var rows = await db.RecentSearches.Where(r => r.ProfileId == profile.Id).ToListAsync(ct);
            if (query is null && itemId is null)
            {
                db.RecentSearches.RemoveRange(rows);
            }

            if (query?.Trim() is { Length: > 0 } words)
            {
                db.RecentSearches.RemoveRange(rows.Where(r => string.Equals(r.Query, words, StringComparison.OrdinalIgnoreCase)));
            }

            if (itemId is { } id)
            {
                foreach (var row in rows.Where(r => r.ItemId == id))
                {
                    // Forget the item, but not the words that found it.
                    if (row.Query is null)
                    {
                        db.RecentSearches.Remove(row);
                    }
                    else
                    {
                        row.ItemId = null;
                    }
                }
            }

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithTags("Discovery");

        return api;
    }

    private static Task<List<RecentSearch>> RowsAsync(FlexarrDbContext db, string profileId, CancellationToken ct) =>
        db.RecentSearches.AsNoTracking().Where(r => r.ProfileId == profileId)
            .OrderByDescending(r => r.SearchedAt).ThenByDescending(r => r.Id).Take(Kept).ToListAsync(ct);
}
