using System.Text.Json;
using Flexarr.Api.Endpoints;
using Flexarr.Api.Profiles;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Flexarr.Api.Collections;

/// <param name="Personal">Made by this profile for itself (otherwise a household collection).</param>
/// <param name="CanEdit">Its owner, or the main profile for household ones.</param>
/// <param name="Posters">Up to four of its titles' posters, for artwork made of them.</param>
/// <param name="Poster">A franchise's own poster.</param>
public sealed record CollectionSummaryDto(
    Guid Id,
    string Name,
    string? Description,
    CollectionKind Kind,
    bool Personal,
    bool CanEdit,
    int ItemCount,
    int WatchedCount,
    int MissingCount,
    IReadOnlyList<string> Posters,
    string? Poster,
    bool ShowOnHome,
    bool Hidden);

public sealed record MissingTitleDto(int TmdbId, string Title, int? Year, string? Overview, string? Poster);

public sealed record CollectionDetailDto(CollectionSummaryDto Summary, string? Backdrop, IReadOnlyList<ItemSummaryDto> Items, IReadOnlyList<MissingTitleDto> Missing, SmartRules? Rules);

public sealed record CollectionRequest(string? Name = null, string? Description = null, bool? Personal = null, SmartRules? Rules = null, bool? ShowOnHome = null, bool? Hidden = null);

public sealed record CollectionItemRequest(Guid ItemId);

public sealed record CollectionOrderRequest(IReadOnlyList<Guid> ItemIds);

/// <summary>A collection, from a title's point of view: whether it's in it, and whether this profile may change that.</summary>
public sealed record ItemCollectionDto(Guid Id, string Name, CollectionKind Kind, bool Personal, bool Contains, bool CanEdit);

public static class CollectionEndpoints
{
    public static RouteGroupBuilder MapCollectionEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/collections").WithTags("Collections").NotForKids();

        group.MapGet("/", async (bool? hidden, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var collections = await Visible(db, profile).Where(c => hidden == true && profile.IsMainProfile || !c.Hidden).OrderBy(c => c.Name).ToListAsync(ct);
            // A collection with nothing this profile can see (e.g. films, for a profile kept out of them) isn't listed.
            return (await SummariesAsync(db, profile, collections, ct))
                .Where(s => s.ItemCount > 0 || s.CanEdit && s.Kind != CollectionKind.Franchise).ToList();
        });

        group.MapGet("/suggestions", async (FlexarrDbContext db, CurrentProfile profile, IMemoryCache cache, CancellationToken ct) =>
        {
            // Working them out takes a few dozen queries over the whole library, and they hardly change from minute to minute.
            // Keyed by the libraries hidden from the profile and its age limit too, so changing either takes effect at once.
            var hiddenLibraries = string.Join(',', db.HiddenLibraryIds.Order());
            var all = await cache.GetOrCreateAsync($"collection-suggestions:{profile.Id}:{hiddenLibraries}:{db.MaxRatingAge}:{LibraryVisibility.KidsSettingsVersion}", entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                return CollectionSuggestions.ForAsync(db, profile.Id, ct);
            }) ?? [];
            // Names already used are left out fresh each time, so a suggestion just turned into a collection goes at once.
            var taken = (await Visible(db, profile).Select(c => c.Name).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return all.Where(s => !taken.Contains(s.Name)).ToList();
        });

        group.MapPost("/sync", async (FranchiseSync sync, CancellationToken ct) => Results.Ok(new { franchises = await sync.SyncAsync(ct) }))
            .RequireAuthorization(FlexarrClaims.AdminPolicy);

