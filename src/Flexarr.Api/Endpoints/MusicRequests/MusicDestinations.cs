using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Endpoints.MusicRequests;

/// <summary>A music library requests can go to: a Lidarr root folder, as Flexarr sees it.</summary>
/// <param name="IsDefault">Where a request goes when the profile hasn't chosen: the first Lidarr's first root folder.</param>
public sealed record MusicDestination(Guid LibraryId, string Name, ArrInstance Lidarr, LidarrRootFolder Root, bool IsDefault);

/// <summary>One Lidarr and its root folders.</summary>
public sealed record LidarrRoots(ArrInstance Lidarr, IReadOnlyList<LidarrRootFolder> Folders);

/// <summary>Where a music request goes, and how Lidarr keeps it.</summary>
public sealed record MusicTarget(ArrInstance Lidarr, LidarrPlacement Placement);

public static class MusicDestinations
{
    /// <summary>Every Lidarr's root folders, leaving out a Lidarr that doesn't answer.</summary>
    public static async Task<IReadOnlyList<LidarrRoots>> RootsAsync(ArrClient client, IEnumerable<ArrInstance> lidarrs, CancellationToken ct)
    {
        var roots = new List<LidarrRoots>();
        foreach (var lidarr in lidarrs)
        {
            try
            {
                roots.Add(new LidarrRoots(lidarr, await client.GetLidarrRootFoldersAsync(lidarr, ct)));
            }
            catch (HttpRequestException)
            {
                // Its requests fail on their own with a reason; its libraries just aren't offered meanwhile.
            }
        }

        return roots;
    }

    /// <summary>Pairs each music library with the Lidarr root folder it is (Lidarr's paths translated by the path mappings).</summary>
    public static IReadOnlyList<MusicDestination> Match(IReadOnlyList<LidarrRoots> roots, IReadOnlyList<PathMapping> mappings, IEnumerable<Library> libraries)
    {
        var folders = new Dictionary<string, (ArrInstance Lidarr, LidarrRootFolder Root, bool IsDefault)>();
        var first = true;
        foreach (var lidarr in roots)
        {
            foreach (var root in lidarr.Folders)
            {
                folders.TryAdd(PathComparison.Key(PathMapper.Map(root.Path, mappings)), (lidarr.Lidarr, root, first));
                first = false;
            }
        }

        var destinations = new List<MusicDestination>();
        foreach (var library in libraries.Where(l => l.Type == LibraryType.Music))
        {
            (ArrInstance Lidarr, LidarrRootFolder Root, bool IsDefault)? match = null;
            foreach (var folder in library.Folders)
            {
                if (folders.TryGetValue(PathComparison.Key(folder.Path), out var m) && (match is null || (m.IsDefault && !match.Value.IsDefault)))
                {
                    match = m;
                }
            }

            if (match is { } found)
            {
                destinations.Add(new MusicDestination(library.Id, library.Name, found.Lidarr, found.Root, found.IsDefault));
            }
        }

        return destinations;
    }

    /// <summary>The music libraries the context shows (the current profile's, unless its filters are ignored) that requests can go to.</summary>
    public static async Task<IReadOnlyList<MusicDestination>> ListAsync(ArrClient client, IEnumerable<ArrInstance> lidarrs, IReadOnlyList<PathMapping> mappings,
        IQueryable<Library> libraries, CancellationToken ct)
    {
        var music = await libraries.AsNoTracking().Include(l => l.Folders).Where(l => l.Type == LibraryType.Music).OrderBy(l => l.CreatedAt).ToListAsync(ct);
        return music.Count == 0 ? [] : Match(await RootsAsync(client, lidarrs, ct), mappings, music);
    }

    /// <summary>
    /// Where the profile's music request goes: the library it chose, if it can still see it and Lidarr adds to it;
    /// otherwise Lidarr's default among the libraries it can see, else the first of those. Never a library hidden from
    /// it. Null with no problem when no Lidarr answers; null with a problem when none of its music libraries is a
    /// Lidarr root folder.
    /// </summary>
    public static async Task<(MusicTarget? Target, string? Problem)> ForRequestAsync(ArrClient client, IReadOnlyList<ArrInstance> lidarrs,
        IReadOnlyList<PathMapping> mappings, FlexarrDbContext db, string profileId, CancellationToken ct)
    {
        var roots = await RootsAsync(client, lidarrs, ct);
        if (!roots.Any(r => r.Folders.Count > 0))
        {
            return (null, null);
        }

        // The context's filters leave out libraries hidden from the profile.
        var visible = await db.Libraries.AsNoTracking().Include(l => l.Folders).Where(l => l.Type == LibraryType.Music).OrderBy(l => l.CreatedAt).ToListAsync(ct);
        var destinations = Match(roots, mappings, visible);
        if (destinations.Count == 0)
        {
            return (null, "None of your music libraries is one of Lidarr's root folders, so what it downloads wouldn't show here.");
        }

        var chosen = await db.Profiles.AsNoTracking().Where(p => p.Id == profileId).Select(p => p.RequestMusicLibraryId).FirstOrDefaultAsync(ct);
        var destination = destinations.FirstOrDefault(d => d.LibraryId == chosen) ?? destinations.FirstOrDefault(d => d.IsDefault) ?? destinations[0];
        return (new MusicTarget(destination.Lidarr, await PlacementAsync(client, destination.Lidarr, destination.Root, ct)), null);
    }

    /// <summary>
    /// The root folder's own default profiles, as Lidarr's Add dialog would pick; failing those, the ones most of this
    /// Lidarr's artists use, then the first it has.
    /// </summary>
    private static async Task<LidarrPlacement> PlacementAsync(ArrClient client, ArrInstance lidarr, LidarrRootFolder root, CancellationToken ct)
    {
        var quality = root.DefaultQualityProfileId;
        var metadata = root.DefaultMetadataProfileId;
        if (quality <= 0 || metadata <= 0)
        {
            var artists = await client.GetArtistsAsync(lidarr, ct);
            if (quality <= 0)
            {
                quality = MostUsed(artists.Select(a => a.QualityProfileId)) ?? (await client.GetQualityProfilesAsync(lidarr, ct)).Select(p => p.Id).FirstOrDefault();
            }

            if (metadata <= 0)
            {
                metadata = MostUsed(artists.Select(a => a.MetadataProfileId)) ?? (await client.GetMetadataProfilesAsync(lidarr, ct)).Select(p => p.Id).FirstOrDefault();
            }
        }

        return new LidarrPlacement(root.Path, quality, metadata);
    }

    private static int? MostUsed(IEnumerable<int> ids) =>
        ids.Where(i => i > 0).GroupBy(i => i).OrderByDescending(g => g.Count()).Select(g => (int?)g.Key).FirstOrDefault();
}
