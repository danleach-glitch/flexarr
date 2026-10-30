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
/// <param name="Streaming">For a title that isn't in the library, the subscription services it's on in Seerr's streaming region; null when none or unknown.</param>
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
    string Source,
    IReadOnlyList<string>? Streaming = null);

/// <summary>A library title (<paramref name="LibraryItemId"/>), or one from outside it (<paramref name="MediaType"/> and <paramref name="TmdbId"/>).</summary>
/// <param name="LibraryItemId">A film or show; a season or episode stands for its show.</param>
/// <param name="MediaType">"movie" or "tv".</param>
public sealed record WatchlistAdd(Guid? LibraryItemId = null, string? MediaType = null, int? TmdbId = null);

/// <param name="Requested">Adding it asked Seerr for it (the profile requests what it adds).</param>
public sealed record WatchlistAdded(WatchlistEntryDto Entry, bool Requested);

public sealed record WatchlistMembershipDto(bool OnWatchlist, Guid? Id);

/// <summary>What's on the profile's watchlist, cheaply, for menus that offer Add or Remove.</summary>
/// <param name="LibraryItemIds">Library films and shows on it.</param>
/// <param name="Titles">Every title on it as "movie:123" / "tv:456" (TMDB ids).</param>
public sealed record WatchlistKeysDto(IReadOnlyList<Guid> LibraryItemIds, IReadOnlyList<string> Titles);

/// <summary>Why a title couldn't go on a watchlist, as the endpoint's problem.</summary>
public sealed record WatchlistRefusal(int StatusCode, string Message);

/// <summary>
/// Each profile's watchlist: films and shows to watch later, in the library or not. Titles leave on their own once
/// they're watched (a film) or started (a show); titles that aren't in the library can be requested as they're added.
/// A title that leaves is kept as removed (with why), so a sync elsewhere can be told and adding it again revives it.
/// </summary>
public sealed class WatchlistService(FlexarrDbContext db, SeerrClient seerr, IOptionsMonitor<FlexarrOptions> options, IMemoryCache cache)
{
    /// <summary>Home's row of watchlist titles ready to play.</summary>
    public const string HomeRowId = "watchlist";

    private static readonly TimeSpan SeerrCacheFor = TimeSpan.FromMinutes(10);

    /// <summary>A Seerr that didn't answer is asked again after this, not on every load.</summary>
    private static readonly TimeSpan SeerrFailureCacheFor = TimeSpan.FromMinutes(1);

    /// <summary>The most titles one profile's watchlist holds: past it, adding is refused.</summary>
    public const int MaxEntries = 1000;

    /// <summary>Seerr lookups at once while a list loads.</summary>
    private const int SeerrParallelism = 6;

    private IQueryable<WatchlistEntry> Live(string profileId) => db.WatchlistEntries.Where(w => w.ProfileId == profileId && w.RemovedAt == null);

    public async Task<List<WatchlistEntryDto>> ListAsync(string profileId, CancellationToken ct)
    {
        await PruneAsync(profileId, ct);
        var entries = await Live(profileId).OrderByDescending(w => w.AddedAt).ToListAsync(ct);
        var result = await ToDtosAsync(profileId, entries, ct);
        await SaveLinksAsync(ct);
        return result;
    }

