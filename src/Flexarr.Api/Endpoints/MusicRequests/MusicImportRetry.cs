using System.Collections.Concurrent;
using System.Text.Json;
using Flexarr.Core.Media;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Endpoints.MusicRequests;

/// <summary>
/// Lidarr grabs any release that looks like the album, then refuses it at import when tracks are missing (a bonus disc,
/// a single), and never tries another: it counts as a finished download, so its own redownload doesn't start. For
/// albums asked for in Flexarr, this blocklists such a release and searches again. A download with all its tracks that
/// merely scored low stays put for someone to import by hand: it's probably the right music.
/// <para>
/// Lidarr also adds an artist unmonitored when an album brings them in, whatever it's told, and then won't keep looking
/// for the album. So an artist a request added is monitored here, with the album, while the album is missing. Nothing
/// else of theirs is monitored, so this starts no discography. An artist Lidarr had already is the admin's to decide.
/// </para>
/// <para>
/// And Lidarr only honours "search when added" at the artist's next full scan, which can be its daily refresh when the
/// add itself didn't scan them. So a requested album it has never searched for is searched for here.
/// </para>
/// </summary>
public sealed partial class MusicImportRetry(IServiceScopeFactory scopes, ILogger<MusicImportRetry> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    /// <summary>Copies rejected per album, so an album only ever posted incomplete isn't chased for good.</summary>
    internal const int MaxRetries = 3;

    /// <summary>In Lidarr's rejections ("Has missing tracks", "Album match is not close enough: … [tracks, missing tracks]").</summary>
    private const string MissingTracks = "missing tracks";

    /// <summary>Kept in memory: a restart only starts the count afresh, and each try still rules out a release for good.</summary>
    private readonly ConcurrentDictionary<string, int> _retries = new(StringComparer.Ordinal);

    /// <summary>
    /// When an album was last searched for from here. Lidarr's own last search time is the lasting guard, but stays unset
    /// if the search never ran (no indexers, a stalled queue), and every sweep repeating it risks an indexer's limits.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> _searchedAt = new(StringComparer.Ordinal);

    /// <summary>
    /// Albums whose bad copy has gone but whose search failed, with their release groups: nothing in Lidarr's queue will
    /// bring them back to mind, so the next sweep searches first.
    /// </summary>
    private readonly ConcurrentDictionary<(string Lidarr, int Album), string> _searchOwed = new();

    /// <summary>
    /// What needs no asking Lidarr about until the time given: requests can sit open for two weeks, and each sweep calling
    /// for every album on them goes on all day. Forgotten by a restart, which only means asking once more.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> _settled = new(StringComparer.Ordinal);

    /// <summary>An album already searched for, or an artist added and left as it should be.</summary>
    private static readonly TimeSpan SettledFor = TimeSpan.FromHours(1);

    /// <summary>A searched-for album or one with files can't come back to needing a first search, short of being deleted and added again.</summary>
    private static readonly TimeSpan SearchedFor = TimeSpan.FromHours(6);

    /// <summary>A new request's album may not be in Lidarr until it has scanned the artist, so it's looked for every sweep to begin with.</summary>
    private static readonly TimeSpan NewFor = TimeSpan.FromDays(1);

    /// <summary>How long an album Lidarr still says it has never searched for waits before it's asked again.</summary>
    internal TimeSpan SearchAgainAfter { get; set; } = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await TickAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>One sweep, whose failure waits for the next: an exception leaving a background service stops the whole server.</summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        try
        {
            await SweepAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogSweepFailed(logger, ex);
        }
    }

    public async Task SweepAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var c = scope.ServiceProvider.GetRequiredService<MusicRequestContext>();
        if (c.Lidarrs.Count == 0)
        {
            return;
        }

        var since = DateTime.UtcNow - MusicMatcher.RequestedFor;
        var requests = await c.Db.MediaRequests.AsNoTracking()
            .Where(r => r.Kind == MediaKind.Album && r.FulfilledAt == null && r.RequestedAt > since && r.MusicBrainzId != null)
            .Select(r => new AlbumAsked(r.MusicBrainzId!, r.AddedArtist ? r.ArtistMusicBrainzId : null, r.RequestedAt)).ToListAsync(ct);
        if (requests.Count == 0)
        {
            return;
        }

        foreach (var lidarr in c.Lidarrs)
        {
            try
            {
                await SweepAsync(c, lidarr, requests, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                LogLidarrAway(logger, ex, lidarr.Name); // tried again at the next sweep
            }
        }
    }

    private sealed record AlbumAsked(string Album, string? ArtistItAdded, DateTime RequestedAt);

    /// <summary>The album starts afresh: its request was taken back, or someone's asked for it to be tried again.</summary>
    public void StartAfresh(ArrInstance lidarr, int albumId)
    {
        ArgumentNullException.ThrowIfNull(lidarr);
        _retries.TryRemove(Key(lidarr, albumId), out _);
        _searchedAt.TryRemove(Key(lidarr, albumId), out _);
        _searchOwed.TryRemove((lidarr.BaseUrl.ToString(), albumId), out _);
        foreach (var settled in _settled.Keys.Where(k => k.StartsWith($"{lidarr.BaseUrl}|", StringComparison.Ordinal)).ToList())
        {
            _settled.TryRemove(settled, out _);
        }
    }

    /// <summary>A search made elsewhere: the sweep's first search for this album needn't repeat it.</summary>
    public void Searched(ArrInstance lidarr, int albumId)
    {
        ArgumentNullException.ThrowIfNull(lidarr);
        NoteSearched(lidarr, [albumId]);
    }

    private static string Key(ArrInstance lidarr, int albumId) => $"{lidarr.BaseUrl}|{albumId}";

    private void NoteSearched(ArrInstance lidarr, IEnumerable<int> albumIds)
    {
        var now = DateTime.UtcNow;
        foreach (var old in _searchedAt.Where(s => now - s.Value >= SearchAgainAfter).Select(s => s.Key).ToList())
        {
            _searchedAt.TryRemove(old, out _);
        }

        foreach (var id in albumIds)
        {
            _searchedAt[Key(lidarr, id)] = now;
        }
    }

    private bool SearchedLately(ArrInstance lidarr, int albumId) =>
        _searchedAt.TryGetValue(Key(lidarr, albumId), out var at) && DateTime.UtcNow - at < SearchAgainAfter;

    private bool IsSettled(string key) => _settled.TryGetValue(key, out var until) && DateTime.UtcNow < until;

    private void Settle(string key, TimeSpan length)
    {
        var now = DateTime.UtcNow;
        foreach (var old in _settled.Where(s => now >= s.Value).Select(s => s.Key).ToList())
        {
            _settled.TryRemove(old, out _);
        }

        _settled[key] = now + length;
    }

    private async Task SweepAsync(MusicRequestContext c, ArrInstance lidarr, List<AlbumAsked> requests, CancellationToken ct)
    {
        var queue = await c.Client.GetQueueAsync(lidarr, ct);
        var asked = requests.Select(r => r.Album).ToHashSet(StringComparer.Ordinal);
        await PayOwedSearchesAsync(c, lidarr, asked, ct);
        var searched = await KeepLookingAsync(c, lidarr, requests, queue, ct);
        NoteSearched(lidarr, searched);
        foreach (var item in queue)
        {
            if (item.Album is not { } album || item.AlbumId is not { } albumId || album.ForeignAlbumId is not { } id || !asked.Contains(id) || !IsMissingTracks(item))
            {
                continue;
            }

            // A download that carries other albums too might be fine for them.
            if (item.DownloadId is not null && queue.Any(q => q.DownloadId == item.DownloadId && q.AlbumId != albumId))
            {
                continue;
            }

            var key = Key(lidarr, albumId);
            if (_retries.GetValueOrDefault(key) >= MaxRetries)
            {
                continue; // stays as "couldn't import"
            }

            if (!album.Monitored)
            {
                await c.Client.SetAlbumsMonitoredAsync(lidarr, [albumId], monitored: true, ct); // Lidarr keeps looking if this search finds nothing
            }

            // Only now does it use up a try: Lidarr being away for a moment says nothing about the album.
            await c.Client.BlocklistFromQueueAsync(lidarr, item.Id, ct);
            _retries.AddOrUpdate(key, 1, (_, tries) => tries + 1);
            _searchOwed[(lidarr.BaseUrl.ToString(), albumId)] = id;
            await c.Client.SearchAlbumsAsync(lidarr, [albumId], ct);
            _searchOwed.TryRemove((lidarr.BaseUrl.ToString(), albumId), out _);
            MusicRequested.Forget(c.Cache);
            LogRetrying(logger, album.Title, item.Title);
        }

        await SearchFirstTimeAsync(c, lidarr, requests, queue, searched, ct);
    }

    /// <summary>The searches a blocklisted copy still needs. One still asked for: a request taken back wants nothing more.</summary>
    private async Task PayOwedSearchesAsync(MusicRequestContext c, ArrInstance lidarr, HashSet<string> asked, CancellationToken ct)
    {
        foreach (var ((home, albumId), releaseGroup) in _searchOwed.Where(o => o.Key.Lidarr == lidarr.BaseUrl.ToString()).ToList())
        {
            if (asked.Contains(releaseGroup))
            {
                await c.Client.SearchAlbumsAsync(lidarr, [albumId], ct);
                MusicRequested.Forget(c.Cache);
            }

            _searchOwed.TryRemove((home, albumId), out _);
        }
    }

    /// <summary>Searches for the requested albums Lidarr has never looked for. Once it has, finding them is up to its RSS sync.</summary>
    private async Task SearchFirstTimeAsync(MusicRequestContext c, ArrInstance lidarr, List<AlbumAsked> requests, IReadOnlyList<ArrQueueItem> queue, HashSet<int> searched, CancellationToken ct)
    {
        var search = new List<int>();
        var now = DateTime.UtcNow;
        foreach (var asked in requests.GroupBy(r => r.Album, StringComparer.Ordinal))
        {
            var key = $"{lidarr.BaseUrl}|album|{asked.Key}";
            if (IsSettled(key))
            {
                continue;
            }

            var albums = await c.Client.GetAlbumsByForeignIdAsync(lidarr, asked.Key, ct);
            search.AddRange(albums
                .Where(a => AwaitsFirstSearch(a, now) && !searched.Contains(a.Id) && !SearchedLately(lidarr, a.Id) && !queue.Any(q => q.AlbumId == a.Id))
                .Select(a => a.Id));
            if (albums.Count > 0 && albums.All(a => a.LastSearchTime is not null || a.Statistics is { TrackFileCount: > 0 }))
            {
                Settle(key, SearchedFor);
            }
            else if (now - asked.Max(r => r.RequestedAt) >= NewFor)
            {
                Settle(key, SettledFor);
            }
        }

        if (search.Count > 0)
        {
            await c.Client.SearchAlbumsAsync(lidarr, search, ct);
            NoteSearched(lidarr, search);
            MusicRequested.Forget(c.Cache);
        }
    }

    /// <summary>
    /// Monitored, out already, with no files and never searched for. An album with no tracks yet hasn't been scanned:
    /// a search now would have no running time to judge a release's size by, so it waits for the next sweep.
    /// </summary>
    private static bool AwaitsFirstSearch(LidarrAlbum album, DateTime now) =>
        album is { Monitored: true, LastSearchTime: null, Statistics: { TrackCount: > 0, TrackFileCount: 0 } }
        && !(album.ReleaseDate > now);

    /// <summary>
    /// Monitors the artists requests added, and their albums still missing, and searches for what that just put back on
    /// the list. Returns the albums it searched for.
    /// </summary>
    private async Task<HashSet<int>> KeepLookingAsync(MusicRequestContext c, ArrInstance lidarr, List<AlbumAsked> requests, IReadOnlyList<ArrQueueItem> queue, CancellationToken ct)
    {
        var searched = new HashSet<int>();
        var byArtist = requests.Where(r => r.ArtistItAdded is not null).ToLookup(r => r.ArtistItAdded!, r => r.Album, StringComparer.Ordinal);
        if (byArtist.Count == 0)
        {
            return searched;
        }

        // Which albums go with it is in the key: a new request for the artist needs its look straight away.
        string Key(string artistId) => $"{lidarr.BaseUrl}|artist|{artistId}|{string.Join(',', byArtist[artistId].Order(StringComparer.Ordinal))}";
        if (byArtist.All(a => IsSettled(Key(a.Key))))
        {
            return searched;
        }

        var changed = false;
        var artists = await MusicMatcher.LidarrArtistsAsync(c.Client, lidarr, c.Cache, ct);
        if (artists.Count > 0)
        {
            // Taken out of Lidarr since: nothing here will bring it back, so no reading its whole list for it each sweep.
            var there = artists.Select(a => a.ForeignArtistId).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in byArtist.Where(a => !there.Contains(a.Key)))
            {
                Settle(Key(gone.Key), SettledFor);
            }
        }

        foreach (var artist in artists)
        {
            if (artist.ForeignArtistId is not { } artistId || !byArtist.Contains(artistId) || IsSettled(Key(artistId)))
            {
                continue;
            }

            var missing = (await c.Client.GetArtistAlbumsAsync(lidarr, artist.Id, ct))
                .Where(a => a.ForeignAlbumId is { } id && byArtist[artistId].Contains(id, StringComparer.Ordinal)
                    && !(a.Statistics is { TrackFileCount: > 0 } s && s.TrackFileCount >= s.TrackCount))
                .ToList();
            var unmonitored = missing.Where(a => !a.Monitored).Select(a => a.Id).ToList();
            if (missing.Count == 0 || (artist.Monitored && unmonitored.Count == 0))
            {
                Settle(Key(artistId), SettledFor);
                continue;
            }

            if (!artist.Monitored)
            {
                await c.Client.MonitorArtistAsync(lidarr, artist.Id, ct);
            }

            if (unmonitored.Count > 0)
            {
                await c.Client.SetAlbumsMonitoredAsync(lidarr, unmonitored, monitored: true, ct);
            }

            var search = missing.Where(a => !queue.Any(q => q.AlbumId == a.Id)).Select(a => a.Id).ToList();
            if (search.Count > 0)
            {
                await c.Client.SearchAlbumsAsync(lidarr, search, ct);
                searched.UnionWith(search);
            }

            changed = true;
        }

        if (changed)
        {
            MusicMatcher.Forget(c.Cache, lidarr);
            MusicRequested.Forget(c.Cache);
        }

        return searched;
    }

    private static bool IsMissingTracks(ArrQueueItem item) =>
        item.TrackedDownloadState == "importFailed"
        && item.StatusMessages?.Any(s => s.Messages?.Any(m => m.Contains(MissingTracks, StringComparison.OrdinalIgnoreCase)) == true) == true;

    [LoggerMessage(Level = LogLevel.Information, Message = "{Release} is missing tracks of {Album}: blocklisted it and searching again")]
    private static partial void LogRetrying(ILogger logger, string album, string? release);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Couldn't check {Lidarr}'s queue for failed imports")]
    private static partial void LogLidarrAway(ILogger logger, Exception ex, string lidarr);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't check requested albums for failed imports")]
    private static partial void LogSweepFailed(ILogger logger, Exception ex);
}
