using Flexarr.Api.Books;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Core.Scanning;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Endpoints;

/// <summary>
/// A film's or show's details as edited by hand. Blank fields clear (no description, no studio); a blank sort title is
/// worked out from the title. Locked: refreshes keep them (<see cref="MediaItem.DetailsLocked"/>). Added in 0.59.
/// </summary>
public sealed record ItemEditRequest(
    string Title,
    string? SortTitle = null,
    string? OriginalTitle = null,
    int? Year = null,
    string? Overview = null,
    IReadOnlyList<string>? Genres = null,
    string? Studio = null,
    bool Locked = true);

/// <summary>An album's details as edited by hand; <see cref="AlbumType"/> is one of <see cref="AlbumKind"/>'s values. Added in 0.59.</summary>
public sealed record AlbumEditRequest(
    string Title,
    int? Year = null,
    IReadOnlyList<string>? Genres = null,
    string? AlbumType = null,
    bool Locked = true);

/// <summary>
/// Editing a film's, show's or album's details by hand. The main profile's, as Fix match is: tidying the library,
/// not changing who sees what (the age rating isn't editable; kids access stays with Allow and Block).
/// </summary>
public static class ItemEditEndpoints
{
    private const int MaxGenres = 20;

    private static readonly string[] AlbumTypes =
        [AlbumKind.Album, AlbumKind.Ep, AlbumKind.SingleRelease, AlbumKind.Live, AlbumKind.Compilation, AlbumKind.Soundtrack, AlbumKind.Other];

    public static RouteGroupBuilder MapItemEditEndpoints(this RouteGroupBuilder api)
    {
        var item = api.MapGroup("/items/{id:guid}").WithTags("Edit details").RequireAuthorization(FlexarrClaims.MainProfilePolicy);

        item.MapPut("/details", async (Guid id, ItemEditRequest request, FlexarrDbContext db, CancellationToken ct) =>
        {
            if (Invalid(request) is { } problem)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["details"] = [problem] });
            }

            var entity = await db.MediaItems.Include(i => i.Genres)
                .FirstOrDefaultAsync(i => i.Id == id && (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series), ct);
            if (entity is null)
            {
                return Results.NotFound();
            }

            entity.Title = request.Title.Trim();
            entity.SortTitle = Blank(request.SortTitle)?.ToLowerInvariant() ?? SortTitle.For(entity.Title); // As typed, articles and all.
            entity.OriginalTitle = Blank(request.OriginalTitle) is { } original && original != entity.Title ? original : null;
            entity.Year = request.Year;
            entity.Overview = Blank(request.Overview);
            entity.Studio = Blank(request.Studio);
            await SetGenresAsync(db, entity, request.Genres ?? [], ct);
            entity.DetailsLocked = request.Locked;
            entity.MetadataUpdatedAt = DateTime.UtcNow; // Search and artwork addresses notice the change.
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithMetadata(new EditSizeLimit());

        item.MapPut("/album-details", async (Guid id, AlbumEditRequest request, FlexarrDbContext db, CancellationToken ct) =>
        {
            if (InvalidAlbum(request) is { } problem)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["album"] = [problem] });
            }

            var album = await db.MediaItems.Include(i => i.Genres).FirstOrDefaultAsync(i => i.Id == id && i.Kind == MediaKind.Album, ct);
            if (album is null)
            {
                return Results.NotFound();
            }

            Rename(album, request.Title.Trim());
            album.Year = request.Year;
            album.AlbumType = Blank(request.AlbumType);
            await SetGenresAsync(db, album, request.Genres ?? [], ct);
            album.DetailsLocked = request.Locked;
            album.MetadataUpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithMetadata(new EditSizeLimit());

        // Back to automatic: the details are unlocked and come from the sources again.
        item.MapDelete("/details", async (Guid id, FlexarrDbContext db, MetadataRefresher refresher, ILibraryScanQueue queue, CancellationToken ct) =>
        {
            var entity = await db.MediaItems.Include(i => i.Genres).Include(i => i.Credits)
                .FirstOrDefaultAsync(i => i.Id == id && (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series || i.Kind == MediaKind.Album), ct);
            if (entity is null)
            {
                return Results.NotFound();
            }

            entity.DetailsLocked = false;
            if (entity.Kind != MediaKind.Album)
            {
                await refresher.RefreshItemAsync(entity, ct);
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            }

            // An album: the tags' name back now (the refresh gives it Lidarr's or the match's again), then its folder is
            // read again, which refreshes its artist.
            if (entity.OriginalTitle is { } tagged)
            {
                entity.Title = tagged;
                entity.SortTitle = SortTitle.For(tagged);
                entity.OriginalTitle = null;
            }

            entity.MetadataUpdatedAt = null;
            await db.MediaItems.Where(i => i.Id == entity.ParentId).ExecuteUpdateAsync(u => u.SetProperty(i => i.MetadataUpdatedAt, (DateTime?)null), ct);
            await db.SaveChangesAsync(ct);
            if (entity.Path is { } path)
            {
                await queue.EnqueueAsync(new LibraryScanRequest(path, "Album details back to automatic"), ct);
            }

            return Results.NoContent();
        });

        return api;
    }

    /// <summary>
    /// Gives an album a new name, keeping the tags' spelling as its original title: the scanner files tracks by their
    /// tags, so it has to still recognise the album under that name.
    /// </summary>
    internal static void Rename(MediaItem album, string title)
    {
        if (title == album.Title)
        {
            return;
        }

        album.OriginalTitle ??= album.Title;
        if (album.OriginalTitle == title)
        {
            album.OriginalTitle = null;
        }

        album.Title = title;
        album.SortTitle = SortTitle.For(title);
    }

    internal static string? Invalid(ItemEditRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Title) || r.Title.Length > 500 || r.SortTitle?.Length > 500 || r.OriginalTitle?.Length > 500)
        {
            return "A title of up to 500 characters.";
        }

        if (!Names(r.Genres) || r.Studio?.Length > 200)
        {
            return "Up to 20 genres, and a studio or network, each up to 200 characters.";
        }

        if (r.Overview?.Length > 20_000)
        {
            return "A description of up to 20,000 characters.";
        }

        return r.Year is < 1800 or > 3000 ? "A year such as 1999." : null;
    }

    internal static string? InvalidAlbum(AlbumEditRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Title) || r.Title.Length > 500)
        {
            return "A title of up to 500 characters.";
        }

        if (!Names(r.Genres))
        {
            return "Up to 20 genres, each up to 200 characters.";
        }

        if (Blank(r.AlbumType) is { } type && !AlbumTypes.Contains(type))
        {
            return $"A type that's one of {string.Join(", ", AlbumTypes)}.";
        }

        return r.Year is < 1800 or > 3000 ? "A year such as 1999." : null;
    }

    private static bool Names(IReadOnlyList<string>? names) => names is null || (names.Count <= MaxGenres && names.All(n => n is not null && n.Length <= 200));

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task SetGenresAsync(FlexarrDbContext db, MediaItem item, IReadOnlyList<string> names, CancellationToken ct)
    {
        var wanted = names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var known = await db.Genres.ToListAsync(ct);
        item.Genres.Clear();
        foreach (var name in wanted)
        {
            var genre = known.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
            if (genre is null)
            {
                genre = new Genre { Name = name };
                db.Genres.Add(genre);
                known.Add(genre);
            }

            item.Genres.Add(genre);
        }
    }
}