    /// <summary>The library titles from this profile's watchlist that are ready to play, newest on the list first: Home's row.</summary>
    public async Task<List<ItemSummaryDto>> ReadyAsync(string profileId, int take, CancellationToken ct)
    {
        await PruneAsync(profileId, ct);
        var entries = await Live(profileId).OrderByDescending(w => w.AddedAt).ToListAsync(ct);
        if (entries.Count == 0)
        {
            return [];
        }

        await LinkAsync(profileId, entries, ct);
        await SaveLinksAsync(ct);
        var order = entries.Where(e => e.MediaItemId is not null).Select((e, i) => (Id: e.MediaItemId!.Value, i)).ToDictionary(e => e.Id, e => e.i);
        var linked = order.Keys.ToList();
        // Which can be played (and seen by this profile) first, cheaply; then the full card for only the ones shown.
        var playable = await db.MediaItems.Where(i => linked.Contains(i.Id) && (i.Kind == MediaKind.Movie || i.Children.Any(s => s.Children.Count > 0)))
            .Select(i => i.Id).ToListAsync(ct);
        var shown = playable.OrderBy(id => order[id]).Take(take).ToList();
        var items = await db.MediaItems.Where(i => shown.Contains(i.Id)).Summaries(profileId).ToListAsync(ct);
        return items.OrderBy(i => order[i.Id]).ToList();
    }

    public async Task<WatchlistMembershipDto> MembershipAsync(string profileId, WatchlistAdd key, CancellationToken ct)
    {
        await PruneAsync(profileId, ct);
        var entry = await FindAsync(profileId, key, includeRemoved: false, ct);
        return new WatchlistMembershipDto(entry is not null, entry?.Id);
    }

    /// <summary>The profile's watchlist as "movie:123" / "tv:456" (TMDB ids), to mark Discover's and search's titles.</summary>
    public async Task<IReadOnlySet<string>> KeysAsync(string profileId, CancellationToken ct) =>
        (await Live(profileId).Where(w => w.TmdbId != null).Select(w => new { w.Kind, w.TmdbId }).ToListAsync(ct))
        .Select(w => Key(w.Kind, w.TmdbId!.Value)).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// What's on the list, without asking Seerr or writing anything: the library titles (linked, or matching an entry's
    /// TMDB id) and every TMDB key. For the apps' poster menus.
    /// </summary>
    public async Task<WatchlistKeysDto> AllKeysAsync(string profileId, CancellationToken ct)
    {
        await PruneAsync(profileId, ct);
        var entries = await Live(profileId).Select(w => new { w.Kind, w.TmdbId, w.MediaItemId }).ToListAsync(ct);
        var linked = entries.Where(e => e.MediaItemId is not null).Select(e => e.MediaItemId!.Value).ToList();
        // Only titles this profile can still see (a library hidden from it since stays out).
        var ids = (await db.MediaItems.Where(i => linked.Contains(i.Id)).Select(i => i.Id).ToListAsync(ct)).ToHashSet();
        var unlinked = entries.Where(e => e.MediaItemId is null && e.TmdbId is not null).ToList();
        if (unlinked.Count > 0)
        {
            var tmdb = unlinked.Select(e => e.TmdbId!.Value.ToString(CultureInfo.InvariantCulture)).ToList();
            var wanted = unlinked.Select(e => Key(e.Kind, e.TmdbId!.Value)).ToHashSet(StringComparer.Ordinal);
            foreach (var item in await db.MediaItems.Where(i => i.ParentId == null && (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series) && tmdb.Contains(i.TmdbId!))
                         .Select(i => new { i.Id, i.Kind, i.TmdbId }).ToListAsync(ct))
            {
                if (wanted.Contains($"{(item.Kind == MediaKind.Movie ? "movie" : "tv")}:{item.TmdbId}"))
                {
                    ids.Add(item.Id);
                }
            }
        }

        return new WatchlistKeysDto(ids.ToList(), entries.Where(e => e.TmdbId is not null).Select(e => Key(e.Kind, e.TmdbId!.Value)).Distinct().ToList());
    }

    /// <summary>Whether a library title (or, for a season or episode, its show) is on the profile's watchlist.</summary>
    public async Task<bool> ContainsItemAsync(string profileId, Guid itemId, CancellationToken ct) =>
        (await MembershipAsync(profileId, new WatchlistAdd(itemId), ct)).OnWatchlist;

