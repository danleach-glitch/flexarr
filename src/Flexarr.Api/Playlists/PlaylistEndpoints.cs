using Flexarr.Api.Endpoints;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Playlists;

/// <param name="Covers">Up to four of its tracks' album covers, first tracks first, for artwork made of them.</param>
/// <param name="Cover">A cover chosen for it (an album's), when one was; shown instead of the mosaic.</param>
public sealed record PlaylistSummaryDto(
    Guid Id,
    string Name,
    int TrackCount,
    double DurationSeconds,
    IReadOnlyList<string> Covers,
    string? Cover,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>A place in a playlist: the entry (what's moved or removed) and the track in it.</summary>
public sealed record PlaylistTrackDto(int EntryId, ItemSummaryDto Track);

public sealed record PlaylistDetailDto(PlaylistSummaryDto Summary, IReadOnlyList<PlaylistTrackDto> Tracks);

/// <param name="ItemIds">Tracks, albums or artists to start it with; albums and artists bring all their tracks.</param>
public sealed record PlaylistCreateRequest(string? Name = null, IReadOnlyList<Guid>? ItemIds = null);

/// <param name="CoverItemId">An album or track whose cover stands for the playlist; <paramref name="ClearCover"/> goes back to the mosaic.</param>
public sealed record PlaylistUpdateRequest(string? Name = null, Guid? CoverItemId = null, bool? ClearCover = null);

/// <param name="ItemIds">Tracks, albums or artists, in the order to add them; albums and artists bring all their tracks.</param>
/// <param name="SkipDuplicates">Leave out tracks the playlist already has.</param>
public sealed record PlaylistAddRequest(IReadOnlyList<Guid> ItemIds, bool SkipDuplicates = false);

public sealed record PlaylistAddedDto(int Added, PlaylistSummaryDto Summary);

/// <param name="Position">Where the entry goes, from 0; beyond the end means last.</param>
public sealed record PlaylistMoveRequest(int Position);

/// <summary>
/// A profile's playlists. Every call is for the profile making it: nobody sees or changes anyone else's, and another
/// profile's playlist is simply not found.
/// </summary>
public static class PlaylistEndpoints
{
    public const string HomeRowId = "music-playlists";

    public static RouteGroupBuilder MapPlaylistEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/playlists").WithTags("Playlists");

        group.MapGet("/", async (FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
            await SummariesAsync(db, profile.Id, ct));

        group.MapGet("/{id:guid}", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (await Mine(db, profile).FirstOrDefaultAsync(p => p.Id == id, ct) is not { } playlist)
            {
                return Results.NotFound();
            }

            return Results.Ok(await DetailAsync(db, profile.Id, playlist, ct));
        });

        group.MapPost("/", async (PlaylistCreateRequest request, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (Name(request.Name) is not { } name)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Give the playlist a name."] });
            }

            var tracks = await TracksOfAsync(db, request.ItemIds ?? [], ct);
            if (tracks is null)
            {
                return Results.Problem("Only tracks, albums and artists can go in a playlist.", statusCode: 400);
            }

            var playlist = new Playlist { ProfileId = profile.Id, Name = name };
            playlist.Items.AddRange(tracks.Select((t, i) => new PlaylistItem { MediaItemId = t, Position = i }));
            db.Playlists.Add(playlist);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/playlists/{playlist.Id}", await SummaryAsync(db, playlist, ct));
        });

        group.MapPut("/{id:guid}", async (Guid id, PlaylistUpdateRequest request, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (await Mine(db, profile).FirstOrDefaultAsync(p => p.Id == id, ct) is not { } playlist)
            {
                return Results.NotFound();
            }

            if (request.Name is not null && Name(request.Name) is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Give the playlist a name."] });
            }

            if (request.CoverItemId is { } cover)
            {
                if (!await db.MediaItems.AnyAsync(i => i.Id == cover && (i.Kind == MediaKind.Album || i.Kind == MediaKind.Track), ct))
                {
                    return Results.Problem("The cover has to be an album's or a track's.", statusCode: 400);
                }

                playlist.CoverItemId = cover;
            }
            else if (request.ClearCover == true)
            {
                playlist.CoverItemId = null;
            }

            playlist.Name = Name(request.Name) ?? playlist.Name;
            playlist.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(await SummaryAsync(db, playlist, ct));
        });

        group.MapDelete("/{id:guid}", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (await Mine(db, profile).FirstOrDefaultAsync(p => p.Id == id, ct) is not { } playlist)
            {
                return Results.NotFound();
            }

            db.Playlists.Remove(playlist);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Appends. Adding an album adds its tracks in album order; adding an artist, every track they have.
        group.MapPost("/{id:guid}/items", async (Guid id, PlaylistAddRequest request, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (await Mine(db, profile).FirstOrDefaultAsync(p => p.Id == id, ct) is not { } playlist)
            {
                return Results.NotFound();
            }

            var tracks = await TracksOfAsync(db, request.ItemIds, ct);
            if (tracks is null)
            {
                return Results.Problem("Only tracks, albums and artists can go in a playlist.", statusCode: 400);
            }

            var existing = await db.PlaylistItems.Where(i => i.PlaylistId == id).Select(i => new { i.MediaItemId, i.Position }).ToListAsync(ct);
            if (request.SkipDuplicates)
            {
                var had = existing.Select(e => e.MediaItemId).ToHashSet();
                tracks = tracks.Where(had.Add).ToList();
            }

            var next = existing.Count == 0 ? 0 : existing.Max(e => e.Position) + 1;
            db.PlaylistItems.AddRange(tracks.Select((t, i) => new PlaylistItem { PlaylistId = id, MediaItemId = t, Position = next + i }));
            if (tracks.Count > 0)
            {
                playlist.UpdatedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new PlaylistAddedDto(tracks.Count, await SummaryAsync(db, playlist, ct)));
        });

        group.MapDelete("/{id:guid}/items/{entryId:int}", async (Guid id, int entryId, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (await Mine(db, profile).FirstOrDefaultAsync(p => p.Id == id, ct) is not { } playlist)
            {
                return Results.NotFound();
            }

            var items = await db.PlaylistItems.Where(i => i.PlaylistId == id).OrderBy(i => i.Position).ToListAsync(ct);
            if (items.FirstOrDefault(i => i.Id == entryId) is not { } entry)
            {
                return Results.NotFound();
            }

            items.Remove(entry);
            db.PlaylistItems.Remove(entry);
            Renumber(items);
            playlist.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapPut("/{id:guid}/items/{entryId:int}/position", async (Guid id, int entryId, PlaylistMoveRequest request, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (await Mine(db, profile).FirstOrDefaultAsync(p => p.Id == id, ct) is not { } playlist)
            {
                return Results.NotFound();
            }

            var items = await db.PlaylistItems.Where(i => i.PlaylistId == id).OrderBy(i => i.Position).ToListAsync(ct);
            if (items.FirstOrDefault(i => i.Id == entryId) is not { } entry)
            {
                return Results.NotFound();
            }

            items.Remove(entry);
            items.Insert(Math.Clamp(request.Position, 0, items.Count), entry);
            Renumber(items);
            playlist.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        return api;
    }

    /// <summary>The "Your playlists" row for Home and a music library's Browse, or null when the profile has none.</summary>
    public static async Task<HomeRowDto?> HomeRowAsync(FlexarrDbContext db, string profileId, int size, CancellationToken ct)
    {
        var playlists = await SummariesAsync(db, profileId, ct);
        return playlists.Count == 0 ? null : new HomeRowDto(HomeRowId, "Your playlists", "playlists", [], "/playlists", playlists.Take(size).ToList());
    }

    private static IQueryable<Playlist> Mine(FlexarrDbContext db, CurrentProfile profile)
    {
        var me = profile.Id;
        return db.Playlists.Where(p => p.ProfileId == me);
    }

    private static string? Name(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed[..Math.Min(trimmed.Length, 120)];
    }

    /// <summary>Positions from 0 without gaps, in the order given.</summary>
    private static void Renumber(List<PlaylistItem> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            items[i].Position = i;
        }
    }

    /// <summary>
    /// The tracks <paramref name="ids"/> stand for, in the order asked: a track is itself, an album its tracks in
    /// order, an artist every track they have (albums oldest first). Null when an id isn't a track, album or artist.
    /// </summary>
    private static async Task<List<Guid>?> TracksOfAsync(FlexarrDbContext db, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        var kinds = await db.MediaItems.Where(i => wanted.Contains(i.Id)).Select(i => new { i.Id, i.Kind }).ToDictionaryAsync(i => i.Id, i => i.Kind, ct);
        if (wanted.Any(id => !kinds.TryGetValue(id, out var kind) || kind is not (MediaKind.Track or MediaKind.Album or MediaKind.Artist)))
        {
            return null;
        }

        var tracks = await db.MediaItems
            .Where(t => t.Kind == MediaKind.Track && (wanted.Contains(t.Id) || wanted.Contains(t.ParentId!.Value) || wanted.Contains(t.Parent!.ParentId!.Value)))
            .Select(t => new { t.Id, AlbumId = t.ParentId!.Value, ArtistId = t.Parent!.ParentId!.Value, AlbumYear = t.Parent.Year, AlbumSort = t.Parent.SortTitle, t.SortTitle })
            .ToListAsync(ct);
        var inOrder = tracks.OrderBy(t => t.AlbumYear == null).ThenBy(t => t.AlbumYear).ThenBy(t => t.AlbumSort, StringComparer.Ordinal).ThenBy(t => t.SortTitle, StringComparer.Ordinal).ToList();

        var result = new List<Guid>();
        foreach (var id in ids)
        {
            result.AddRange(kinds[id] switch
            {
                MediaKind.Track => inOrder.Where(t => t.Id == id).Select(t => t.Id),
                MediaKind.Album => inOrder.Where(t => t.AlbumId == id).Select(t => t.Id),
                _ => inOrder.Where(t => t.ArtistId == id).Select(t => t.Id),
            });
        }

        return result;
    }

    private static async Task<PlaylistDetailDto> DetailAsync(FlexarrDbContext db, string profileId, Playlist playlist, CancellationToken ct)
    {
        // Tracks in libraries this profile can't see drop out through the library filter on MediaItems.
        var entries = await db.PlaylistItems.Where(i => i.PlaylistId == playlist.Id && i.MediaItem != null).OrderBy(i => i.Position)
            .Select(i => new { i.Id, i.MediaItemId }).ToListAsync(ct);
        var ids = entries.Select(e => e.MediaItemId).Distinct().ToList();
        var tracks = await db.MediaItems.Where(i => ids.Contains(i.Id)).Summaries(profileId).ToDictionaryAsync(i => i.Id, ct);
        var inOrder = entries.Where(e => tracks.ContainsKey(e.MediaItemId)).Select(e => new PlaylistTrackDto(e.Id, tracks[e.MediaItemId])).ToList();
        return new PlaylistDetailDto((await SummariesAsync(db, [playlist], ct))[0], inOrder);
    }

    private static async Task<PlaylistSummaryDto> SummaryAsync(FlexarrDbContext db, Playlist playlist, CancellationToken ct) =>
        (await SummariesAsync(db, [playlist], ct))[0];

    /// <summary>Every playlist of the profile, most recently changed first.</summary>
    public static async Task<List<PlaylistSummaryDto>> SummariesAsync(FlexarrDbContext db, string profileId, CancellationToken ct) =>
        await SummariesAsync(db, await db.Playlists.Where(p => p.ProfileId == profileId).OrderByDescending(p => p.UpdatedAt).ThenBy(p => p.Name).ToListAsync(ct), ct);

    /// <summary>
    /// What a list of playlists shows (count, length and four covers each) without summarising every track: one query
    /// over the entries of them all, one for any chosen covers.
    /// </summary>
    private static async Task<List<PlaylistSummaryDto>> SummariesAsync(FlexarrDbContext db, IReadOnlyList<Playlist> playlists, CancellationToken ct)
    {
        var ids = playlists.Select(p => p.Id).ToList();
        var entries = ids.Count == 0 ? []
            : await db.PlaylistItems.Where(i => ids.Contains(i.PlaylistId) && i.MediaItem != null)
                .Select(i => new
                {
                    i.PlaylistId,
                    i.Position,
                    Duration = i.MediaItem!.Files.Max(f => f.DurationSeconds),
                    // A track's cover is its album's, unless it has one of its own.
                    CoverOf = i.MediaItem.PosterUrl != null ? i.MediaItem.Id : i.MediaItem.Parent!.PosterUrl != null ? i.MediaItem.ParentId : null,
                    CoverVersion = i.MediaItem.PosterUrl != null ? i.MediaItem.MetadataUpdatedAt : i.MediaItem.Parent!.MetadataUpdatedAt,
                    CoverChanged = i.MediaItem.PosterUrl != null ? i.MediaItem.ArtworkChangedAt : i.MediaItem.Parent!.ArtworkChangedAt,
                })
                .ToListAsync(ct);
        var byPlaylist = entries.ToLookup(e => e.PlaylistId);

        var coverIds = playlists.Where(p => p.CoverItemId != null).Select(p => p.CoverItemId!.Value).Distinct().ToList();
        var covers = coverIds.Count == 0 ? []
            : await db.MediaItems.Where(i => coverIds.Contains(i.Id) && i.PosterUrl != null).Select(i => new { i.Id, i.MetadataUpdatedAt, i.ArtworkChangedAt }).ToDictionaryAsync(i => i.Id, ct);

        return playlists.Select(p =>
        {
            var items = byPlaylist[p.Id].OrderBy(e => e.Position).ToList();
            var mosaic = items.Where(e => e.CoverOf != null).DistinctBy(e => e.CoverOf).Take(4).Select(e => Poster(e.CoverOf!.Value, ArtworkUrls.Version(e.CoverVersion, e.CoverChanged))).ToList();
            var cover = p.CoverItemId is { } id && covers.TryGetValue(id, out var c) ? Poster(id, ArtworkUrls.Version(c.MetadataUpdatedAt, c.ArtworkChangedAt)) : null;
            return new PlaylistSummaryDto(p.Id, p.Name, items.Count, items.Sum(e => e.Duration ?? 0), mosaic, cover, p.CreatedAt, p.UpdatedAt);
        }).ToList();
    }

    private static string Poster(Guid id, long version) =>
        ArtworkUrls.For(id, version, true, false, false, false).Poster!;
}
