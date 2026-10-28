using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Endpoints;

/// <summary>A library requests can go to: one of the root folders of Seerr's Sonarr or Radarr, as Flexarr sees it.</summary>
/// <param name="IsDefault">Where Seerr adds this kind of title when it isn't told otherwise.</param>
public sealed record RequestDestination(Guid LibraryId, string Name, MediaKind Kind, SeerrDestination To, bool IsDefault);

/// <summary>A server Seerr sends one kind of request to, and its root folders.</summary>
public sealed record SeerrServer(MediaKind Kind, SeerrService Service, IReadOnlyList<SeerrRootFolder> RootFolders);

public static class RequestDestinations
{
    /// <summary>The film and TV libraries Seerr can add requests to, in the order Settings lists libraries.</summary>
    /// <remarks>Only libraries the database context shows: the current profile's, unless its filters are ignored.</remarks>
    public static async Task<IReadOnlyList<RequestDestination>> ListAsync(SeerrClient seerr, SeerrInstance instance, IReadOnlyList<PathMapping> mappings,
        IQueryable<Library> libraries, CancellationToken ct)
    {
        var candidates = await libraries.AsNoTracking().Include(l => l.Folders).Where(l => l.Type == LibraryType.Movies || l.Type == LibraryType.TvShows)
            .OrderBy(l => l.CreatedAt).ToListAsync(ct);
        if (candidates.Count == 0)
        {
            return [];
        }

        var servers = new List<SeerrServer>();
        foreach (var (kind, service) in new[] { (MediaKind.Series, "sonarr"), (MediaKind.Movie, "radarr") })
        {
            // 4K servers hold second copies, which aren't what a profile chooses between.
            foreach (var server in (await seerr.ServicesAsync(instance, service, ct)).Where(s => !s.Is4k))
            {
                servers.Add(new SeerrServer(kind, server, await seerr.RootFoldersAsync(instance, service, server.Id, ct)));
            }
        }

        return Match(servers, mappings, candidates);
    }

    /// <summary>
    /// Pairs each film and TV library with the root folder it's in (Seerr's paths translated by the path mappings). A
    /// library two servers share goes to the default one.
    /// </summary>
    public static IReadOnlyList<RequestDestination> Match(IEnumerable<SeerrServer> servers, IReadOnlyList<PathMapping> mappings, IEnumerable<Library> libraries)
    {
        var folders = new Dictionary<(MediaKind, string), (SeerrDestination To, bool IsDefault)>();
        foreach (var server in servers.OrderByDescending(s => s.Service.IsDefault))
        {
            var active = server.Service.ActiveDirectory is { } directory ? PathComparison.Key(directory) : null;
            foreach (var root in server.RootFolders)
            {
                folders.TryAdd((server.Kind, PathComparison.Key(PathMapper.Map(root.Path, mappings))),
                    (new SeerrDestination(server.Service.Id, root.Path), server.Service.IsDefault && PathComparison.Key(root.Path) == active));
            }
        }

        var destinations = new List<RequestDestination>();
        foreach (var library in libraries.Where(l => !RecordingsLibrary.Is(l)))
        {
            var kind = library.Type switch
            {
                LibraryType.Movies => MediaKind.Movie,
                LibraryType.TvShows => MediaKind.Series,
                _ => (MediaKind?)null,
            };
            if (kind is not { } k)
            {
                continue;
            }

            // A library with several folders goes to the one Seerr adds to by default, if it's one of them.
            (SeerrDestination To, bool IsDefault)? match = null;
            foreach (var folder in library.Folders)
            {
                if (folders.TryGetValue((k, PathComparison.Key(folder.Path)), out var m) && (match is null || (m.IsDefault && !match.Value.IsDefault)))
                {
                    match = m;
                }
            }

            if (match is { } found)
            {
                destinations.Add(new RequestDestination(library.Id, library.Name, k, found.To, found.IsDefault));
            }
        }

        return destinations;
    }

    /// <summary>
    /// Where the current profile's request for <paramref name="kind"/> goes: the library it chose, if it can still see
    /// it and Seerr can still add to it. Null leaves it to Seerr (no choice, or the choice is Seerr's default anyway).
    /// </summary>
    public static async Task<SeerrDestination?> ForRequestAsync(SeerrClient seerr, SeerrInstance instance, IReadOnlyList<PathMapping> mappings, FlexarrDbContext db,
        string profileId, MediaKind kind, CancellationToken ct)
    {
        var chosen = await db.Profiles.AsNoTracking().Where(p => p.Id == profileId)
            .Select(p => kind == MediaKind.Movie ? p.RequestFilmsLibraryId : p.RequestShowsLibraryId).FirstOrDefaultAsync(ct);
        if (chosen is not { } libraryId)
        {
            return null;
        }

        try
        {
            // The context's filters leave out libraries hidden from the profile, so a choice that's since been hidden is ignored.
            var destination = (await ListAsync(seerr, instance, mappings, db.Libraries.Where(l => l.Id == libraryId), ct)).FirstOrDefault(d => d.Kind == kind);
            return destination is { IsDefault: false } ? destination.To : null;
        }
        catch (HttpRequestException)
        {
            return null; // Seerr's default, then; the request itself says whether Seerr is there.
        }
    }
}
