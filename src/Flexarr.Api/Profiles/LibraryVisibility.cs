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
    /// The kinds of library the profile has had one hidden of. Titles that aren't in the library yet (coming soon,
    /// Seerr's Discover) can't be traced to a library, so a profile kept out of any film library sees no such films.
    /// </summary>
    public static async Task<IReadOnlySet<LibraryType>> HiddenTypesAsync(FlexarrDbContext db, CancellationToken ct)
    {
        var hidden = db.HiddenLibraryIds;
        if (hidden.Count == 0)
        {
            return new HashSet<LibraryType>();
        }

        return (await db.Libraries.IgnoreQueryFilters().Where(l => hidden.Contains(l.Id)).Select(l => l.Type).ToListAsync(ct)).ToHashSet();
    }

    public static bool Hides(this IReadOnlySet<LibraryType> types, MediaKind kind) => kind switch
    {
        MediaKind.Movie => types.Contains(LibraryType.Movies),
        MediaKind.Series or MediaKind.Season or MediaKind.Episode => types.Contains(LibraryType.TvShows),
        _ => types.Contains(LibraryType.Music),
    };
}