        group.MapGet("/{id:guid}", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (await Visible(db, profile).FirstOrDefaultAsync(c => c.Id == id, ct) is not { } collection)
            {
                return Results.NotFound();
            }

            var items = await ItemsAsync(db, profile, collection, ct);
            var hidden = await LibraryVisibility.HiddenTypesAsync(db, ct);
            var missing = collection.MissingJson is null || hidden.Hides(MediaKind.Movie)
                ? []
                : JsonSerializer.Deserialize<List<MissingFilm>>(collection.MissingJson, FranchiseSync.Json)!
                    .Select(m => new MissingTitleDto(m.TmdbId, m.Title, m.Year, m.Overview, Remote(m.Poster))).ToList();
            return Results.Ok(new CollectionDetailDto(Summary(profile, collection, items, missing.Count), Remote(collection.BackdropUrl), items, missing, Rules(collection)));
        });

        group.MapPost("/", async (CollectionRequest request, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Give the collection a name."] });
            }

            var personal = request.Personal ?? !profile.IsMainProfile;
            if (!personal && !profile.IsMainProfile)
            {
                return Results.Problem("Only the main profile can make collections for the whole household.", statusCode: 403);
            }

            var collection = new Collection
            {
                Name = request.Name.Trim()[..Math.Min(request.Name.Trim().Length, 120)],
                Description = request.Description?.Trim(),
                Kind = request.Rules is null ? CollectionKind.Manual : CollectionKind.Smart,
                OwnerProfileId = personal ? profile.Id : null,
                RulesJson = request.Rules is null ? null : JsonSerializer.Serialize(request.Rules, FranchiseSync.Json),
                ShowOnHome = request.ShowOnHome ?? false,
            };
            db.Collections.Add(collection);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/collections/{collection.Id}", await SummaryAsync(db, profile, collection, ct));
        });

        group.MapPut("/{id:guid}", async (Guid id, CollectionRequest request, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (await Editable(db, profile, id, ct) is not { } collection)
            {
                return Results.NotFound();
            }

            // Franchises follow Radarr: their name and films aren't edited here.
            if (collection.Kind != CollectionKind.Franchise && !string.IsNullOrWhiteSpace(request.Name))
            {
                collection.Name = request.Name.Trim()[..Math.Min(request.Name.Trim().Length, 120)];
            }

            if (request.Description is not null)
            {
                collection.Description = request.Description.Trim().Length == 0 ? null : request.Description.Trim();
            }

            if (request.Rules is not null && collection.Kind == CollectionKind.Smart)
            {
                collection.RulesJson = JsonSerializer.Serialize(request.Rules, FranchiseSync.Json);
            }

            collection.ShowOnHome = request.ShowOnHome ?? collection.ShowOnHome;
            collection.Hidden = request.Hidden ?? collection.Hidden;
            collection.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(await SummaryAsync(db, profile, collection, ct));
        });

        // Franchises are hidden rather than deleted, or the next sync would bring them back.
        group.MapDelete("/{id:guid}", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (await Editable(db, profile, id, ct) is not { } collection)
            {
                return Results.NotFound();
            }

            if (collection.Kind == CollectionKind.Franchise)
            {
                collection.Hidden = true;
            }
            else
            {
                db.Collections.Remove(collection);
            }

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapPost("/{id:guid}/items", async (Guid id, CollectionItemRequest request, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (await Editable(db, profile, id, ct) is not { Kind: CollectionKind.Manual } collection)
            {
                return Results.NotFound();
            }

            if (!await db.MediaItems.AnyAsync(i => i.Id == request.ItemId && (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series), ct))
            {
                return Results.Problem("Only films and shows can be added to a collection.", statusCode: 400);
            }

            if (!await db.CollectionItems.AnyAsync(i => i.CollectionId == id && i.MediaItemId == request.ItemId, ct))
            {
                var next = await db.CollectionItems.Where(i => i.CollectionId == id).Select(i => (int?)i.Position).MaxAsync(ct) ?? -1;
                db.CollectionItems.Add(new CollectionItem { CollectionId = id, MediaItemId = request.ItemId, Position = next + 1 });
                collection.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }

            return Results.NoContent();
        });

        group.MapDelete("/{id:guid}/items/{itemId:guid}", async (Guid id, Guid itemId, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (await Editable(db, profile, id, ct) is not { Kind: CollectionKind.Manual })
            {
                return Results.NotFound();
            }

            await db.CollectionItems.Where(i => i.CollectionId == id && i.MediaItemId == itemId).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        group.MapPut("/{id:guid}/order", async (Guid id, CollectionOrderRequest request, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (await Editable(db, profile, id, ct) is not { Kind: CollectionKind.Manual })
            {
                return Results.NotFound();
            }

            var items = await db.CollectionItems.Where(i => i.CollectionId == id).ToListAsync(ct);
            foreach (var item in items)
            {
                var at = request.ItemIds.ToList().IndexOf(item.MediaItemId);
                item.Position = at < 0 ? request.ItemIds.Count + item.Position : at;
            }

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // For "Add to collection": the manual ones this profile can change, and which already hold the title.
        api.MapGet("/items/{id:guid}/collections", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var collections = await Visible(db, profile).Where(c => !c.Hidden && (c.Kind == CollectionKind.Manual || c.Items.Any(i => i.MediaItemId == id)))
                .OrderBy(c => c.Name).Select(c => new { c.Id, c.Name, c.Kind, c.OwnerProfileId, Contains = c.Items.Any(i => i.MediaItemId == id) }).ToListAsync(ct);
            return collections.Select(c => new ItemCollectionDto(c.Id, c.Name, c.Kind, c.OwnerProfileId != null, c.Contains,
                c.Kind == CollectionKind.Manual && (c.OwnerProfileId == profile.Id || c.OwnerProfileId == null && profile.IsMainProfile))).ToList();
        }).WithTags("Collections");

        return api;
    }

    /// <summary>Rows for Home: collections this profile has chosen to show there, in layout order.</summary>
    public static async Task<List<HomeRowDto>> HomeRowsAsync(FlexarrDbContext db, CurrentProfile profile, int rowSize, CancellationToken ct)
    {
        var shown = await Visible(db, profile).Where(c => c.ShowOnHome && !c.Hidden).OrderBy(c => c.Name).ToListAsync(ct);

        // Every hand-picked collection's titles in two queries, rather than two for each collection. Smart ones are
        // queries of their own.
        var manual = shown.Where(c => c.Kind != CollectionKind.Smart).Select(c => c.Id).ToList();
        var entries = await db.CollectionItems.Where(i => manual.Contains(i.CollectionId) && i.MediaItem != null).OrderBy(i => i.Position)
            .Select(i => new { i.CollectionId, i.MediaItemId }).ToListAsync(ct);
        var firsts = entries.GroupBy(e => e.CollectionId).ToDictionary(g => g.Key, g => g.Select(e => e.MediaItemId).Take(rowSize).ToList());
        var wanted = firsts.Values.SelectMany(ids => ids).Distinct().ToList();
        var summaries = await db.MediaItems.Where(i => wanted.Contains(i.Id)).Summaries(profile.Id).ToDictionaryAsync(i => i.Id, ct);

        var rows = new List<HomeRowDto>();
        foreach (var c in shown)
        {
            var items = c.Kind == CollectionKind.Smart
                ? await ItemsAsync(db, profile, c, rowSize, ct)
                : [.. firsts.GetValueOrDefault(c.Id, []).Where(summaries.ContainsKey).Select(id => summaries[id])];
            rows.Add(new HomeRowDto($"collection-{c.Id}", c.Name, "poster", items, $"/collection/{c.Id}"));
        }

        return rows;
    }

    /// <summary>Household collections and this profile's own.</summary>
    private static IQueryable<Collection> Visible(FlexarrDbContext db, CurrentProfile profile)
    {
        var me = profile.Id;
        return db.Collections.Where(c => c.OwnerProfileId == null || c.OwnerProfileId == me);
    }

    private static async Task<Collection?> Editable(FlexarrDbContext db, CurrentProfile profile, Guid id, CancellationToken ct) =>
        await Visible(db, profile).FirstOrDefaultAsync(c => c.Id == id, ct) is { } c && CanEdit(profile, c) ? c : null;

    /// <summary>
    /// Whether <paramref name="profile"/> may change it: its owner, or the main profile for the household's, without
    /// its PIN (the household's collections are everyday tidying, not managing the server).
    /// </summary>
    private static bool CanEdit(CurrentProfile profile, Collection c) =>
        c.OwnerProfileId == profile.Id || c.OwnerProfileId == null && profile.IsMainProfile;

    private static Task<List<ItemSummaryDto>> ItemsAsync(FlexarrDbContext db, CurrentProfile profile, Collection c, CancellationToken ct) =>
        ItemsAsync(db, profile, c, int.MaxValue, ct);

    /// <param name="take">At most this many (the first ones), worked out in the query.</param>
    private static async Task<List<ItemSummaryDto>> ItemsAsync(FlexarrDbContext db, CurrentProfile profile, Collection c, int take, CancellationToken ct)
    {
        if (c.Kind == CollectionKind.Smart)
        {
            return await (Rules(c) ?? new SmartRules()).Query(db, profile.Id, DateTime.UtcNow).Take(take).Summaries(profile.Id).ToListAsync(ct);
        }

        // Items in libraries this profile can't see drop out through the library filter on MediaItems.
        var order = await db.CollectionItems.Where(i => i.CollectionId == c.Id && i.MediaItem != null).OrderBy(i => i.Position)
            .Select(i => i.MediaItemId).Take(take).ToListAsync(ct);
        var items = await db.MediaItems.Where(i => order.Contains(i.Id)).Summaries(profile.Id).ToDictionaryAsync(i => i.Id, ct);
        return order.Where(items.ContainsKey).Select(id => items[id]).ToList();
    }

    private static async Task<CollectionSummaryDto> SummaryAsync(FlexarrDbContext db, CurrentProfile profile, Collection c, CancellationToken ct) =>
        (await SummariesAsync(db, profile, [c], ct))[0];

    /// <summary>
    /// What a list of collections shows (counts and four posters each) without summarising every title in them:
    /// one query for all the manual and franchise ones, one per smart one.
    /// </summary>
    private static async Task<List<CollectionSummaryDto>> SummariesAsync(FlexarrDbContext db, CurrentProfile profile, IReadOnlyList<Collection> collections, CancellationToken ct)
    {
        var me = profile.Id;
        var listed = collections.Where(c => c.Kind != CollectionKind.Smart).Select(c => c.Id).ToList();
        var entries = listed.Count == 0 ? []
            : await db.CollectionItems.Where(i => listed.Contains(i.CollectionId) && i.MediaItem != null)
                .Select(i => new { i.CollectionId, i.Position, i.MediaItemId }).ToListAsync(ct);
        var itemIds = entries.Select(e => e.MediaItemId).Distinct().ToList();
        var facts = itemIds.Count == 0 ? []
            : await db.MediaItems.Where(i => itemIds.Contains(i.Id)).Select(FactOf(me)).ToDictionaryAsync(f => f.Id, ct);
        var byCollection = entries.Where(e => facts.ContainsKey(e.MediaItemId)).ToLookup(e => e.CollectionId);

        var hidden = collections.Any(c => c.MissingJson is not null) ? await LibraryVisibility.HiddenTypesAsync(db, ct) : null;
        var summaries = new List<CollectionSummaryDto>();
        foreach (var c in collections)
        {
            var missing = c.MissingJson is null || hidden!.Hides(MediaKind.Movie) ? 0 : JsonSerializer.Deserialize<List<MissingFilm>>(c.MissingJson, FranchiseSync.Json)!.Count;
            if (c.Kind == CollectionKind.Smart)
            {
                // A broad rule can match thousands: count them in the database and fetch only the four posters.
                var matches = (Rules(c) ?? new SmartRules()).Query(db, me, DateTime.UtcNow);
                var count = await matches.CountAsync(ct);
                var watched = await matches.Where(WatchedBy(me)).CountAsync(ct);
                var first = await matches.Where(i => i.PosterUrl != null).Take(4).Select(FactOf(me)).ToListAsync(ct);
                summaries.Add(Summary(profile, c, count, watched, Posters(first), missing));
                continue;
            }

            var items = byCollection[c.Id].OrderBy(e => e.Position).Select(e => facts[e.MediaItemId]).ToList();
            summaries.Add(Summary(profile, c, items.Count, items.Count(i => i.Watched), Posters(items.Where(i => i.HasPoster).Take(4)), missing));
        }

        return summaries;
    }

    private static List<string> Posters(IEnumerable<ItemFact> items) =>
        items.Select(i => ArtworkUrls.For(i.Id, i.MetadataUpdatedAt == null ? 0 : i.MetadataUpdatedAt.Value.Ticks / 10_000_000, true, false, false, false).Poster!).ToList();

    /// <summary>Watched as <see cref="FactOf"/> works it out: a show once every episode is.</summary>
    private static System.Linq.Expressions.Expression<Func<MediaItem, bool>> WatchedBy(string profileId) => i =>
        i.Kind == MediaKind.Series
            ? !i.Children.SelectMany(s => s.Children).Any(e => !e.PlaybackStates.Any(p => p.UserId == profileId && p.Watched))
            : i.PlaybackStates.Any(p => p.UserId == profileId && p.Watched);

    /// <summary>Just enough of a title for a collection's counts and poster: watched works as it does in summaries.</summary>
    private sealed record ItemFact(Guid Id, bool HasPoster, DateTime? MetadataUpdatedAt, bool Watched);

    private static System.Linq.Expressions.Expression<Func<MediaItem, ItemFact>> FactOf(string profileId) => i => new ItemFact(
        i.Id, i.PosterUrl != null, i.MetadataUpdatedAt,
        i.Kind == MediaKind.Series
            ? !i.Children.SelectMany(s => s.Children).Any(e => !e.PlaybackStates.Any(p => p.UserId == profileId && p.Watched))
            : i.PlaybackStates.Any(p => p.UserId == profileId && p.Watched));

    private static CollectionSummaryDto Summary(CurrentProfile profile, Collection c, List<ItemSummaryDto> items, int missing) =>
        Summary(profile, c, items.Count, items.Count(i => i.UserData.Watched), items.Select(i => i.Artwork.Poster).OfType<string>().Take(4).ToList(), missing);

    private static CollectionSummaryDto Summary(CurrentProfile profile, Collection c, int count, int watched, IReadOnlyList<string> posters, int missing) => new(
        c.Id, c.Name, c.Description, c.Kind, c.OwnerProfileId != null, CanEdit(profile, c), count, watched, missing,
        posters, Remote(c.PosterUrl), c.ShowOnHome, c.Hidden);


    private static SmartRules? Rules(Collection c) => c.RulesJson is null ? null : JsonSerializer.Deserialize<SmartRules>(c.RulesJson, FranchiseSync.Json);

    private static string? Remote(string? url) => ImageEndpoints.RemoteUrl(url);
}
