using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Endpoints.MusicRequests;

/// <summary>
/// Followed artists the library has nothing of yet, kept as artists with no albums (<see cref="MediaItem.Placeholder"/>)
/// so they show in the Artists list straight away. The scanner fills one in when their first album arrives.
/// </summary>
public static class MusicPlaceholders
{
    /// <summary>
    /// Adds the artist to the library as a placeholder, unless a music library already has them (by MusicBrainz id or
    /// name). Returns the artist's library id, new or found.
    /// </summary>
    public static async Task<Guid?> AddAsync(FlexarrDbContext db, Guid libraryId, string name, string? musicBrainzId, string? photo, string? lidarrName, int? lidarrId,
        CancellationToken ct)
    {
        if (libraryId == Guid.Empty)
        {
            return null;
        }

        if (await FindAsync(db, name, musicBrainzId, ct) is { } had)
        {
            return had;
        }

        var artist = new MediaItem
        {
            LibraryId = libraryId,
            Kind = MediaKind.Artist,
            Title = name,
            SortTitle = SortTitle.For(name),
            MusicBrainzId = musicBrainzId,
            PosterUrl = ArtworkUrl.IsHttps(photo) ? photo : null,
            Placeholder = true,
            ArrId = lidarrId is > 0 ? lidarrId : null,
            MetadataSource = lidarrId is > 0 ? lidarrName : null,
        };
        db.MediaItems.Add(artist);
        await db.SaveChangesAsync(ct);
        MusicMatcher.ForgetLibrary();
        return artist.Id;
    }

    /// <summary>Removes the artist's placeholder, if they have one that's still empty.</summary>
    public static async Task RemoveAsync(FlexarrDbContext db, string name, string? musicBrainzId, CancellationToken ct)
    {
        var key = MusicNames.Normalise(name);
        var candidates = await db.MediaItems.IgnoreQueryFilters()
            .Where(i => i.Kind == MediaKind.Artist && i.Placeholder && !i.Children.Any())
            .Select(i => new { i.Id, i.Title, i.MusicBrainzId }).ToListAsync(ct);
        var ids = candidates.Where(c => (musicBrainzId is not null && c.MusicBrainzId == musicBrainzId) || MusicNames.Normalise(c.Title) == key).Select(c => c.Id).ToList();
        if (ids.Count > 0)
        {
            await db.MediaItems.IgnoreQueryFilters().Where(i => ids.Contains(i.Id)).ExecuteDeleteAsync(ct);
            MusicMatcher.ForgetLibrary();
        }
    }

    /// <summary>The artist in any music library (seen by this profile or not), by MusicBrainz id, else by name.</summary>
    private static async Task<Guid?> FindAsync(FlexarrDbContext db, string name, string? musicBrainzId, CancellationToken ct)
    {
        var musicLibraries = db.Libraries.IgnoreQueryFilters().Where(l => l.Type == LibraryType.Music).Select(l => l.Id);
        var artists = await db.MediaItems.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.Kind == MediaKind.Artist && musicLibraries.Contains(i.LibraryId))
            .Select(i => new { i.Id, i.Title, i.MusicBrainzId }).ToListAsync(ct);
        var key = MusicNames.Normalise(name);
        return (musicBrainzId is null ? null : artists.FirstOrDefault(a => a.MusicBrainzId == musicBrainzId)?.Id)
            ?? artists.FirstOrDefault(a => MusicNames.Normalise(a.Title) == key)?.Id;
    }
}
