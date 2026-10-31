using System.Globalization;
using System.Net;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Trakt;

/// <summary>Trakt's ids for a library film or episode: the film's, or the episode's show's with its season and number.</summary>
public sealed record TraktTitle(MediaKind Kind, TraktIds Ids, int? Season = null, int? Number = null);

/// <summary>Queues changes for Trakt (see <see cref="TraktOutboxEntry"/>), for profiles that are connected.</summary>
public static class TraktOutbox
{
    /// <summary>Plays per queued history change: Trakt takes big batches, but a failure retries the whole of one.</summary>
    private const int HistoryBatch = 200;

    /// <summary>The Trakt ids of library films and episodes; those without any are left out.</summary>
    public static async Task<Dictionary<Guid, TraktTitle>> TitlesAsync(FlexarrDbContext db, IReadOnlyCollection<Guid> itemIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await db.MediaItems.IgnoreQueryFilters().AsNoTracking()
            .Where(i => itemIds.Contains(i.Id) && (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Episode))
            .Select(i => new
            {
                i.Id, i.Kind, i.TmdbId, i.TvdbId, i.ImdbId, i.SeasonNumber, i.EpisodeNumber,
                ShowTmdb = i.Parent!.Parent!.TmdbId, ShowTvdb = i.Parent.Parent.TvdbId, ShowImdb = i.Parent.Parent.ImdbId,
            })
            .ToListAsync(ct);
        var titles = new Dictionary<Guid, TraktTitle>();
        foreach (var r in rows)
        {
            if (r.Kind == MediaKind.Movie)
            {
                if (Ids(r.TmdbId, r.TvdbId, r.ImdbId) is { } ids)
                {
                    titles[r.Id] = new TraktTitle(MediaKind.Movie, ids);
                }
            }
            else if (r is { SeasonNumber: { } season, EpisodeNumber: { } number } && Ids(r.ShowTmdb, r.ShowTvdb, r.ShowImdb) is { } show)
            {
                titles[r.Id] = new TraktTitle(MediaKind.Episode, show, season, number);
            }
        }

        return titles;
    }

    public static TraktIds? Ids(string? tmdb, string? tvdb, string? imdb)
    {
        var ids = new TraktIds(
            Imdb: string.IsNullOrEmpty(imdb) ? null : imdb,
            Tmdb: int.TryParse(tmdb, NumberStyles.None, CultureInfo.InvariantCulture, out var t) ? t : null,
            Tvdb: int.TryParse(tvdb, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null);
        return ids.Imdb is null && ids.Tmdb is null && ids.Tvdb is null ? null : ids;
    }

    /// <summary>A <c>/sync</c> body for films and episodes, with their times if given.</summary>
    public static TraktSyncBody Body(IEnumerable<(TraktTitle Title, DateTime? At)> plays)
    {
        var list = plays.ToList();
        var movies = list.Where(p => p.Title.Kind == MediaKind.Movie).Select(p => new TraktSyncMovie(p.Title.Ids, p.At)).ToList();
        var shows = list.Where(p => p.Title.Kind == MediaKind.Episode)
            .GroupBy(p => p.Title.Ids)
            .Select(show => new TraktSyncShow(show.Key, show.GroupBy(p => p.Title.Season!.Value)
                .Select(season => new TraktSyncSeason(season.Key, season.Select(p => new TraktSyncEpisode(p.Title.Number!.Value, p.At)).ToList()))
                .ToList()))
            .ToList();
        return new TraktSyncBody(movies.Count > 0 ? movies : null, shows.Count > 0 ? shows : null);
    }

    public static Task<bool> ConnectedAsync(FlexarrDbContext db, string profileId, CancellationToken ct) =>
        db.TraktConnections.AnyAsync(c => c.ProfileId == profileId && c.Status == TraktStatus.Connected, ct);

    /// <summary>
    /// Queues plays for Trakt's history: marked watched, or watched away from the server (a download played offline).
    /// Streamed plays aren't, as their scrobble adds them. Saved by the caller.
    /// </summary>
    public static async Task QueueHistoryAsync(FlexarrDbContext db, string profileId, IReadOnlyCollection<(Guid ItemId, DateTime WatchedAt)> plays, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(plays);
        if (plays.Count == 0 || !await ConnectedAsync(db, profileId, ct))
        {
            return;
        }

        var titles = await TitlesAsync(db, plays.Select(p => p.ItemId).Distinct().ToList(), ct);
        var known = plays.Where(p => titles.ContainsKey(p.ItemId)).Select(p => (titles[p.ItemId], (DateTime?)p.WatchedAt.ToUniversalTime())).ToList();
        foreach (var chunk in known.Chunk(HistoryBatch))
        {
            db.TraktOutbox.Add(new TraktOutboxEntry
            {
                ProfileId = profileId, Kind = TraktOutboxKind.History, Payload = JsonSerializer.Serialize(Body(chunk), TraktJson.Options),
            });
        }

        TraktOutboxWorker.Nudge();
    }

    /// <summary>Queues a live scrobble; saved here, as it comes from playback rather than a request.</summary>
    public static async Task QueueScrobbleAsync(FlexarrDbContext db, string profileId, string action, TraktScrobbleBody body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        db.TraktOutbox.Add(new TraktOutboxEntry
        {
            ProfileId = profileId, Kind = TraktOutboxKind.Scrobble, Action = action, Payload = JsonSerializer.Serialize(body, TraktJson.Options),
        });
        await db.SaveChangesAsync(ct);
        TraktOutboxWorker.Nudge();
    }
}

/// <summary>
/// Sends queued changes to Trakt as they come, at most one write a second per account (Trakt's limit), retrying with
/// a growing wait while Trakt or the network is away.
/// </summary>
public sealed partial class TraktOutboxWorker(IServiceScopeFactory scopes, ILogger<TraktOutboxWorker> logger) : BackgroundService
{
    /// <summary>A scrobble that couldn't go for this long is stale: "watching now" would be wrong by then.</summary>
    internal static readonly TimeSpan ScrobbleStaleAfter = TimeSpan.FromMinutes(10);

