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
        await ApplyAsync(services.GetRequiredService<FlexarrDbContext>(), profileId, ct);
    }

    /// <summary>Has <paramref name="db"/> show only what <paramref name="profileId"/> may see: its libraries, and its age limit.</summary>
    public static async Task ApplyAsync(FlexarrDbContext db, string profileId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var profile = await db.Profiles.Where(p => p.Id == profileId).Select(p => new { p.HiddenLibraryIds, p.MaxRatingAge, p.IsKids }).FirstOrDefaultAsync(ct);
        db.HiddenLibraryIds = profile?.HiddenLibraryIds ?? [];
        db.MaxRatingAge = profile?.MaxRatingAge;
        db.ServesKids = profile?.IsKids == true;
        if (db.ServesKids)
        {
            // A kids profile has no podcasts or Live TV: their libraries (podcasts, and the DVR's recordings) are hidden
            // from it like any it's kept out of.
            var kept = await db.Libraries.IgnoreQueryFilters()
                .Where(l => l.Type == LibraryType.Podcasts || (l.Type == LibraryType.TvShows && l.Name == RecordingsLibrary.Name))
                .Select(l => l.Id).ToListAsync(ct);
            db.HiddenLibraryIds = [.. db.HiddenLibraryIds.Union(kept)];
        }
        db.KidsLibraryIds = db.MaxRatingAge is null ? [] : await db.Libraries.IgnoreQueryFilters().Where(l => l.FineForKids).Select(l => l.Id).ToListAsync(ct);
    }

    /// <summary>
    /// Turns away profiles with an age limit: what these endpoints reach (Seerr, podcast directories, the DVR) has no
    /// ages Flexarr can check. Needs <see cref="ApplyLibraryVisibility"/> on an outer group.
    /// </summary>
    public static TBuilder NotForAgeLimited<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (context, next) =>
            context.HttpContext.RequestServices.GetRequiredService<FlexarrDbContext>().IsLimited()
                ? Results.Problem("That isn't available on a profile with an age limit.", statusCode: 403)
                : await next(context));
        return builder;
    }

    /// <summary>
    /// Turns away kids profiles: a child's app has no podcasts, watchlist or collections. Needs
    /// <see cref="ApplyLibraryVisibility"/> on an outer group.
    /// </summary>
    public static TBuilder NotForKids<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (context, next) =>
            context.HttpContext.RequestServices.GetRequiredService<FlexarrDbContext>().ServesKids
                ? Results.Problem("That isn't available on a kids profile.", statusCode: 403)
                : await next(context));
        return builder;
    }

    private static int _kidsSettings;

    /// <summary>
    /// Changes whenever what a profile with an age limit may see does (a limit, an Allow or Block, a library or channel
    /// fine for kids), for caches and open streams to start again.
    /// </summary>
    public static int KidsSettingsVersion => Volatile.Read(ref _kidsSettings);

    public static void KidsSettingsChanged() => Interlocked.Increment(ref _kidsSettings);

    /// <summary>The profile <paramref name="db"/> serves has an age limit.</summary>
    public static bool IsLimited(this FlexarrDbContext db) => db?.MaxRatingAge is not null;

    /// <summary>
    /// The kinds of title a profile is kept out of altogether: those where every library of that kind is hidden from
    /// it. Titles that aren't in the library yet (Discover, search, Coming soon, downloads) can't be traced to one
    /// library, so they're shown unless the profile can't see any library of their kind. Hiding one TV library of
    /// several (someone else's shows) still leaves new shows visible. A profile with an age limit is kept out of all of
    /// them: a title Flexarr hasn't got has no age it can check.
    /// </summary>
    public static async Task<IReadOnlySet<LibraryType>> HiddenTypesAsync(FlexarrDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.IsLimited())
        {
            return Enum.GetValues<LibraryType>().ToHashSet();
        }

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
            // A runtime and when it was added can pick out a film as surely as its title; what the watcher has seen of it too.
            ChildCount = 0, DurationSeconds = null, RuntimeMinutes = null, AddedAt = DateTime.UnixEpoch, UserData = new UserDataDto(0, false, 0, null, null),
        };
    }

    /// <summary>A title on another screen, as this context's profile may be shown it (see <see cref="Private"/>); null when it's gone.</summary>
    public static async Task<ItemSummaryDto?> PlayingAsync(FlexarrDbContext db, Guid itemId, string playingProfile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var item = await db.MediaItems.IgnoreQueryFilters().Where(i => i.Id == itemId && i.MissingSince == null)
            .Summaries(playingProfile).FirstOrDefaultAsync(ct);
        return item is not null && !await CanSeeAsync(db, item.LibraryId, itemId, ct) ? item.Private() : item;
    }

    /// <summary>Whether this context's profile may see item <paramref name="itemId"/> of library <paramref name="libraryId"/>.</summary>
    public static async Task<bool> CanSeeAsync(FlexarrDbContext db, Guid libraryId, Guid itemId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return !db.HiddenLibraryIds.Contains(libraryId) && (!db.IsLimited() || await db.MediaItems.AnyAsync(i => i.Id == itemId, ct));
    }

    /// <summary>The age limit's part of the context's filter, for items already in memory (see <see cref="FlexarrDbContext.MaxRatingAge"/>).</summary>
    public static bool AgeAllows(int? maxRatingAge, IReadOnlyCollection<Guid> kidsLibraryIds, Guid libraryId, int? ratingAge, KidsOverride kidsOverride)
    {
        ArgumentNullException.ThrowIfNull(kidsLibraryIds);
        return maxRatingAge is null || kidsOverride == KidsOverride.Allow
            || kidsOverride != KidsOverride.Block && (kidsLibraryIds.Contains(libraryId) || ratingAge <= maxRatingAge);
    }

    public static bool Hides(this IReadOnlySet<LibraryType> types, MediaKind kind) => kind switch
    {
        MediaKind.Movie => types.Contains(LibraryType.Movies),
        MediaKind.Series or MediaKind.Season or MediaKind.Episode => types.Contains(LibraryType.TvShows),
        MediaKind.Podcast or MediaKind.PodcastEpisode => types.Contains(LibraryType.Podcasts),
        _ => types.Contains(LibraryType.Music),
    };
}
