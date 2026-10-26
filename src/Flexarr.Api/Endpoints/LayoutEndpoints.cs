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

public sealed record LayoutDto(IReadOnlyList<RowChoiceDto> Home, IReadOnlyList<RowChoiceDto> Movies, IReadOnlyList<RowChoiceDto> Shows);

public sealed record LayoutUpdate(IReadOnlyList<RowSetting> Home, IReadOnlyList<RowSetting> Movies, IReadOnlyList<RowSetting> Shows);

/// <summary>Choosing which rows Home and Browse show, and in what order (Settings › Home and Browse).</summary>
public static class LayoutEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapLayoutEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/settings/layout").WithTags("Settings").RequireAuthorization(FlexarrClaims.AdminPolicy);

        group.MapGet("/", async (FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, Profiles.CurrentProfile profile, CancellationToken ct) =>
        {
            var all = await db.Libraries.IgnoreQueryFilters().OrderBy(l => l.Name).ToListAsync(ct);
            var libraries = all.Where(l => l.Type is (LibraryType.Movies or LibraryType.TvShows)
                && !(options.CurrentValue.LiveTv?.Account is not null && RecordingsLibrary.Is(l))).ToList();
            var me = profile.Id;
            var collections = await db.Collections.Where(c => c.ShowOnHome && !c.Hidden && (c.OwnerProfileId == null || c.OwnerProfileId == me))
                .OrderBy(c => c.Name).Select(c => new RowChoiceDto($"collection-{c.Id}", $"Collection: {c.Name}", true)).ToListAsync(ct);
            var layout = options.CurrentValue.Layout;
            return new LayoutDto(Merge([.. HomeRows(libraries, all.Any(l => l.Type == LibraryType.Music)), .. collections], layout.Home), Merge(BrowseRows(movies: true), layout.Movies), Merge(BrowseRows(movies: false), layout.Shows));
        });

        group.MapPut("/", (LayoutUpdate update, IOptionsMonitor<FlexarrOptions> options) =>
        {
            ServerConfigStore.Update(options.CurrentValue.DataDirectory, config =>
                config["layout"] = JsonSerializer.SerializeToNode(new { home = update.Home, movies = update.Movies, shows = update.Shows }, Json));
            return Results.NoContent();
        });

        return api;
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

    public static bool IsOn(IReadOnlyList<RowSetting> layout, string id) => !layout.Any(r => r.Id == id && !r.Enabled);

    private static string Key(string rowId) => rowId.StartsWith("genre-", StringComparison.Ordinal) ? "genres" : rowId;

    /// <param name="music">Whether there's a music library: its rows are only offered when there's one to fill them.</param>
    private static List<RowChoiceDto> HomeRows(IEnumerable<Library> libraries, bool music) =>
    [
        new("continue", "Continue watching", true),
        new("live-tv", "Live TV (your favourite channels)", true),
        .. libraries.Select(l => new RowChoiceDto($"recent-{l.Id}", $"Latest unwatched in {l.Name}", true)),
        new("top-movies", "Top 10 films", true),
        new("top-shows", "Top 10 shows", true),
        .. music ? MusicRows : Array.Empty<RowChoiceDto>(),
        new("because", "Because you watched…", true),
        new("genres", "Genre rows (three, changing daily)", true),
        new("gems", "Highly rated films, not yet watched", true),
        new("upcoming", "Coming soon", true),
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