    /// <summary>
    /// Puts a title on the list (already there is fine; one taken off before comes back), and requests it if the
    /// profile asks for that.
    /// </summary>
    /// <param name="mayRequest">False to never request, whatever the profile asks (an assistant's add by a bare TMDB id).</param>
    public async Task<(WatchlistAdded? Added, WatchlistRefusal? Refusal)> AddAsync(string profileId, WatchlistAdd add, WatchlistSource source, CancellationToken ct,
        bool mayRequest = true)
    {
        // So a title watched since it went on counts as off the list, and comes back as a fresh entry.
        await PruneAsync(profileId, ct);
        var existing = await FindAsync(profileId, add, includeRemoved: true, ct);
        if (existing is { RemovedAt: null })
        {
            // Already on: added again where requests may follow (an assistant confirming its pick), it's requested now.
            var again = mayRequest && source == WatchlistSource.Flexarr && await AutoRequestAsync(profileId, existing, ct);
            return (new WatchlistAdded(await DtoAsync(profileId, existing, ct), again), null);
        }

        if (await Live(profileId).CountAsync(ct) >= MaxEntries)
        {
            return (null, new WatchlistRefusal(409, $"A watchlist holds up to {MaxEntries:N0} titles. Take some off first."));
        }

        WatchlistEntry entry;
        var item = await LibraryTitleAsync(add, ct);
        if (existing is not null)
        {
            if (item is null && (await LibraryVisibility.HiddenTypesAsync(db, ct)).Hides(existing.Kind))
            {
                return (null, new WatchlistRefusal(403, existing.Kind == MediaKind.Movie ? "This profile can't add films." : "This profile can't add shows."));
            }

            entry = existing;
            entry.RemovedAt = null;
            entry.RemovedReason = null;
            entry.AddedAt = DateTime.UtcNow;
            entry.Source = source;
            // Already in the library: nothing to tell anyone (and nothing to request). Not yet: tell them when it comes.
            entry.ArrivedAt = item is not null ? DateTime.UtcNow : null;
            if (item is not null && entry.MediaItemId is null
                && !await db.WatchlistEntries.AnyAsync(w => w.ProfileId == profileId && w.MediaItemId == item.Id, ct))
            {
                entry.MediaItemId = item.Id;
            }
        }
        else if (item is not null)
        {
            entry = FromItem(profileId, item);
            entry.Source = source;
            db.WatchlistEntries.Add(entry);
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
            catch (Exception ex) when (SeerrFailed(ex, ct))
            {
                return (null, new WatchlistRefusal(502, "Seerr didn't answer. Try again in a moment."));
            }

            if (title is not { } t)
            {
                return (null, new WatchlistRefusal(404, "TMDB doesn't know that title."));
            }

            var region = await RegionAsync(instance, ct);
            cache.Set(SeerrKey(add.MediaType, add.TmdbId.Value), Facts(t, region ?? "US"), region is null ? SeerrFailureCacheFor : SeerrCacheFor);
            entry = FromSeerr(profileId, kind, add.TmdbId.Value, t);
            entry.Source = source;
            db.WatchlistEntries.Add(entry);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Added twice at once (a double tap): the other one is on the list, which is all that was asked.
            db.Entry(entry).State = EntityState.Detached;
            return await FindAsync(profileId, add, includeRemoved: false, ct) is { } other
                ? (new WatchlistAdded(await DtoAsync(profileId, other, ct), false), null)
                : (null, new WatchlistRefusal(409, "That couldn't be added. Try again."));
        }

        // In the library already (even held by another of its entries): nothing to request.
        var requested = mayRequest && source == WatchlistSource.Flexarr && item is null && await AutoRequestAsync(profileId, entry, ct);
        return (new WatchlistAdded(await DtoAsync(profileId, entry, ct), requested), null);
    }

    /// <summary>How long a title taken off is remembered (for a sync to pass on, and to bring back as it was).</summary>
    public static readonly TimeSpan RemovedKeptFor = TimeSpan.FromDays(365);

