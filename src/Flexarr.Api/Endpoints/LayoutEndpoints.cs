using System.Text.Json;
using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Endpoints;

public sealed record RowChoiceDto(string Id, string Label, bool Enabled);

/// <param name="Combinable">Whether there are two film libraries or two TV ones, so combining them changes anything.</param>
/// <param name="Music">A music library's Browse rows; null without a music library (and from servers before 0.58).</param>
/// <param name="Audiobooks">An audiobooks library's shelves; null without an audiobooks library (and from servers before 0.58).</param>
public sealed record LayoutDto(IReadOnlyList<RowChoiceDto> Home, IReadOnlyList<RowChoiceDto> Movies, IReadOnlyList<RowChoiceDto> Shows, bool CombineLibraries, bool Combinable,
    IReadOnlyList<RowChoiceDto>? Music = null, IReadOnlyList<RowChoiceDto>? Audiobooks = null);

/// <param name="CombineLibraries">Left out (by apps from before it), the setting stays as it is.</param>
/// <param name="Music">Left out, music's rows stay as they are.</param>
/// <param name="Audiobooks">Left out, the audiobook shelves stay as they are.</param>
public sealed record LayoutUpdate(IReadOnlyList<RowSetting> Home, IReadOnlyList<RowSetting> Movies, IReadOnlyList<RowSetting> Shows, bool? CombineLibraries = null,
    IReadOnlyList<RowSetting>? Music = null, IReadOnlyList<RowSetting>? Audiobooks = null);

