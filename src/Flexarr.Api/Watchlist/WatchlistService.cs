using System.Globalization;
using System.Text.Json;
using Flexarr.Api.Configuration;
using Flexarr.Api.Endpoints;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Integrations.Arr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Watchlist;

/// <summary>Where a title on a watchlist has got to.</summary>
public enum WatchlistStatus
{
    /// <summary>In the library: play it.</summary>
    Ready,

    /// <summary>Requested, or Sonarr or Radarr are getting it.</summary>
    OnItsWay,

    /// <summary>Not out yet.</summary>
    Coming,

    /// <summary>Out, but nobody's asked for it: it can be requested.</summary>
    NotAvailable,
}

/// <param name="MediaType">"movie" or "tv".</param>
/// <param name="Item">The title in the library, when it's <see cref="WatchlistStatus.Ready"/>, for the usual poster card.</param>
/// <param name="Source">"flexarr", or "trakt" for a title brought across from Trakt.</param>
public sealed record WatchlistEntryDto(
    Guid Id,
    string MediaType,
    int? TmdbId,
    string Title,
    int? Year,
    string? Poster,
    string? Backdrop,
    DateTime AddedAt,
    WatchlistStatus Status,
    DateOnly? ReleaseDate,
    Guid? LibraryItemId,
    ItemSummaryDto? Item,
    string Source);

/// <summary>A library title (<paramref name="LibraryItemId"/>), or one from outside it (<paramref name="MediaType"/> and <paramref name="TmdbId"/>).</summary>
/// <param name="LibraryItemId">A film or show; a season or episode stands for its show.</param>
/// <param name="MediaType">"movie" or "tv".</param>
public sealed record WatchlistAdd(Guid? LibraryItemId = null, string? MediaType = null, int? TmdbId = null);

/// <param name="Requested">Adding it asked Seerr for it (the profile requests what it adds).</param>
public sealed record WatchlistAdded(WatchlistEntryDto Entry, bool Requested);

public sealed record WatchlistMembershipDto(bool OnWatchlist, Guid? Id);

/// <summary>Why a title couldn't go on a watchlist, as the endpoint's problem.</summary>
public sealed record WatchlistRefusal(int StatusCode, string Message);

/// <summary>
/// Each profile's watchlist: films and shows to watch later, in the library or not. Titles leave on their own once
/// they're watched (a film) or started (a show); titles that aren't in the library can be requested as they're added.
/// </summary>
public sealed class WatchlistService(FlexarrDbContext db, SeerrClient seerr, IOptionsMonitor<FlexarrOptions> options, IMemoryCache cache)
{
    /// <summary>Home's row of watchlist titles ready to play.</summary>
    public const string HomeRowId = "watchlist";

    private static readonly TimeSpan SeerrCacheFor = TimeSpan.FromMinutes(10);