    /// <summary>Takes a title off. It's kept as removed, so a sync can pass that on; a request for it isn't cancelled.</summary>
    public async Task<bool> RemoveAsync(string profileId, Guid id, CancellationToken ct)
    {
        LookAgain();
        var removed = await Live(profileId).Where(w => w.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(w => w.RemovedAt, DateTime.UtcNow).SetProperty(w => w.RemovedReason, WatchlistRemoval.Removed), ct) > 0;
        // Removals long past go for good, so they can't pile up.
        var before = DateTime.UtcNow - RemovedKeptFor;
        await db.WatchlistEntries.Where(w => w.ProfileId == profileId && w.RemovedAt != null && w.RemovedAt < before).ExecuteDeleteAsync(ct);
        return removed;
    }

    public async Task<bool> RemoveAsync(string profileId, WatchlistAdd key, CancellationToken ct) =>
        await FindAsync(profileId, key, includeRemoved: false, ct) is { } entry && await RemoveAsync(profileId, entry.Id, ct);

    // ---- Leaving, arriving, requesting ----

    /// <summary>
    /// Takes titles off once they've been dealt with since they went on: a film watched, a show with an episode played
    /// (Continue watching has it from there). Something already seen, added to see again, stays until it's played again;
    /// marking a show unwatched isn't playing it. Only reads unless something leaves.
    /// </summary>
    internal async Task PruneAsync(string profileId, CancellationToken ct)
    {
        // Only worth looking when there's been a play, or a title has come into the library, since the last look (or
        // something said to look again: plays dated in the past, a title linked or taken off).
        var lastPlay = await db.PlaybackStates.Where(p => p.UserId == profileId).MaxAsync(p => (DateTime?)p.LastPlayedAt, ct);
        var linkedCount = await Live(profileId).CountAsync(w => w.MediaItemId != null, ct);
        var stamp = (lastPlay, linkedCount + 1_000_000 * Volatile.Read(ref pruneGeneration));
        var stampKey = $"watchlist:pruned:{profileId}";
        if (cache.TryGetValue(stampKey, out (DateTime?, int) seen) && seen == stamp)
        {
            return;
        }

        var watched = await Live(profileId)
            .Where(w => w.Kind == MediaKind.Movie && w.MediaItemId != null
                && db.PlaybackStates.Any(p => p.UserId == profileId && p.MediaItemId == w.MediaItemId && p.Watched && p.LastPlayedAt > w.AddedAt))
            .Select(w => w.Id).ToListAsync(ct);
        var started = await Live(profileId)
            .Where(w => w.Kind == MediaKind.Series && w.MediaItemId != null
                && db.MediaItems.IgnoreQueryFilters().Any(e => e.Kind == MediaKind.Episode && e.Parent!.ParentId == w.MediaItemId
                    && e.PlaybackStates.Any(p => p.UserId == profileId && (p.Watched || p.PositionSeconds > 0) && p.LastPlayedAt > w.AddedAt)))
            .Select(w => w.Id).ToListAsync(ct);
        foreach (var (ids, reason) in new[] { (watched, WatchlistRemoval.Watched), (started, WatchlistRemoval.Started) })
        {
            if (ids.Count > 0)
            {
                await db.WatchlistEntries.Where(w => ids.Contains(w.Id))
                    .ExecuteUpdateAsync(u => u.SetProperty(w => w.RemovedAt, DateTime.UtcNow).SetProperty(w => w.RemovedReason, reason), ct);
            }
        }

        cache.Set(stampKey, (lastPlay, linkedCount - watched.Count - started.Count + 1_000_000 * Volatile.Read(ref pruneGeneration)), TimeSpan.FromHours(1));
    }

    private static int pruneGeneration;

    /// <summary>
    /// Makes the next read of any watchlist look for titles to take off, whatever it saw last: for plays recorded with
    /// a past date (watched offline, imported from Plex), which a newest-play check can't see, and titles newly linked.
    /// </summary>
    public static void LookAgain() => Interlocked.Increment(ref pruneGeneration);

