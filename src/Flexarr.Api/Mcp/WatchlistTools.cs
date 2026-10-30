using System.ComponentModel;
using Flexarr.Api.Configuration;
using Flexarr.Api.Profiles;
using Flexarr.Api.Watchlist;
using Flexarr.Core.Media;
using Flexarr.Integrations.Arr;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Flexarr.Api.Mcp;

/// <summary>A title on the watchlist as the tools describe it.</summary>
/// <param name="Id">The entry's id, for remove_from_watchlist.</param>
/// <param name="LibraryItemId">Set when it's in the library: play it with play_on_device.</param>
/// <param name="Status">Ready (in the library), OnItsWay (requested or downloading), Coming (not out yet) or NotAvailable.</param>
public sealed record McpWatchlistEntry(Guid Id, string Kind, string Title, int? Year, WatchlistStatus Status, Guid? LibraryItemId, DateOnly? ReleaseDate)
{
    public static McpWatchlistEntry From(WatchlistEntryDto e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new McpWatchlistEntry(e.Id, e.MediaType == "movie" ? "film" : "show", e.Title, e.Year, e.Status, e.LibraryItemId, e.ReleaseDate);
    }
}

/// <summary>The profile's watchlist for an assistant: what's on it, and adding or taking titles off.</summary>
[McpServerToolType]
public static class WatchlistTools
{
    [McpServerTool(Name = "watchlist", ReadOnly = true), Description("This profile's watchlist: films and shows it wants to watch, newest first, with whether each is ready to play (in the library), on its way, coming out later, or not available.")]
    public static async Task<IReadOnlyList<McpWatchlistEntry>> ListAsync(WatchlistService watchlist, CurrentProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(watchlist);
        ArgumentNullException.ThrowIfNull(profile);
        return (await watchlist.ListAsync(profile.Id, ct)).Select(McpWatchlistEntry.From).ToList();
    }

    [McpServerTool(Name = "add_to_watchlist"), Description("Adds a film or show to this profile's watchlist (which can request it, if the profile asks for that). Give a library title's id (from search_library); or, for one not in the library, its TMDB id with its kind; or a title to look up among every film and show, with its year and kind if known. A title that doesn't match exactly answers with candidates to choose from by TMDB id. The result says whether it was also requested.")]
    public static async Task<string> AddAsync(WatchlistService watchlist, SeerrClient seerr, IOptionsMonitor<FlexarrOptions> options, CurrentProfile profile, CancellationToken ct,
        [Description("A film's or show's id, from search_library")] Guid? itemId = null,
        [Description("A TMDB id, from an earlier answer's candidates; give kind too")] int? tmdbId = null,
        [Description("The title, for one not in the library, e.g. \"dune part three\"")] string? title = null,
        [Description("The year it came out, to pick the right one")] int? year = null,
        [Description("film or show")] string? kind = null)
    {
        ArgumentNullException.ThrowIfNull(watchlist);
        ArgumentNullException.ThrowIfNull(seerr);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(profile);

        var mediaType = kind?.Trim().ToUpperInvariant() switch
        {
            "FILM" or "MOVIE" => "movie",
            "SHOW" or "TV" or "SERIES" => "tv",
            _ => null,
        };
        WatchlistAdd add;
        if (itemId is { } id)
        {
            add = new WatchlistAdd(id);
        }
        else if (tmdbId is { } tmdb)
        {
            add = new WatchlistAdd(null, mediaType ?? throw new InvalidOperationException("Say whether TMDB id is a film or a show (kind)."), tmdb);
        }
        else if (!string.IsNullOrWhiteSpace(title))
        {
            if (options.CurrentValue.Seerr?.Instance is not { } instance)
            {
                throw new InvalidOperationException("Looking up titles that aren't in the library needs a Seerr server. Use search_library for ones that are.");
            }

            var wanted = title.Trim();
            var results = (await seerr.SearchAsync(instance, wanted, ct)).Results
                .Where(r => r.MediaType is "movie" or "tv" && (mediaType is null || r.MediaType == mediaType)).ToList();
            static int? yearOf(SeerrResult r) => (r.ReleaseDate ?? r.FirstAirDate) is { Length: >= 4 } d && int.TryParse(d[..4], System.Globalization.CultureInfo.InvariantCulture, out var y) ? y : null;
            // Only an exact title (and year, if given) is taken as meant: anything looser could have Seerr download
            // something nobody chose.
            var exact = results.Where(r => string.Equals((r.Title ?? r.Name)?.Trim(), wanted, StringComparison.OrdinalIgnoreCase)
                && (year is null || yearOf(r) == year)).ToList();
            if (exact.Count != 1)
            {
                var candidates = results.Take(5).Select(r => $"{r.Title ?? r.Name} ({yearOf(r)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}, {(r.MediaType == "movie" ? "film" : "show")}, tmdbId {r.Id})");
                throw new InvalidOperationException(results.Count == 0
                    ? $"No film or show called \"{wanted}\" was found."
                    : $"Which one? Ask again with its tmdbId and kind: {string.Join("; ", candidates)}.");
            }

            add = new WatchlistAdd(null, exact[0].MediaType, exact[0].Id);
        }
        else
        {
            throw new InvalidOperationException("Give a library title's id, a TMDB id with its kind, or a title to look up.");
        }

        var (added, refusal) = await watchlist.AddAsync(profile.Id, add, WatchlistSource.Flexarr, ct);
        if (added is null)
        {
            throw new InvalidOperationException(refusal!.Message);
        }

        var name = added.Entry.Year is { } released ? $"{added.Entry.Title} ({released})" : added.Entry.Title;
        return added.Entry.Status switch
        {
            _ when added.Requested => $"Added {name} to the watchlist and requested it.",
            WatchlistStatus.Ready => $"Added {name} to the watchlist. It's in the library, ready to play.",
            WatchlistStatus.OnItsWay => $"Added {name} to the watchlist. It's on its way to the library.",
            WatchlistStatus.Coming => $"Added {name} to the watchlist. It comes out {added.Entry.ReleaseDate:d MMMM yyyy}.",
            _ => $"Added {name} to the watchlist. It isn't in the library or requested.",
        };
    }

    [McpServerTool(Name = "remove_from_watchlist"), Description("Takes a title off this profile's watchlist. It doesn't cancel a request.")]
    public static async Task<string> RemoveAsync([Description("The entry's id, from watchlist")] Guid id, WatchlistService watchlist, CurrentProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(watchlist);
        ArgumentNullException.ThrowIfNull(profile);
        return await watchlist.RemoveAsync(profile.Id, id, ct) ? "Removed from the watchlist." : throw new InvalidOperationException("That isn't on the watchlist.");
    }
}