    public async Task<List<WatchlistEntryDto>> ListAsync(string profileId, CancellationToken ct)
    {
        await PruneAsync(profileId, ct);
        var entries = await db.WatchlistEntries.Where(w => w.ProfileId == profileId).OrderByDescending(w => w.AddedAt).ToListAsync(ct);
        await LinkAsync(entries, ct);

        var ids = entries.Where(e => e.MediaItemId is not null).Select(e => e.MediaItemId!.Value).ToList();
        // Only what this profile can see (the query filter), and a show only once it has an episode to play.
        var items = (await db.MediaItems.Where(i => ids.Contains(i.Id) && (i.Kind == MediaKind.Movie || i.Children.Any(s => s.Children.Count > 0)))
            .Summaries(profileId).ToListAsync(ct)).ToDictionary(i => i.Id);
        var requested = await RequestedAsync(entries, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var result = new List<WatchlistEntryDto>(entries.Count);
        foreach (var entry in entries)
        {
            var item = entry.MediaItemId is { } id ? items.GetValueOrDefault(id) : null;
            var status = item is not null ? WatchlistStatus.Ready : await StatusOutsideLibraryAsync(entry, requested, today, ct);
            result.Add(ToDto(entry, status, item));
        }

        await db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>The library titles from this profile's watchlist that are ready to play, newest on the list first: Home's row.</summary>
    public async Task<List<ItemSummaryDto>> ReadyAsync(string profileId, int take, CancellationToken ct)
    {
        await PruneAsync(profileId, ct);
        var entries = await db.WatchlistEntries.Where(w => w.ProfileId == profileId).OrderByDescending(w => w.AddedAt).ToListAsync(ct);
        if (entries.Count == 0)
        {
            return [];
        }

        await LinkAsync(entries, ct);
        await db.SaveChangesAsync(ct);
        var order = entries.Where(e => e.MediaItemId is not null).Select((e, i) => (Id: e.MediaItemId!.Value, i)).ToDictionary(e => e.Id, e => e.i);
        var ids = order.Keys.ToList();
        var items = await db.MediaItems.Where(i => ids.Contains(i.Id) && (i.Kind == MediaKind.Movie || i.Children.Any(s => s.Children.Count > 0)))
            .Summaries(profileId).ToListAsync(ct);
        return items.OrderBy(i => order[i.Id]).Take(take).ToList();
    }

    public async Task<WatchlistMembershipDto> MembershipAsync(string profileId, WatchlistAdd key, CancellationToken ct)
    {
        await PruneAsync(profileId, ct);
        var entry = await FindAsync(profileId, key, ct);
        return new WatchlistMembershipDto(entry is not null, entry?.Id);
    }

    /// <summary>Whether a library title (or, for a season or episode, its show) is on the profile's watchlist.</summary>
    public async Task<bool> ContainsItemAsync(string profileId, Guid itemId, CancellationToken ct) =>
        (await MembershipAsync(profileId, new WatchlistAdd(itemId), ct)).OnWatchlist;

    /// <summary>Puts a title on the list (already there is fine), and requests it if the profile asks for that.</summary>
    public async Task<(WatchlistAdded? Added, WatchlistRefusal? Refusal)> AddAsync(string profileId, WatchlistAdd add, WatchlistSource source, CancellationToken ct)
    {
        if (await FindAsync(profileId, add, ct) is { } existing)
        {
            return (new WatchlistAdded((await ListAsync(profileId, ct)).First(e => e.Id == existing.Id), false), null);
        }

        WatchlistEntry entry;
        var item = await LibraryTitleAsync(add, ct);
        if (item is not null)
        {
            entry = FromItem(profileId, item);
        }
        else if (add.LibraryItemId is not null)
        {
            return (null, new WatchlistRefusal(404, "That title isn't in the library."));
        }
        else if (add.MediaType is not ("movie" or "tv") || add.TmdbId is not > 0)
        {
            return (null, new WatchlistRefusal(400, "Give a library title, or a TMDB id with \"movie\" or \"tv\"."));
        }
        else
        {
            var kind = add.MediaType == "movie" ? MediaKind.Movie : MediaKind.Series;
            if ((await LibraryVisibility.HiddenTypesAsync(db, ct)).Hides(kind))
            {
                return (null, new WatchlistRefusal(403, kind == MediaKind.Movie ? "This profile can't add films." : "This profile can't add shows."));
            }

            if (options.CurrentValue.Seerr?.Instance is not { } instance)
            {
                return (null, new WatchlistRefusal(409, "Titles that aren't in the library need a Seerr server. Add one under Settings › Connections."));
            }

            JsonElement? title;
            try
            {
                title = await seerr.GetAsync(instance, $"{add.MediaType}/{add.TmdbId}", ct);
            }
            catch (HttpRequestException ex)
            {
                return (null, new WatchlistRefusal(502, $"Seerr didn't answer ({(int?)ex.StatusCode ?? 0})."));
            }

            if (title is not { } t)
            {
                return (null, new WatchlistRefusal(404, "TMDB doesn't know that title."));
            }

            cache.Set(SeerrKey(add.MediaType, add.TmdbId.Value), Facts(t), SeerrCacheFor);
            entry = FromSeerr(profileId, kind, add.TmdbId.Value, t);
        }

        entry.Source = source;
        db.WatchlistEntries.Add(entry);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Added twice at once (a double tap): the other one is on the list, which is all that was asked.
            db.Entry(entry).State = EntityState.Detached;
            var other = await FindAsync(profileId, add, ct);
            return other is null
                ? (null, new WatchlistRefusal(409, "That couldn't be added. Try again."))
                : (new WatchlistAdded((await ListAsync(profileId, ct)).First(e => e.Id == other.Id), false), null);
        }

        var requested = source == WatchlistSource.Flexarr && await AutoRequestAsync(profileId, entry, ct);
        return (new WatchlistAdded((await ListAsync(profileId, ct)).First(e => e.Id == entry.Id), requested), null);
    }

    public async Task<bool> RemoveAsync(string profileId, Guid id, CancellationToken ct) =>
        await db.WatchlistEntries.Where(w => w.ProfileId == profileId && w.Id == id).ExecuteDeleteAsync(ct) > 0;

    public async Task<bool> RemoveAsync(string profileId, WatchlistAdd key, CancellationToken ct) =>
        await FindAsync(profileId, key, ct) is { } entry && await RemoveAsync(profileId, entry.Id, ct);

    // ---- Leaving, arriving, requesting ----

    /// <summary>
    /// Takes titles off once they've been dealt with since they went on: a film watched, a show with an episode played
    /// (Continue watching has it from there). Something already seen, added to see again, stays until it's played again.
    /// </summary>
    internal async Task PruneAsync(string profileId, CancellationToken ct) =>
        await db.WatchlistEntries.Where(w => w.ProfileId == profileId && w.MediaItemId != null && (w.Kind == MediaKind.Movie
                ? db.PlaybackStates.Any(p => p.UserId == profileId && p.MediaItemId == w.MediaItemId && p.Watched && p.LastPlayedAt > w.AddedAt)
                : db.PlaybackStates.Any(p => p.UserId == profileId && p.MediaItem!.Parent!.ParentId == w.MediaItemId && p.LastPlayedAt > w.AddedAt)))
            .ExecuteDeleteAsync(ct);

    /// <summary>Points titles that have come into the library (or come back to it) at their library item. Saved by the caller.</summary>
    private async Task LinkAsync(List<WatchlistEntry> entries, CancellationToken ct)
    {
        var waiting = entries.Where(e => e.MediaItemId is null && e.TmdbId is not null).ToList();
        if (waiting.Count == 0)
        {
            return;
        }

        var tmdb = waiting.Select(e => e.TmdbId!.Value.ToString(CultureInfo.InvariantCulture)).ToList();
        var found = await db.MediaItems.IgnoreQueryFilters().Where(i => i.MissingSince == null && (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series) && tmdb.Contains(i.TmdbId!))
            .Select(i => new { i.Id, i.Kind, i.TmdbId }).ToListAsync(ct);
        foreach (var entry in waiting)
        {
            var id = entry.TmdbId!.Value.ToString(CultureInfo.InvariantCulture);
            if (found.FirstOrDefault(f => f.Kind == entry.Kind && f.TmdbId == id) is { } match)
            {
                entry.MediaItemId = match.Id;
            }
        }
    }

    /// <summary>
    /// Requests a title the profile just added, if it asks for that, it isn't in (or already on its way to) the
    /// library, and the profile may see its kind. Seerr refusing isn't an error: the list shows where it really is.
    /// </summary>
    private async Task<bool> AutoRequestAsync(string profileId, WatchlistEntry entry, CancellationToken ct)
    {
        if (entry.MediaItemId is not null || entry.TmdbId is not { } tmdbId || options.CurrentValue.Seerr?.Instance is not { } instance
            || !await db.Profiles.AnyAsync(p => p.Id == profileId && p.AutoRequestWatchlist, ct)
            || (await LibraryVisibility.HiddenTypesAsync(db, ct)).Hides(entry.Kind))
        {
            return false;
        }

        var mediaType = entry.Kind == MediaKind.Movie ? "movie" : "tv";
        if (cache.Get<SeerrFacts>(SeerrKey(mediaType, tmdbId)) is { Availability: not AvailabilityDto.None })
        {
            return false;
        }

        try
        {
            await RequestEndpoints.PlaceAsync(seerr, instance, options.CurrentValue, db, profileId, mediaType, tmdbId, ct);
            cache.Remove(SeerrKey(mediaType, tmdbId));
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    // ---- Status of titles not in the library ----

    private sealed record SeerrFacts(AvailabilityDto Availability, DateOnly? ReleaseDate);

    private static string SeerrKey(string mediaType, int tmdbId) => $"watchlist:seerr:{mediaType}:{tmdbId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Titles asked for through Flexarr and not in yet: "movie:123" / "tv:456".</summary>
    private async Task<HashSet<string>> RequestedAsync(List<WatchlistEntry> entries, CancellationToken ct)
    {
        var tmdb = entries.Where(e => e.TmdbId is not null).Select(e => e.TmdbId!.Value).ToList();
        return (await db.MediaRequests.Where(r => r.FulfilledAt == null && tmdb.Contains(r.TmdbId)).Select(r => new { r.Kind, r.TmdbId }).ToListAsync(ct))
            .Select(r => $"{(r.Kind == MediaKind.Movie ? "movie" : "tv")}:{r.TmdbId.ToString(CultureInfo.InvariantCulture)}").ToHashSet(StringComparer.Ordinal);
    }

    private async Task<WatchlistStatus> StatusOutsideLibraryAsync(WatchlistEntry entry, HashSet<string> requested, DateOnly today, CancellationToken ct)
    {
        if (entry.TmdbId is not { } tmdbId)
        {
            return WatchlistStatus.NotAvailable;
        }

        var mediaType = entry.Kind == MediaKind.Movie ? "movie" : "tv";
        var facts = await SeerrFactsAsync(mediaType, tmdbId, ct);
        if (facts?.ReleaseDate is { } released)
        {
            entry.ReleaseDate = released;
        }

        if (requested.Contains($"{mediaType}:{tmdbId.ToString(CultureInfo.InvariantCulture)}") || facts?.Availability is AvailabilityDto.Requested or AvailabilityDto.PartlyAvailable or AvailabilityDto.Available)
        {
            return WatchlistStatus.OnItsWay;
        }

        return entry.ReleaseDate > today ? WatchlistStatus.Coming : WatchlistStatus.NotAvailable;
    }

    /// <summary>What Seerr says about a title, kept a few minutes; null without Seerr or when it doesn't answer.</summary>
    private async Task<SeerrFacts?> SeerrFactsAsync(string mediaType, int tmdbId, CancellationToken ct)
    {
        var key = SeerrKey(mediaType, tmdbId);
        if (cache.TryGetValue(key, out SeerrFacts? known))
        {
            return known;
        }

        if (options.CurrentValue.Seerr?.Instance is not { } instance)
        {
            return null;
        }

        try
        {
            var facts = await seerr.GetAsync(instance, $"{mediaType}/{tmdbId.ToString(CultureInfo.InvariantCulture)}", ct) is { } t ? Facts(t) : null;
            cache.Set(key, facts, SeerrCacheFor);
            return facts;
        }
        catch (HttpRequestException)
        {
            return null; // The list still shows; the title just can't say whether it's on its way.
        }
    }

    private static SeerrFacts Facts(JsonElement t)
    {
        int? status = t.TryGetProperty("mediaInfo", out var info) && info.ValueKind == JsonValueKind.Object && info.TryGetProperty("status", out var s)
            && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : null;
        return new SeerrFacts(RequestEndpoints.AvailabilityOf(status), DateOf(t));
    }

    private static DateOnly? DateOf(JsonElement t) =>
        (Str(t, "releaseDate") ?? Str(t, "firstAirDate")) is { } date && DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---- Finding and making entries ----

    /// <summary>The film or show a library id stands for (a season or episode stands for its show), or the one with that TMDB id.</summary>
    private async Task<MediaItem?> LibraryTitleAsync(WatchlistAdd add, CancellationToken ct)
    {
        if (add.LibraryItemId is { } id)
        {
            var item = await db.MediaItems.AsNoTracking().Include(i => i.Parent).ThenInclude(p => p!.Parent).FirstOrDefaultAsync(i => i.Id == id, ct);
            return item?.Kind switch
            {
                MediaKind.Movie or MediaKind.Series => item,
                MediaKind.Season => item.Parent,
                MediaKind.Episode => item.Parent?.Parent,
                _ => null,
            };
        }

        if (add.MediaType is not ("movie" or "tv") || add.TmdbId is not { } tmdbId)
        {
            return null;
        }

        var kind = add.MediaType == "movie" ? MediaKind.Movie : MediaKind.Series;
        var tmdb = tmdbId.ToString(CultureInfo.InvariantCulture);
        return await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Kind == kind && i.ParentId == null && i.TmdbId == tmdb, ct);
    }

    private async Task<WatchlistEntry?> FindAsync(string profileId, WatchlistAdd key, CancellationToken ct)
    {
        var mine = db.WatchlistEntries.Where(w => w.ProfileId == profileId);
        if (await LibraryTitleAsync(key, ct) is { } item)
        {
            var tmdb = int.TryParse(item.TmdbId, NumberStyles.None, CultureInfo.InvariantCulture, out var t) ? t : (int?)null;
            return await mine.FirstOrDefaultAsync(w => w.MediaItemId == item.Id || (tmdb != null && w.Kind == item.Kind && w.TmdbId == tmdb), ct);
        }

        if (key.MediaType is not ("movie" or "tv") || key.TmdbId is not { } tmdbId)
        {
            return null;
        }

        var kind = key.MediaType == "movie" ? MediaKind.Movie : MediaKind.Series;
        return await mine.FirstOrDefaultAsync(w => w.Kind == kind && w.TmdbId == tmdbId, ct);
    }

    private static WatchlistEntry FromItem(string profileId, MediaItem item) => new()
    {
        ProfileId = profileId,
        Kind = item.Kind,
        TmdbId = int.TryParse(item.TmdbId, NumberStyles.None, CultureInfo.InvariantCulture, out var tmdb) ? tmdb : null,
        TvdbId = item.TvdbId,
        ImdbId = item.ImdbId,
        MediaItemId = item.Id,
        Title = item.Title,
        Year = item.Year,
        PosterUrl = item.PosterUrl,
        BackdropUrl = item.BackdropUrl,
        ReleaseDate = item.ReleaseDate,
        // Already here: nothing to tell anyone when it "arrives".
        ArrivedAt = DateTime.UtcNow,
    };

    private static WatchlistEntry FromSeerr(string profileId, MediaKind kind, int tmdbId, JsonElement t)
    {
        var date = DateOf(t);
        string? ids(string name) => t.TryGetProperty("externalIds", out var e) && e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;
        return new WatchlistEntry
        {
            ProfileId = profileId,
            Kind = kind,
            TmdbId = tmdbId,
            TvdbId = ids("tvdbId"),
            ImdbId = ids("imdbId"),
            Title = Str(t, "title") ?? Str(t, "name") ?? "Untitled",
            Year = date?.Year,
            PosterUrl = Str(t, "posterPath") is { } poster ? $"https://image.tmdb.org/t/p/w500{poster}" : null,
            BackdropUrl = Str(t, "backdropPath") is { } backdrop ? $"https://image.tmdb.org/t/p/w1280{backdrop}" : null,
            ReleaseDate = date,
        };
    }

    private static WatchlistEntryDto ToDto(WatchlistEntry e, WatchlistStatus status, ItemSummaryDto? item) => new(
        e.Id, e.Kind == MediaKind.Movie ? "movie" : "tv", e.TmdbId, item?.Title ?? e.Title, item?.Year ?? e.Year,
        item?.Artwork.Poster ?? ImageEndpoints.RemoteUrl(e.PosterUrl), item?.Artwork.Backdrop ?? ImageEndpoints.RemoteUrl(e.BackdropUrl), e.AddedAt, status, e.ReleaseDate,
        item?.Id, item, e.Source == WatchlistSource.Trakt ? "trakt" : "flexarr");
}
