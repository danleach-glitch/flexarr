using System.Text.Json;
using Flexarr.Api.Endpoints;
using Flexarr.Api.Profiles;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

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
        var group = api.MapGroup("/collections").WithTags("Collections");

        group.MapGet("/", async (bool? hidden, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var collections = await Visible(db, profile).Where(c => hidden == true && profile.IsAdmin || !c.Hidden).OrderBy(c => c.Name).ToListAsync(ct);
            var summaries = new List<CollectionSummaryDto>();
            foreach (var c in collections)
            {
                var summary = await SummaryAsync(db, profile, c, ct);
                // A collection with nothing this profile can see (e.g. films, for a profile kept out of them) isn't listed.
                if (summary.ItemCount > 0 || summary.CanEdit && c.Kind != CollectionKind.Franchise)
                {
                    summaries.Add(summary);
                }
            }

            return summaries;
        });

        group.MapGet("/suggestions", async (FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var taken = (await Visible(db, profile).Select(c => c.Name).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return await CollectionSuggestions.ForAsync(db, profile.Id, taken, ct);
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

            var personal = request.Personal ?? !profile.IsAdmin;
            if (!personal && !profile.IsAdmin)
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
                c.Kind == CollectionKind.Manual && (c.OwnerProfileId == profile.Id || c.OwnerProfileId == null && profile.IsAdmin))).ToList();
        }).WithTags("Collections");

        return api;
    }

    /// <summary>Rows for Home: collections this profile has chosen to show there, in layout order.</summary>
    public static async Task<List<HomeRowDto>> HomeRowsAsync(FlexarrDbContext db, CurrentProfile profile, int rowSize, CancellationToken ct)
    {
        var rows = new List<HomeRowDto>();
        foreach (var c in await Visible(db, profile).Where(c => c.ShowOnHome && !c.Hidden).OrderBy(c => c.Name).ToListAsync(ct))
        {
            var items = await ItemsAsync(db, profile, c, ct);
            rows.Add(new HomeRowDto($"collection-{c.Id}", c.Name, "poster", items.Take(rowSize).ToList(), $"/collection/{c.Id}"));
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

    private static bool CanEdit(CurrentProfile profile, Collection c) => c.OwnerProfileId == profile.Id || c.OwnerProfileId == null && profile.IsAdmin;

    private static async Task<List<ItemSummaryDto>> ItemsAsync(FlexarrDbContext db, CurrentProfile profile, Collection c, CancellationToken ct)
    {
        if (c.Kind == CollectionKind.Smart)
        {
            return await (Rules(c) ?? new SmartRules()).Query(db, profile.Id, DateTime.UtcNow).Summaries(profile.Id).ToListAsync(ct);
        }

        // Items in libraries this profile can't see drop out through the library filter on MediaItems.
        var order = await db.CollectionItems.Where(i => i.CollectionId == c.Id && i.MediaItem != null).OrderBy(i => i.Position)
            .Select(i => i.MediaItemId).ToListAsync(ct);
        var items = await db.MediaItems.Where(i => order.Contains(i.Id)).Summaries(profile.Id).ToDictionaryAsync(i => i.Id, ct);
        return order.Where(items.ContainsKey).Select(id => items[id]).ToList();
    }

    private static async Task<CollectionSummaryDto> SummaryAsync(FlexarrDbContext db, CurrentProfile profile, Collection c, CancellationToken ct)
    {
        var items = await ItemsAsync(db, profile, c, ct);
        var missing = c.MissingJson is null ? 0 : JsonSerializer.Deserialize<List<MissingFilm>>(c.MissingJson, FranchiseSync.Json)!.Count;
        if (missing > 0 && (await LibraryVisibility.HiddenTypesAsync(db, ct)).Hides(MediaKind.Movie))
        {
            missing = 0;
        }

        return Summary(profile, c, items, missing);
    }

    private static CollectionSummaryDto Summary(CurrentProfile profile, Collection c, List<ItemSummaryDto> items, int missing) => new(
        c.Id, c.Name, c.Description, c.Kind, c.OwnerProfileId != null, CanEdit(profile, c), items.Count, items.Count(i => i.UserData.Watched), missing,
        items.Select(i => i.Artwork.Poster).OfType<string>().Take(4).ToList(), Remote(c.PosterUrl), c.ShowOnHome, c.Hidden);

    private static SmartRules? Rules(Collection c) => c.RulesJson is null ? null : JsonSerializer.Deserialize<SmartRules>(c.RulesJson, FranchiseSync.Json);

    private static string? Remote(string? url) => url is null ? null : $"/api/v1/images/remote?url={Uri.EscapeDataString(url)}";
}
