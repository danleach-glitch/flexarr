using Flexarr.Api.Endpoints;
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
            await ApplyAsync(context.HttpContext.RequestServices, context.HttpContext.RequestAborted);
            return await next(context);
        });
        return group;
    }

    /// <summary>
    /// Applies the current profile's hidden libraries to the database context in <paramref name="services"/>. For
    /// requests outside the <c>/api/v1</c> group, such as the assistant's tool calls at <c>/api/v1/mcp</c>.
    /// </summary>
    public static async Task ApplyAsync(IServiceProvider services, CancellationToken ct)
    {
        var profileId = services.GetRequiredService<CurrentProfile>().Id;
        var db = services.GetRequiredService<FlexarrDbContext>();
        db.HiddenLibraryIds = await db.Profiles.Where(p => p.Id == profileId).Select(p => p.HiddenLibraryIds).FirstOrDefaultAsync(ct) ?? [];
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

    /// <summary>What a profile is told about a title from a library hidden from it, playing on another screen.</summary>
    public const string PrivateTitle = "Something private";

    /// <summary>
    /// <paramref name="item"/> as a profile that can't see its library is shown it in the household's now playing:
    /// that something is playing, and nothing about what (no title, ids, artwork or ratings).
    /// </summary>
    public static ItemSummaryDto Private(this ItemSummaryDto item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item with
        {
            Id = Guid.Empty, LibraryId = Guid.Empty, ParentId = null, Title = PrivateTitle, Year = null, SeasonNumber = null, EpisodeNumber = null,
            EpisodeNumberEnd = null, AirDate = null, Resolution = null, Artwork = new ArtworkDto(null, null, null, null), AccentColor = null,
            CommunityRating = null, CriticRating = null, ContentRating = null, ReleaseDate = null, SeriesTitle = null, DiscNumber = null,
            TrackNumber = null, SeriesId = null, AlbumType = null, TrackArtist = null,
        };
    }

    /// <summary>A title on another screen, as this context's profile may be shown it (see <see cref="Private"/>); null when it's gone.</summary>
    public static async Task<ItemSummaryDto?> PlayingAsync(FlexarrDbContext db, Guid itemId, string playingProfile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var item = await db.MediaItems.IgnoreQueryFilters().Where(i => i.Id == itemId && i.MissingSince == null)
            .Summaries(playingProfile).FirstOrDefaultAsync(ct);
        return item is not null && db.HiddenLibraryIds.Contains(item.LibraryId) ? item.Private() : item;
    }

    public static bool Hides(this IReadOnlySet<LibraryType> types, MediaKind kind) => kind switch
    {
        MediaKind.Movie => types.Contains(LibraryType.Movies),
        MediaKind.Series or MediaKind.Season or MediaKind.Episode => types.Contains(LibraryType.TvShows),
        MediaKind.Podcast or MediaKind.PodcastEpisode => types.Contains(LibraryType.Podcasts),
        _ => types.Contains(LibraryType.Music),
    };
}
