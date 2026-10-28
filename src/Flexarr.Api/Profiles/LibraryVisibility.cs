using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Profiles;

/// <summary>Keeps libraries a profile has been kept out of from showing up anywhere it browses or plays.</summary>
public static class LibraryVisibility
{
    /// <summary>Applies the current profile's hidden libraries to the request's database context.</summary>
    public static RouteGroupBuilder ApplyLibraryVisibility(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            var services = context.HttpContext.RequestServices;
            var profileId = services.GetRequiredService<CurrentProfile>().Id;
            var db = services.GetRequiredService<FlexarrDbContext>();
            db.HiddenLibraryIds = await db.Profiles.Where(p => p.Id == profileId).Select(p => p.HiddenLibraryIds)
                .FirstOrDefaultAsync(context.HttpContext.RequestAborted) ?? [];
            return await next(context);
        });
        return group;
    }

    /// <summary>
    /// The kinds of title a profile is kept out of altogether: those where every library of that kind is hidden from
    /// it. Titles that aren't in the library yet (Discover, search, Coming soon, downloads) can't be traced to one
    /// library, so they're shown unless the profile can't see any library of their kind. Hiding one TV library of
    /// several (someone else's shows) still leaves new shows visible.
    /// </summary>
    public static async Task<IReadOnlySet<LibraryType>> HiddenTypesAsync(FlexarrDbContext db, CancellationToken ct)
    {
        var hidden = db.HiddenLibraryIds;
        if (hidden.Count == 0)
        {
            return new HashSet<LibraryType>();
        }

        // Home videos are never upcoming or requested, so they don't keep TV that is from being hidden.
        var libraries = await db.Libraries.IgnoreQueryFilters().Where(l => l.Type != LibraryType.HomeVideos).Select(l => new { l.Id, l.Type }).ToListAsync(ct);
        return libraries.GroupBy(l => l.Type).Where(g => g.All(l => hidden.Contains(l.Id))).Select(g => g.Key).ToHashSet();
    }

    public static bool Hides(this IReadOnlySet<LibraryType> types, MediaKind kind) => kind switch
    {
        MediaKind.Movie => types.Contains(LibraryType.Movies),
        MediaKind.Series or MediaKind.Season or MediaKind.Episode => types.Contains(LibraryType.TvShows),
        MediaKind.Podcast or MediaKind.PodcastEpisode => types.Contains(LibraryType.Podcasts),
        _ => types.Contains(LibraryType.Music),
    };
}