    /// <summary>
    /// Points titles that have come into the library (or come back to it) at their library item, among the titles this
    /// profile can see. One item belongs to one entry: a second entry for it (the same title added two ways before its
    /// ids matched) is a duplicate and goes, and a removed entry still holding it lets go. Saved by the caller.
    /// </summary>
    private async Task LinkAsync(string profileId, List<WatchlistEntry> entries, CancellationToken ct)
    {
        var waiting = entries.Where(e => e.MediaItemId is null && e.TmdbId is not null).ToList();
        if (waiting.Count == 0)
        {
            return;
        }

        var tmdb = waiting.Select(e => e.TmdbId!.Value.ToString(CultureInfo.InvariantCulture)).ToList();
        var found = await db.MediaItems.Where(i => i.ParentId == null && (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series) && tmdb.Contains(i.TmdbId!))
            .Select(i => new { i.Id, i.Kind, i.TmdbId }).ToListAsync(ct);
        if (found.Count == 0)
        {
            return;
        }

        var foundIds = found.Select(f => (Guid?)f.Id).ToList();
        var holders = await db.WatchlistEntries.Where(w => w.ProfileId == profileId && foundIds.Contains(w.MediaItemId)).ToListAsync(ct);
        foreach (var entry in waiting)
        {
            var id = entry.TmdbId!.Value.ToString(CultureInfo.InvariantCulture);
            if (found.FirstOrDefault(f => f.Kind == entry.Kind && f.TmdbId == id) is not { } match)
            {
                continue;
            }

            if (holders.FirstOrDefault(h => h.MediaItemId == match.Id && h.Id != entry.Id) is { } holder)
            {
                if (holder.RemovedAt is null)
                {
                    // The one already holding the title stands; it takes this one's ids if it hadn't any (added from the
                    // library before TMDB knew it), so Discover still marks it.
                    db.WatchlistEntries.Remove(entry);
                    entries.Remove(entry);
                    if (holder.TmdbId is null)
                    {
                        holder.TmdbId = entry.TmdbId;
                        holder.TvdbId ??= entry.TvdbId;
                        holder.ImdbId ??= entry.ImdbId;
                    }

                    continue;
                }

                holder.MediaItemId = null;
            }

            entry.MediaItemId = match.Id;
            holders.Add(entry);
            LookAgain();
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

        // Asked for through Flexarr already (by anyone) and not in yet: once is enough.
        if (await db.MediaRequests.AnyAsync(r => r.FulfilledAt == null && r.Kind == entry.Kind && r.TmdbId == tmdbId, ct))
        {
            return false;
        }

        var mediaType = entry.Kind == MediaKind.Movie ? "movie" : "tv";
        if (await SeerrFactsAsync(mediaType, tmdbId, ct) is not { Availability: AvailabilityDto.None } facts
            || ReferenceEquals(facts, Unanswered) || ReferenceEquals(facts, Unreadable))
        {
            return false; // Already asked for, partly in, or Seerr isn't answering (so it can't say).
        }

        try
        {
            await RequestEndpoints.PlaceAsync(seerr, instance, options.CurrentValue, db, profileId, mediaType, tmdbId, ct);
            cache.Remove(SeerrKey(mediaType, tmdbId));
            return true;
        }
        catch (Exception ex) when (SeerrFailed(ex, ct))
        {
            return false;
        }
    }

    // ---- Entries as the apps see them ----

    private async Task<WatchlistEntryDto> DtoAsync(string profileId, WatchlistEntry entry, CancellationToken ct)
    {
        var dto = (await ToDtosAsync(profileId, [entry], ct)).FirstOrDefault();
        await SaveLinksAsync(ct);
        if (dto is not null)
        {
            return dto;
        }

        // Linking found it's a second entry for a title already on the list: that one is the answer.
        var survivor = await Live(profileId).FirstAsync(w => w.Kind == entry.Kind && w.TmdbId == entry.TmdbId, ct);
        return (await ToDtosAsync(profileId, [survivor], ct))[0];
    }

    /// <summary>
    /// Saves what linking changed. Two loads at once can both merge the same duplicate; the second finds it gone,
    /// which changes nothing worth failing for (the next load sees the same).
    /// </summary>
    private async Task SaveLinksAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>Links, then works out each entry's status: from the library, Flexarr's requests and (in parallel) Seerr.</summary>
    private async Task<List<WatchlistEntryDto>> ToDtosAsync(string profileId, List<WatchlistEntry> entries, CancellationToken ct)
    {
        await LinkAsync(profileId, entries, ct);
        var ids = entries.Where(e => e.MediaItemId is not null).Select(e => e.MediaItemId!.Value).ToList();
        // Only what this profile can see (the query filter), and a show only once it has an episode to play.
        var items = (await db.MediaItems.Where(i => ids.Contains(i.Id) && (i.Kind == MediaKind.Movie || i.Children.Any(s => s.Children.Count > 0)))
            .Summaries(profileId).ToListAsync(ct)).ToDictionary(i => i.Id);
        // A profile with an age limit sees only what's in the library within it: a title outside has no age to check,
        // and one above the limit (put on before it was set) isn't shown at all.
        if (db.IsLimited())
        {
            entries = entries.Where(e => e.MediaItemId is { } id && items.ContainsKey(id)).ToList();
        }

        var outside = entries.Where(e => !(e.MediaItemId is { } id && items.ContainsKey(id)) && e.TmdbId is not null).ToList();
        var facts = await SeerrFactsAsync(outside.Select(e => (MediaType(e.Kind), e.TmdbId!.Value)), ct);
        var requested = await RequestedAsync(entries, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        return entries.Select(entry =>
        {
            if (entry.MediaItemId is { } id && items.TryGetValue(id, out var item))
            {
                return ToDto(entry, WatchlistStatus.Ready, item, null);
            }

            var known = entry.TmdbId is { } tmdbId ? facts.GetValueOrDefault(Key(entry.Kind, tmdbId)) : null;
            if (known?.ReleaseDate is { } released)
            {
                entry.ReleaseDate = released;
            }

            var status = entry.TmdbId is null ? WatchlistStatus.NotAvailable
                : requested.Contains(Key(entry.Kind, entry.TmdbId.Value)) || known?.Availability is AvailabilityDto.Requested or AvailabilityDto.PartlyAvailable or AvailabilityDto.Available
                    ? WatchlistStatus.OnItsWay
                    : entry.ReleaseDate > today ? WatchlistStatus.Coming : WatchlistStatus.NotAvailable;
            return ToDto(entry, status, null, known?.Streaming is { Count: > 0 } streaming ? streaming : null);
        }).ToList();
    }

    // ---- What Seerr knows about titles not in the library ----

    /// <param name="Streaming">Subscription services it's on in Seerr's streaming region, a few at most.</param>
    private sealed record SeerrFacts(AvailabilityDto Availability, DateOnly? ReleaseDate, IReadOnlyList<string> Streaming);

    /// <summary>Kept for a minute when Seerr doesn't answer, so a list isn't held up asking again.</summary>
    private static readonly SeerrFacts Unanswered = new(AvailabilityDto.None, null, []);

    /// <summary>Kept for a minute for one title Seerr answered badly (an error, a page that isn't JSON): Seerr itself is up.</summary>
    private static readonly SeerrFacts Unreadable = new(AvailabilityDto.None, null, []);

    /// <summary>Seerr didn't answer at all (a timeout, no connection), as opposed to answering badly about one title.</summary>
    private static bool SeerrUnreachable(Exception ex, CancellationToken ct) =>
        (ex is TaskCanceledException && !ct.IsCancellationRequested) || ex is HttpRequestException { StatusCode: null };

    private static string MediaType(MediaKind kind) => kind == MediaKind.Movie ? "movie" : "tv";

    private static string Key(MediaKind kind, int tmdbId) => $"{MediaType(kind)}:{tmdbId.ToString(CultureInfo.InvariantCulture)}";

    private static string SeerrKey(string mediaType, int tmdbId) => $"watchlist:seerr:{mediaType}:{tmdbId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>A timeout, a refusal, or an answer that isn't Seerr's (a proxy's login page): not this request being cancelled.</summary>
    private static bool SeerrFailed(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    /// <summary>Titles asked for through Flexarr and not in yet: "movie:123" / "tv:456".</summary>
    private async Task<HashSet<string>> RequestedAsync(List<WatchlistEntry> entries, CancellationToken ct)
    {
        var tmdb = entries.Where(e => e.TmdbId is not null).Select(e => e.TmdbId!.Value).ToList();
        return (await db.MediaRequests.Where(r => r.FulfilledAt == null && tmdb.Contains(r.TmdbId)).Select(r => new { r.Kind, r.TmdbId }).ToListAsync(ct))
            .Select(r => Key(r.Kind, r.TmdbId)).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>What Seerr says about each title ("movie:123" → facts), several at once; titles it can't say about are left out.</summary>
    private async Task<Dictionary<string, SeerrFacts>> SeerrFactsAsync(IEnumerable<(string MediaType, int TmdbId)> titles, CancellationToken ct)
    {
        var result = new System.Collections.Concurrent.ConcurrentDictionary<string, SeerrFacts>(StringComparer.Ordinal);
        var wanted = titles.Distinct().ToList();
        if (wanted.Count == 0 || options.CurrentValue.Seerr?.Instance is not { } instance)
        {
            return [];
        }

        // Once, before the lookups that need it.
        await RegionAsync(instance, ct);
        // The first title Seerr fails on stops the rest: a Seerr that hangs would otherwise cost every title its timeout.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await Parallel.ForEachAsync(wanted, new ParallelOptions { MaxDegreeOfParallelism = SeerrParallelism, CancellationToken = stop.Token }, async (title, token) =>
            {
                var facts = await SeerrFactsAsync(title.MediaType, title.TmdbId, token);
                if (ReferenceEquals(facts, Unanswered))
                {
                    await stop.CancelAsync();
                }
                else if (facts is not null && !ReferenceEquals(facts, Unreadable))
                {
                    result[$"{title.MediaType}:{title.TmdbId.ToString(CultureInfo.InvariantCulture)}"] = facts;
                }
            });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Stopped early: what was learnt before is used, the rest show without Seerr's word.
        }

        return new Dictionary<string, SeerrFacts>(result, StringComparer.Ordinal);
    }

    /// <summary>
    /// What Seerr says about a title, kept a few minutes; null without Seerr or for a title it doesn't know;
    /// <see cref="Unanswered"/> while Seerr isn't answering (kept a minute, for every title, so nothing waits on it).
    /// </summary>
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

        var downKey = $"watchlist:seerr:down:{instance.BaseUrl}";
        if (cache.TryGetValue(downKey, out _))
        {
            return Unanswered;
        }

        try
        {
            var region = await RegionAsync(instance, ct);
            var facts = await seerr.GetAsync(instance, $"{mediaType}/{tmdbId.ToString(CultureInfo.InvariantCulture)}", ct) is { } t ? Facts(t, region ?? "US") : null;
            // Streaming services worked out for a guessed region are only kept until the region can be asked again.
            cache.Set(key, facts, region is null ? SeerrFailureCacheFor : SeerrCacheFor);
            return facts;
        }
        catch (Exception ex) when (SeerrFailed(ex, ct))
        {
            // The list still shows; the title just can't say whether it's on its way. Only Seerr not answering at all
            // stops the others asking.
            if (!SeerrUnreachable(ex, ct))
            {
                cache.Set(key, Unreadable, SeerrFailureCacheFor);
                return Unreadable;
            }

            cache.Set(downKey, true, SeerrFailureCacheFor);
            return Unanswered;
        }
    }

    /// <summary>
    /// The country Seerr shows streaming services for (its Streaming Region setting, else US), kept an hour per Seerr;
    /// null when Seerr didn't say (asked again in a minute).
    /// </summary>
    private async Task<string?> RegionAsync(SeerrInstance instance, CancellationToken ct)
    {
        var key = $"watchlist:seerr:region:{instance.BaseUrl}";
        if (cache.TryGetValue(key, out string? known))
        {
            return known;
        }

        try
        {
            var main = await seerr.GetAsync(instance, "settings/main", ct);
            var region = (main is { } m ? Str(m, "streamingRegion") ?? Str(m, "region") : null) is { Length: 2 } r ? r.ToUpperInvariant() : "US";
            cache.Set(key, region, TimeSpan.FromHours(1));
            return region;
        }
        catch (Exception ex) when (SeerrFailed(ex, ct))
        {
            cache.Set(key, (string?)null, SeerrFailureCacheFor);
            if (SeerrUnreachable(ex, ct))
            {
                cache.Set($"watchlist:seerr:down:{instance.BaseUrl}", true, SeerrFailureCacheFor);
            }

            return null;
        }
    }

    private static SeerrFacts Facts(JsonElement t, string region)
    {
        int? status = t.TryGetProperty("mediaInfo", out var info) && info.ValueKind == JsonValueKind.Object && info.TryGetProperty("status", out var s)
            && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : null;
        return new SeerrFacts(RequestEndpoints.AvailabilityOf(status), DateOf(t), StreamingIn(t, region));
    }

    /// <summary>
    /// The subscription services TMDB lists for the title in the region, first three, leaving out the same service
    /// again with ads or through another's channel ("Amazon Prime Video with Ads", "HBO Max Amazon Channel").
    /// </summary>
    internal static IReadOnlyList<string> StreamingIn(JsonElement t, string region)
    {
        if (!t.TryGetProperty("watchProviders", out var regions) || regions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        foreach (var r in regions.EnumerateArray())
        {
            if (Str(r, "iso_3166_1") != region || !r.TryGetProperty("flatrate", out var flatrate) || flatrate.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            return flatrate.EnumerateArray().Select(p => Str(p, "name")).OfType<string>()
                .Where(n => !n.EndsWith(" with Ads", StringComparison.OrdinalIgnoreCase) && !n.EndsWith(" Channel", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
        }

        return [];
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

    /// <param name="includeRemoved">Also a title taken off before, to bring back rather than add twice.</param>
    private async Task<WatchlistEntry?> FindAsync(string profileId, WatchlistAdd key, bool includeRemoved, CancellationToken ct)
    {
        var mine = includeRemoved ? db.WatchlistEntries.Where(w => w.ProfileId == profileId) : Live(profileId);
        // A live entry before a removed one for the same title.
        mine = mine.OrderBy(w => w.RemovedAt != null);
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

    private static WatchlistEntryDto ToDto(WatchlistEntry e, WatchlistStatus status, ItemSummaryDto? item, IReadOnlyList<string>? streaming) => new(
        e.Id, MediaType(e.Kind), e.TmdbId, item?.Title ?? e.Title, item?.Year ?? e.Year,
        item?.Artwork.Poster ?? ImageEndpoints.RemoteUrl(e.PosterUrl), item?.Artwork.Backdrop ?? ImageEndpoints.RemoteUrl(e.BackdropUrl), e.AddedAt, status, e.ReleaseDate,
        item?.Id, item, e.Source == WatchlistSource.Trakt ? "trakt" : "flexarr", streaming);
}