/// <summary>
/// Choosing which rows Home and Browse show, and in what order (Settings › Home and browse): each profile's own. A
/// profile that hasn't chosen has the server's layout in config.json, as every profile shared before 0.58.
/// </summary>
public static class LayoutEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapLayoutEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/settings/layout").WithTags("Settings");

        group.MapGet("/", async (FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, Profiles.CurrentProfile profile, CancellationToken ct) =>
            await LayoutAsync(db, options.CurrentValue, await ForProfileAsync(db, options.CurrentValue, profile.Id, ct), profile.Id, ct));

        // Answers with the layout as saved: combining changes which rows there are.
        group.MapPut("/", async (LayoutUpdate update, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, Profiles.CurrentProfile profile, CancellationToken ct) =>
        {
            if (Invalid(update) is { } problem)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["rows"] = [problem] });
            }

            if (await db.Profiles.FindAsync([profile.Id], ct) is not { } me)
            {
                return Results.NotFound();
            }

            if (me.IsKids)
            {
                return Results.Problem("A kids profile has the standard Home.", statusCode: 403);
            }

            var before = Parse(me.Layout) ?? options.CurrentValue.Layout;
            var saved = new LayoutOptions
            {
                Home = [.. update.Home],
                Movies = [.. update.Movies],
                Shows = [.. update.Shows],
                Music = update.Music is null ? before.Music : [.. update.Music],
                Audiobooks = update.Audiobooks is null ? before.Audiobooks : [.. update.Audiobooks],
                CombineLibraries = update.CombineLibraries ?? before.CombineLibraries,
            };
            me.Layout = JsonSerializer.Serialize(saved, Json);
            await db.SaveChangesAsync(ct);
            return Results.Ok(await LayoutAsync(db, options.CurrentValue, saved, profile.Id, ct));
        });

        return api;
    }

    /// <summary>
    /// The profile's own layout, or the server's while it hasn't chosen one. A kids profile always has the standard
    /// one: every row a child can have, in the built-in order.
    /// </summary>
    public static async Task<LayoutOptions> ForProfileAsync(FlexarrDbContext db, FlexarrOptions options, string profileId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);
        var saved = await db.Profiles.AsNoTracking().Where(p => p.Id == profileId).Select(p => new { p.Layout, p.IsKids }).FirstOrDefaultAsync(ct);
        if (saved?.IsKids == true)
        {
            return new LayoutOptions();
        }

        return Parse(saved?.Layout) ?? options.Layout;
    }

    /// <summary>Home's rows a new profile starts with switched off: the ones that mostly repeat a library's own page.</summary>
    private static readonly HashSet<string> StartsOff = new(StringComparer.Ordinal)
    {
        Playlists.PlaylistEndpoints.HomeRowId, "music-recent", "music-top", "music-artists", "music-new",
        Books.BookRows.ContinueRowId, Books.BookRows.NewRowId, "gems",
    };

    /// <summary>
    /// What a new profile starts with, as JSON for <see cref="Core.Media.Profile.Layout"/>: Home's rows in the built-in
    /// order with a few switched off, and every Browse row on.
    /// </summary>
    public static async Task<string> StartingLayoutAsync(FlexarrDbContext db, FlexarrOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);
        var all = await db.Libraries.IgnoreQueryFilters().OrderBy(l => l.Name).ToListAsync(ct);
        var libraries = all.Where(l => l.Type is (LibraryType.Movies or LibraryType.TvShows or LibraryType.HomeVideos)
            && !(options.LiveTv?.Account is not null && RecordingsLibrary.Is(l))).ToList();
        var combine = options.Layout.CombineLibraries;
        var home = HomeRows(libraries, combine, all.Any(l => l.Type == LibraryType.Music), all.Any(l => l.Type == LibraryType.Audiobooks),
            all.Any(l => l.Type == LibraryType.Podcasts));
        // Music and book rows a server doesn't have yet are switched off too, ready for when it does.
        var off = StartsOff.Where(id => home.All(r => r.Id != id)).Select(id => new RowSetting { Id = id, Enabled = false });
        var layout = new LayoutOptions
        {
            Home = [.. home.Select(r => new RowSetting { Id = r.Id, Enabled = !StartsOff.Contains(r.Id) }), .. off],
            CombineLibraries = combine,
        };
        return JsonSerializer.Serialize(layout, Json);
    }

    /// <summary>The most rows one list may hold: far more than there are, and a cap on what a profile can store.</summary>
    private const int MaxRows = 200;

    private const int MaxIdLength = 128;

    /// <summary>Why a save can't be taken, or null: every list there, and each row a short id.</summary>
    private static string? Invalid(LayoutUpdate update)
    {
        IReadOnlyList<RowSetting>?[] required = [update.Home, update.Movies, update.Shows];
        if (required.Any(l => l is null))
        {
            return "Send the home, movies and shows rows.";
        }

        return required.Concat([update.Music, update.Audiobooks]).OfType<IReadOnlyList<RowSetting>>()
            .Any(l => l.Count > MaxRows || l.Any(r => r is null || string.IsNullOrWhiteSpace(r.Id) || r.Id.Length > MaxIdLength))
            ? $"Each list takes up to {MaxRows} rows, each with an id of up to {MaxIdLength} characters."
            : null;
    }

    private static LayoutOptions? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            // Rows without an id (saved before saves were checked) are left out rather than breaking Home.
            return JsonSerializer.Deserialize<LayoutOptions>(json, Json) is { } layout
                ? new LayoutOptions
                {
                    Home = Valid(layout.Home),
                    Movies = Valid(layout.Movies),
                    Shows = Valid(layout.Shows),
                    Music = Valid(layout.Music),
                    Audiobooks = Valid(layout.Audiobooks),
                    CombineLibraries = layout.CombineLibraries,
                }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }

        static List<RowSetting> Valid(List<RowSetting>? rows) => [.. (rows ?? []).Where(r => r is not null && !string.IsNullOrEmpty(r.Id))];
    }

    private static async Task<LayoutDto> LayoutAsync(FlexarrDbContext db, FlexarrOptions options, LayoutOptions layout, string me, CancellationToken ct)
    {
        // Only the libraries this profile can see: anyone may open their own Home & browse, kids and guests included.
        var all = await db.Libraries.OrderBy(l => l.Name).ToListAsync(ct);
        var libraries = all.Where(l => l.Type is (LibraryType.Movies or LibraryType.TvShows or LibraryType.HomeVideos)
            && !(options.LiveTv?.Account is not null && RecordingsLibrary.Is(l))).ToList();
        var collections = await db.Collections.Where(c => c.ShowOnHome && !c.Hidden && (c.OwnerProfileId == null || c.OwnerProfileId == me))
            .OrderBy(c => c.Name).Select(c => new RowChoiceDto($"collection-{c.Id}", $"Collection: {c.Name}", true)).ToListAsync(ct);
        var home = WithPodcastsRow(WithWatchlistRow(ForCombining(layout.Home, libraries, layout.CombineLibraries)));
        bool music = all.Any(l => l.Type == LibraryType.Music), books = all.Any(l => l.Type == LibraryType.Audiobooks);
        return new LayoutDto(Merge([.. HomeRows(libraries, layout.CombineLibraries, music, books, all.Any(l => l.Type == LibraryType.Podcasts)), .. collections], home),
            Merge(BrowseRows(movies: true), layout.Movies), Merge(BrowseRows(movies: false), layout.Shows),
            layout.CombineLibraries, libraries.GroupBy(l => l.Type).Any(g => g.Count() > 1),
            music ? Merge([.. MusicBrowseRows], layout.Music) : null, books ? Merge([.. BookShelves], layout.Audiobooks) : null);
    }

    /// <summary>
    /// Puts rows in the saved order and leaves out switched-off ones. Rows the layout doesn't mention (a new library, a
    /// new kind of row) keep their built-in place after the ones it does. All genre rows move together as "genres".
    /// </summary>
    public static List<HomeRowDto> Arrange(IEnumerable<HomeRowDto> rows, IReadOnlyList<RowSetting> layout)
    {
        var order = layout.Select((r, i) => (r.Id, i)).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().i, StringComparer.Ordinal);
        var off = layout.Where(r => !r.Enabled).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        return rows.Select((row, i) => (row, i, key: Key(row.Id)))
            .Where(r => !off.Contains(r.key))
            .OrderBy(r => order.TryGetValue(r.key, out var at) ? at : layout.Count + r.i)
            .ThenBy(r => r.i)
            .Select(r => r.row)
            .ToList();
    }

    /// <summary>The Latest unwatched row standing for every library of a kind, when they're combined.</summary>
    public static string CombinedRowId(LibraryType type) => type switch
    {
        LibraryType.Movies => "recent-movies",
        LibraryType.HomeVideos => "recent-home-videos",
        _ => "recent-shows",
    };

    /// <summary>
    /// A saved Home layout for the rows Home has now, with libraries combined or not. Whichever of a kind's combined row
    /// and its libraries' own rows the layout doesn't mention takes the place and the switch of the ones it does, so
    /// turning combining on or off moves nothing.
    /// </summary>
    /// <param name="libraries">The film and TV libraries, in Home's order.</param>
    public static List<RowSetting> ForCombining(IReadOnlyList<RowSetting> layout, IEnumerable<Library> libraries, bool combine)
    {
        var result = layout.ToList();

        foreach (var kind in libraries.Where(l => l.Type is LibraryType.Movies or LibraryType.TvShows or LibraryType.HomeVideos).GroupBy(l => l.Type))
        {
            List<string> combined = [CombinedRowId(kind.Key)], own = [.. kind.Select(l => $"recent-{l.Id}")];
            var (shown, other) = combine ? (combined, own) : (own, combined);
            var missing = shown.Where(id => result.All(r => r.Id != id)).ToList();
            var at = result.FindIndex(r => other.Contains(r.Id));
            if (missing.Count == 0 || at < 0)
            {
                continue;
            }

            var enabled = result.Where(r => other.Contains(r.Id)).Any(r => r.Enabled);
            result.InsertRange(at, missing.Select(id => new RowSetting { Id = id, Enabled = enabled }));
        }

        return result;
    }

    /// <summary>A Home layout saved before the watchlist row, with it straight after Continue watching rather than at the end.</summary>
    public static List<RowSetting> WithWatchlistRow(List<RowSetting> layout)
    {
        var continueAt = layout.FindIndex(r => r.Id == "continue");
        if (continueAt >= 0 && layout.All(r => r.Id != Watchlist.WatchlistService.HomeRowId))
        {
            layout.Insert(continueAt + 1, new RowSetting { Id = Watchlist.WatchlistService.HomeRowId, Enabled = true });
        }

        return layout;
    }

    /// <summary>
    /// A Home layout saved before the New podcasts row was one of its rows (each app placed it after Continue watching
    /// itself), with it there rather than at the end.
    /// </summary>
    public static List<RowSetting> WithPodcastsRow(List<RowSetting> layout)
    {
        var continueAt = layout.FindIndex(r => r.Id == "continue");
        if (continueAt >= 0 && layout.All(r => r.Id != PodcastsRowId))
        {
            layout.Insert(continueAt + 1, new RowSetting { Id = PodcastsRowId, Enabled = true });
        }

        return layout;
    }

    /// <summary>Home's row of new podcast episodes. The apps fill it themselves; the server only places it.</summary>
    public const string PodcastsRowId = "podcasts-new";

    /// <summary>The ids of an audiobooks library's shelves in this layout's order, leaving out switched-off ones.</summary>
    public static List<string> ShelfOrder(IReadOnlyList<RowSetting> layout) => [.. Merge([.. BookShelves], layout).Where(r => r.Enabled).Select(r => r.Id)];

    public static bool IsOn(IReadOnlyList<RowSetting> layout, string id) => !layout.Any(r => r.Id == id && !r.Enabled);

    private static string Key(string rowId) => rowId.StartsWith("genre-", StringComparison.Ordinal) ? "genres" : rowId;

    /// <param name="music">Whether there's a music library: its rows are only offered when there's one to fill them.</param>
    private static List<RowChoiceDto> HomeRows(IEnumerable<Library> libraries, bool combine, bool music, bool books, bool podcasts) =>
    [
        new("continue", "Continue watching (and listening)", true),
        .. podcasts ? [new RowChoiceDto(PodcastsRowId, "New podcast episodes", true)] : Array.Empty<RowChoiceDto>(),
        new(Watchlist.WatchlistService.HomeRowId, "Ready from your watchlist", true),
        new("live-tv", "Live TV (your favourite channels)", true),
        .. RecentRows(libraries.ToList(), combine),
        new("top-movies", "Top 10 films", true),
        new("top-shows", "Top 10 shows", true),
        .. music ? MusicRows : Array.Empty<RowChoiceDto>(),
        .. books ? BookRows : Array.Empty<RowChoiceDto>(),
        new("because", "Because you watched…", true),
        new("genres", "Genre rows (three, changing daily)", true),
        new("gems", "Highly rated films, not yet watched", true),
        new("upcoming", "Coming soon", true),
    ];

    /// <summary>A Latest unwatched row for each library, or one for each kind where it has more than one to combine.</summary>
    private static IEnumerable<RowChoiceDto> RecentRows(List<Library> libraries, bool combine) =>
        libraries.GroupBy(l => combine ? (object)l.Type : l.Id).Select(g =>
        {
            var first = g.First();
            var label = g.Count() == 1 ? $"Latest unwatched in {first.Name}"
                : first.Type switch
                {
                    LibraryType.Movies => "Latest unwatched films, from every film library",
                    LibraryType.HomeVideos => "Latest unwatched home videos, from every home video library",
                    _ => "Latest unwatched shows, from every TV library",
                };
            return new RowChoiceDto(combine ? CombinedRowId(first.Type) : $"recent-{first.Id}", label, true);
        });

    private static readonly RowChoiceDto[] BookRows =
    [
        new(Books.BookRows.ContinueRowId, "Continue listening (when kept apart)", true),
        new(Books.BookRows.NextRowId, "Next in series (audiobooks)", true),
        new(Books.BookRows.NewRowId, "New audiobooks", true),
    ];

    private static readonly RowChoiceDto[] MusicRows =
    [
        new(Playlists.PlaylistEndpoints.HomeRowId, "Your playlists", true),
        new(MusicMixes.HomeRowId, "Made for you (music mixes)", true),
        new("music-recent", "Recently played (music)", true),
        new("music-top", "Most played (music)", true),
        new("music-artists", "More from artists you play", true),
        new("music-new", "New albums", true),
    ];

    private static readonly RowChoiceDto[] MusicBrowseRows =
    [
        new(Playlists.PlaylistEndpoints.HomeRowId, "Your playlists", true),
        new(MusicMixes.HomeRowId, "Made for you (music mixes)", true),
        new("music-recent", "Recently played", true),
        new("music-top", "Most played", true),
        new("music-artists", "More from artists you play", true),
        new("recent", "New albums", true),
    ];

    /// <summary>An audiobooks library's shelves, as BookShelvesDto.Order names them.</summary>
    private static readonly RowChoiceDto[] BookShelves =
    [
        new("continue", "Continue listening", true),
        new("next", "Next in series", true),
        new("recent", "Recently added", true),
        new("series", "Series", true),
        new("authors", "Authors", true),
        new("finished", "Finished", true),
    ];

    private static List<RowChoiceDto> BrowseRows(bool movies) =>
    [
        new("continue", "Continue watching", true),
        new("recent", "Latest unwatched", true),
        new("top", "Top 10", true),
        new("released", movies ? "Newest releases" : "Newest shows", true),
        new("gems", movies ? "Highly rated, not yet watched" : "Highly rated, not yet started", true),
        new("genres", "Genre rows (the library's biggest genres)", true),
    ];

    /// <summary>The saved order and switches over every row there is, new ones last and on.</summary>
    private static List<RowChoiceDto> Merge(List<RowChoiceDto> available, IReadOnlyList<RowSetting> saved)
    {
        var byId = available.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var known = saved.Where(s => byId.ContainsKey(s.Id)).DistinctBy(s => s.Id).Select(s => byId[s.Id] with { Enabled = s.Enabled }).ToList();
        return [.. known, .. available.Where(r => known.All(k => k.Id != r.Id))];
    }
}
