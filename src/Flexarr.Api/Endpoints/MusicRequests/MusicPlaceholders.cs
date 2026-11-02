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
    /// name). It goes in the music library (of those the profile sees) holding the Lidarr artist's folder, else in
    /// <paramref name="libraryId"/>; it takes the Lidarr artist's id only when their folder is in its library, so
    /// deleting it can never reach files elsewhere. Returns the artist's library id, new or found.
    /// </summary>
    /// <param name="lidarrPath">The Lidarr artist's folder, mapped to this server's paths.</param>
    public static async Task<Guid?> AddAsync(FlexarrDbContext db, Guid libraryId, string name, string? musicBrainzId, string? photo, string? lidarrName, int? lidarrId,
        string? lidarrPath, CancellationToken ct)
    {
        if (await FindAsync(db, name, musicBrainzId, ct) is { } had)
        {
            return had;
        }

        var holding = lidarrPath is null ? null : await LibraryHoldingAsync(db, lidarrPath, ct);
        libraryId = holding ?? libraryId;
        if (libraryId == Guid.Empty)
        {
            return null;
        }

        var managed = holding is not null && lidarrId is > 0;

        var artist = new MediaItem
        {
            LibraryId = libraryId,
            Kind = MediaKind.Artist,
            Title = name,
            SortTitle = SortTitle.For(name),
            MusicBrainzId = musicBrainzId,
            PosterUrl = ArtworkUrl.IsHttps(photo) ? photo : null,
            Placeholder = true,
            ArrId = managed ? lidarrId : null,
            MetadataSource = managed ? lidarrName : null,
        };
        db.MediaItems.Add(artist);
        await db.SaveChangesAsync(ct);
        MusicMatcher.ForgetLibrary();
        return artist.Id;
    }

    /// <summary>Removes the artist's placeholder, if they have one that's still empty, in a library the profile can see.</summary>
    public static async Task RemoveAsync(FlexarrDbContext db, string name, string? musicBrainzId, CancellationToken ct)
    {
        var key = MusicNames.Normalise(name);
        if (key.Length == 0 && musicBrainzId is null)
        {
            return;
        }

        var musicLibraries = db.Libraries.Where(l => l.Type == LibraryType.Music).Select(l => l.Id);
        var candidates = await db.MediaItems
            .Where(i => i.Kind == MediaKind.Artist && i.Placeholder && musicLibraries.Contains(i.LibraryId) && !i.Children.Any())
            .Select(i => new { i.Id, i.Title, i.MusicBrainzId }).ToListAsync(ct);
        var ids = candidates.Where(c => (musicBrainzId is not null && c.MusicBrainzId == musicBrainzId) || (key.Length > 0 && MusicNames.Normalise(c.Title) == key))
            .Select(c => c.Id).ToList();
        if (ids.Count > 0)
        {
            await db.MediaItems.Where(i => ids.Contains(i.Id)).ExecuteDeleteAsync(ct);
            MusicMatcher.ForgetLibrary();
        }
    }

    /// <summary>The music library (of those the profile sees) whose folders hold this path; null when none does.</summary>
    public static async Task<Guid?> LibraryHoldingAsync(FlexarrDbContext db, string localPath, CancellationToken ct)
    {
        var libraries = await db.Libraries.AsNoTracking().Include(l => l.Folders).Where(l => l.Type == LibraryType.Music).ToListAsync(ct);
        return libraries.FirstOrDefault(l => l.Folders.Any(f => Flexarr.Core.Paths.PathComparison.IsUnder(localPath, f.Path)))?.Id;
    }

    /// <summary>The artist in any music library (seen by this profile or not), by MusicBrainz id, else by name.</summary>
    private static async Task<Guid?> FindAsync(FlexarrDbContext db, string name, string? musicBrainzId, CancellationToken ct)
    {
        var musicLibraries = db.Libraries.IgnoreQueryFilters().Where(l => l.Type == LibraryType.Music).Select(l => l.Id);
        var artists = db.MediaItems.IgnoreQueryFilters().AsNoTracking().Where(i => i.Kind == MediaKind.Artist && musicLibraries.Contains(i.LibraryId));
        if (musicBrainzId is not null && await artists.Where(a => a.MusicBrainzId == musicBrainzId).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct) is { } byId)
        {
            return byId;
        }

        var key = MusicNames.Normalise(name);
        return (await artists.Select(i => new { i.Id, i.Title }).ToListAsync(ct)).FirstOrDefault(a => MusicNames.Normalise(a.Title) == key)?.Id;
    }
}