    /// <summary>Changes still failing after this long go: something's wrong with them, not with Trakt. (Waiting for a sign-in doesn't count.)</summary>
    internal static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(7);

    private static readonly SemaphoreSlim Signal = new(0, 1);

    /// <summary>Something was queued: send it now rather than at the next look.</summary>
    public static void Nudge()
    {
        if (Signal.CurrentCount == 0)
        {
            try
            {
                Signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Nudged already.
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await SendDueAsync(scope.ServiceProvider, logger, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(ex);
            }

            await Signal.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    /// <summary>
    /// Sends everything due: scrobbles first (they go stale), then the rest, taking each profile in turn so one's
    /// backlog doesn't hold up another's. Each entry is saved as done once sent, so a restart never sends it twice.
    /// Returns how many went.
    /// </summary>
    internal static async Task<int> SendDueAsync(IServiceProvider services, ILogger logger, CancellationToken ct, TimeSpan? spacing = null)
    {
        var db = services.GetRequiredService<FlexarrDbContext>();
        var trakt = services.GetRequiredService<TraktClient>();
        var accounts = services.GetRequiredService<TraktAccounts>();
        var now = DateTime.UtcNow;
        var due = await db.TraktOutbox.Where(o => o.NextAttemptAt <= now).OrderBy(o => o.Id).Take(200).ToListAsync(ct);
        if (due.Count == 0)
        {
            return 0;
        }

        var connections = await db.TraktConnections.AsNoTracking().Select(c => new { c.ProfileId, c.Status }).ToDictionaryAsync(c => c.ProfileId, c => c.Status, ct);
        var ordered = due.OrderBy(o => o.Kind != TraktOutboxKind.Scrobble)
            .GroupBy(o => o.ProfileId)
            .SelectMany(g => g.Select((o, i) => (Entry: o, Turn: i)))
            .OrderBy(x => x.Entry.Kind != TraktOutboxKind.Scrobble).ThenBy(x => x.Turn).ThenBy(x => x.Entry.Id)
            .Select(x => x.Entry).ToList();
        var waiting = new HashSet<string>(StringComparer.Ordinal);
        var sent = 0;
        var lastWrite = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var gap = spacing ?? TimeSpan.FromSeconds(1.1);
        foreach (var entry in ordered)
        {
            if (db.Entry(entry).State == EntityState.Detached)
            {
                continue; // A newer scrobble went, and took this one with it.
            }

            if (!connections.TryGetValue(entry.ProfileId, out var status))
            {
                // Disconnected since it was queued: nobody to send it to.
                await DoneAsync(db, entry, ct);
                continue;
            }

            if (status != TraktStatus.Connected || waiting.Contains(entry.ProfileId))
            {
                // Signed out by Trakt, or no working token just now: kept for when it's back.
                Later(entry, TimeSpan.FromMinutes(30), "Waiting for the Trakt sign-in.");
                continue;
            }

            if (entry.Kind == TraktOutboxKind.Scrobble && DateTime.UtcNow - entry.CreatedAt > ScrobbleStaleAfter)
            {
                Stale(db, entry);
                await db.SaveChangesAsync(ct);
                continue;
            }

            if (lastWrite.TryGetValue(entry.ProfileId, out var last) && DateTime.UtcNow - last < gap)
            {
                await Task.Delay(gap - (DateTime.UtcNow - last), ct);
            }

            try
            {
                var (ok, _) = await accounts.WithTokenAsync(entry.ProfileId, async token =>
                {
                    await SendAsync(trakt, entry, token, ct);
                    return true;
                }, ct);
                lastWrite[entry.ProfileId] = DateTime.UtcNow;
                if (!ok)
                {
                    waiting.Add(entry.ProfileId);
                    Later(entry, TimeSpan.FromMinutes(30), "Waiting for the Trakt sign-in.");
                    continue;
                }

                sent++;
                if (entry.Kind == TraktOutboxKind.Scrobble)
                {
                    // Older scrobbles of this profile still waiting (a start that failed) would now arrive out of order.
                    var older = await db.TraktOutbox.Where(o => o.ProfileId == entry.ProfileId && o.Kind == TraktOutboxKind.Scrobble && o.Id < entry.Id).ToListAsync(ct);
                    db.TraktOutbox.RemoveRange(older);
                }

                await DoneAsync(db, entry, ct);
            }
            catch (TraktException ex)
            {
                lastWrite[entry.ProfileId] = DateTime.UtcNow;
                if (ex.AccountLimit && entry.Kind == TraktOutboxKind.WatchlistAdd)
                {
                    await db.TraktConnections.Where(c => c.ProfileId == entry.ProfileId).ExecuteUpdateAsync(u => u.SetProperty(c => c.WatchlistFull, true), ct);
                    await DoneAsync(db, entry, ct);
                }
                else if (ex.Status is null or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError || ex.Unauthorized)
                {
                    Retry(db, entry, ex);
                    await db.SaveChangesAsync(ct);
                    if (ex.Status == HttpStatusCode.TooManyRequests)
                    {
                        break; // Everything else waits too.
                    }
                }
                else
                {
                    // Trakt won't take it (it doesn't know the title, a malformed body): trying again won't help.
                    LogDropped(logger, entry.Kind, (int?)ex.Status);
                    await DoneAsync(db, entry, ct);
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return sent;
    }

    /// <summary>Takes a sent (or hopeless) entry off the queue, saved at once.</summary>
    private static async Task DoneAsync(FlexarrDbContext db, TraktOutboxEntry entry, CancellationToken ct)
    {
        db.TraktOutbox.Remove(entry);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Gone already (a disconnect cleared the queue meanwhile).
            foreach (var e in db.ChangeTracker.Entries<TraktOutboxEntry>().Where(e => e.State == EntityState.Deleted).ToList())
            {
                e.State = EntityState.Detached;
            }
        }
    }

    /// <summary>Puts an entry off without counting it a failure.</summary>
    private static void Later(TraktOutboxEntry entry, TimeSpan wait, string why)
    {
        entry.NextAttemptAt = DateTime.UtcNow + wait;
        entry.LastError = why;
    }

    private static Task SendAsync(TraktClient trakt, TraktOutboxEntry entry, string token, CancellationToken ct) => entry.Kind switch
    {
        TraktOutboxKind.Scrobble => trakt.ScrobbleAsync(entry.Action ?? "stop", JsonSerializer.Deserialize<TraktScrobbleBody>(entry.Payload, TraktJson.Options)!, token, ct),
        TraktOutboxKind.History => trakt.SyncAsync("/sync/history", Body(entry), token, ct),
        TraktOutboxKind.WatchlistAdd => trakt.SyncAsync("/sync/watchlist", Body(entry), token, ct),
        TraktOutboxKind.WatchlistRemove => trakt.SyncAsync("/sync/watchlist/remove", Body(entry), token, ct),
        _ => Task.CompletedTask,
    };

    private static TraktSyncBody Body(TraktOutboxEntry entry) => JsonSerializer.Deserialize<TraktSyncBody>(entry.Payload, TraktJson.Options)!;

    /// <summary>A scrobble too late to send: a finish still counts, so it goes to history with when it happened.</summary>
    private static void Stale(FlexarrDbContext db, TraktOutboxEntry entry)
    {
        db.TraktOutbox.Remove(entry);
        if (entry.Action != "stop" || JsonSerializer.Deserialize<TraktScrobbleBody>(entry.Payload, TraktJson.Options) is not { Progress: >= 80 } scrobble)
        {
            return;
        }

        var body = scrobble.Movie is { } movie
            ? new TraktSyncBody(Movies: [movie with { WatchedAt = entry.CreatedAt }])
            : scrobble is { Show: { } show, Episode: { } episode }
                ? new TraktSyncBody(Shows: [new TraktSyncShow(show.Ids, [new TraktSyncSeason(episode.Season, [new TraktSyncEpisode(episode.Number, entry.CreatedAt)])])])
                : null;
        if (body is not null)
        {
            db.TraktOutbox.Add(new TraktOutboxEntry { ProfileId = entry.ProfileId, Kind = TraktOutboxKind.History, Payload = JsonSerializer.Serialize(body, TraktJson.Options) });
        }
    }

    private static void Retry(FlexarrDbContext db, TraktOutboxEntry entry, TraktException ex)
    {
        if (DateTime.UtcNow - entry.CreatedAt > GiveUpAfter)
        {
            db.TraktOutbox.Remove(entry);
            return;
        }

        entry.Attempts++;
        entry.LastError = ex.Message;
        var wait = ex.RetryAfter ?? TimeSpan.FromSeconds(Math.Min(6 * 3600, 15 * Math.Pow(2, Math.Min(entry.Attempts, 12))));
        entry.NextAttemptAt = DateTime.UtcNow + wait;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Trakt outbox failed")]
    private partial void LogFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Trakt wouldn't take a queued {Kind} change ({Status}); dropped")]
    private static partial void LogDropped(ILogger logger, TraktOutboxKind kind, int? status);

}
