using System.Text.Json;
using System.Text.Json.Nodes;
using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Endpoints;

public sealed record RowChoiceDto(string Id, string Label, bool Enabled);

/// <param name="Combinable">Whether there are two film libraries or two TV ones, so combining them changes anything.</param>
public sealed record LayoutDto(IReadOnlyList<RowChoiceDto> Home, IReadOnlyList<RowChoiceDto> Movies, IReadOnlyList<RowChoiceDto> Shows, bool CombineLibraries, bool Combinable);

/// <param name="CombineLibraries">Left out (by apps from before it), the setting stays as it is.</param>
public sealed record LayoutUpdate(IReadOnlyList<RowSetting> Home, IReadOnlyList<RowSetting> Movies, IReadOnlyList<RowSetting> Shows, bool? CombineLibraries = null);

/// <summary>Choosing which rows Home and Browse show, and in what order (Settings › Home and Browse).</summary>
public static class LayoutEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapLayoutEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/settings/layout").WithTags("Settings").RequireAuthorization(FlexarrClaims.AdminPolicy);

        group.MapGet("/", (FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, Profiles.CurrentProfile profile, CancellationToken ct) =>
            LayoutAsync(db, options.CurrentValue, options.CurrentValue.Layout, profile.Id, ct));

        // Answers with the layout as saved: config.json reloads a moment later, and combining changes which rows there are.
        group.MapPut("/", (LayoutUpdate update, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, Profiles.CurrentProfile profile, CancellationToken ct) =>
        {
            var combine = update.CombineLibraries ?? options.CurrentValue.Layout.CombineLibraries;
            ServerConfigStore.Update(options.CurrentValue.DataDirectory, config =>
                config["layout"] = JsonSerializer.SerializeToNode(new { home = update.Home, movies = update.Movies, shows = update.Shows, combineLibraries = combine }, Json));
            var saved = new LayoutOptions { Home = [.. update.Home], Movies = [.. update.Movies], Shows = [.. update.Shows], CombineLibraries = combine };
            return LayoutAsync(db, options.CurrentValue, saved, profile.Id, ct);
        });

        return api;
    }

    private static async Task<LayoutDto> LayoutAsync(FlexarrDbContext db, FlexarrOptions options, LayoutOptions layout, string me, CancellationToken ct)
    {
        var all = await db.Libraries.IgnoreQueryFilters().OrderBy(l => l.Name).ToListAsync(ct);
        var libraries = all.Where(l => l.Type is (LibraryType.Movies or LibraryType.TvShows or LibraryType.HomeVideos)
            && !(options.LiveTv?.Account is not null && RecordingsLibrary.Is(l))).ToList();
        var collections = await db.Collections.Where(c => c.ShowOnHome && !c.Hidden && (c.OwnerProfileId == null || c.OwnerProfileId == me))
            .OrderBy(c => c.Name).Select(c => new RowChoiceDto($"collection-{c.Id}", $"Collection: {c.Name}", true)).ToListAsync(ct);
        var home = ForCombining(layout.Home, libraries, layout.CombineLibraries);
        return new LayoutDto(Merge([.. HomeRows(libraries, layout.CombineLibraries, all.Any(l => l.Type == LibraryType.Music)), .. collections], home),
            Merge(BrowseRows(movies: true), layout.Movies), Merge(BrowseRows(movies: false), layout.Shows),
            layout.CombineLibraries, libraries.GroupBy(l => l.Type).Any(g => g.Count() > 1));
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

    public static bool IsOn(IReadOnlyList<RowSetting> layout, string id) => !layout.Any(r => r.Id == id && !r.Enabled);

    private static string Key(string rowId) => rowId.StartsWith("genre-", StringComparison.Ordinal) ? "genres" : rowId;

    /// <param name="music">Whether there's a music library: its rows are only offered when there's one to fill them.</param>
    private static List<RowChoiceDto> HomeRows(IEnumerable<Library> libraries, bool combine, bool music) =>
    [
        new("continue", "Continue watching", true),
        new(Watchlist.WatchlistService.HomeRowId, "Ready from your watchlist", true),
        new("live-tv", "Live TV (your favourite channels)", true),
        .. RecentRows(libraries.ToList(), combine),
        new("top-movies", "Top 10 films", true),
        new("top-shows", "Top 10 shows", true),
        .. music ? MusicRows : Array.Empty<RowChoiceDto>(),
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

    private static readonly RowChoiceDto[] MusicRows =
    [
        new(Playlists.PlaylistEndpoints.HomeRowId, "Your playlists", true),
        new(MusicMixes.HomeRowId, "Made for you (music mixes)", true),
        new("music-recent", "Recently played (music)", true),
        new("music-top", "Most played (music)", true),
        new("music-artists", "More from artists you play", true),
        new("music-new", "New albums", true),
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
