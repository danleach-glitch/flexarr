using System.Globalization;
using Flexarr.Api.Configuration;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Flexarr.Api.Endpoints;

/// <summary>Take back a film or show request.</summary>
/// <param name="MediaType">"movie" or "tv".</param>
/// <param name="Seasons">For a show, just these seasons; null or empty for every season that isn't in yet.</param>
public sealed record TitleUndoBody(string MediaType, int TmdbId, IReadOnlyList<int>? Seasons = null);

/// <summary>
/// Taking a film or show request back, as music's unrequest does: Seerr's request goes, Radarr or Sonarr stop wanting
/// it and drop its downloads, and a film or show with nothing on disk leaves them. Files are never deleted. Seerr,
/// Radarr and Sonarr are the household's, so it's for everyone: every profile's open request for the title closes.
/// Nothing to take back (already undone, in the library, in a library the profile can't see, or never requested:
/// added in Radarr or Sonarr by hand) answers 204 and changes nothing. Throws <see cref="ServiceException"/> when one of them refuses or doesn't answer.
/// </summary>
public static class RequestUndo
{
    /// <summary>Which service failed, for a plain message (their own words can name addresses and go to the log).</summary>
    /// <remarks>The inner exception is an <see cref="HttpRequestException"/>, or a timeout.</remarks>
    public sealed class ServiceException(string service, Exception inner) : Exception($"{service} didn't answer.", inner)
    {
        public string Service { get; } = service;
    }

    /// <summary>Seerr's request states that mean nothing is left to undo.</summary>
    private const int Declined = 3, Completed = 5;

    /// <summary>Seerr's title states: partly in, all in.</summary>
    private const int PartlyAvailable = 4, Available = 5;

    public static async Task<IResult> UnrequestAsync(TitleUndoBody body, SeerrInstance seerr, SeerrClient seerrClient, ArrClient arr, FlexarrOptions options,
        FlexarrDbContext db, IMemoryCache cache, CancellationToken ct)
    {
        var movie = body.MediaType == "movie";
        var kind = RequestEndpoints.KindOf(body.MediaType);
        var tmdb = body.TmdbId.ToString(CultureInfo.InvariantCulture);

        // In a library the profile can't see (someone else's films): not theirs to undo. In one it can, and not just
        // a missing season: it's in, and there's nothing to take back.
        var inLibrary = await db.MediaItems.IgnoreQueryFilters().Where(i => i.Kind == kind && i.TmdbId == tmdb).Select(i => i.Id).ToListAsync(ct);
        var visible = inLibrary.Count == 0 ? [] : await db.MediaItems.Where(i => inLibrary.Contains(i.Id)).Select(i => i.Id).ToListAsync(ct);
        if (inLibrary.Count > 0 && visible.Count == 0)
        {
            return Results.NoContent();
        }

        var record = await Call("Seerr", () => seerrClient.MediaAsync(seerr, body.MediaType, body.TmdbId, ct));
        if (movie && (visible.Count > 0 || record?.Status == Available))
        {
            return Results.NoContent();
        }

        var chosen = !movie && body.Seasons is { Count: > 0 } given ? given.Where(n => n > 0).ToHashSet() : null;
        var open = (record?.Requests ?? []).Where(r => r.Status is not (Declined or Completed)).ToList();
        var onDisk = new HashSet<int>();

        // Only a real request is taken back. Seerr reads a title someone added in Radarr or Sonarr by hand as
        // processing, so it can show as requested; with no open request in Seerr or Flexarr, nobody asked for it.
        if (open.Count == 0 && !await db.MediaRequests.AnyAsync(r => r.Kind == kind && r.TmdbId == body.TmdbId && r.FulfilledAt == null, ct))
        {
            return Results.NoContent();
        }

        // Not in a library yet, and headed for one the profile can't see (the Requested list leaves it out, too): not
        // theirs to undo.
        if (inLibrary.Count == 0 && !await HeadedWhereSeenAsync(movie, body.TmdbId, record, open, arr, options, db, ct))
        {
            return Results.NoContent();
        }

        // Radarr or Sonarr first: a request deleted in Seerr while they still want it would download anyway.
        if (movie)
        {
            foreach (var radarr in options.Arr.Where(a => a.Kind == ArrKind.Radarr))
            {
                foreach (var film in await Call("Radarr", () => arr.GetMoviesByTmdbIdAsync(radarr, body.TmdbId, ct)))
                {
                    if (film.HasFile)
                    {
                        continue; // it's in (a library Flexarr hasn't scanned yet): never touched
                    }

                    await Call("Radarr", () => DropDownloadsAsync(arr, radarr, q => q.MovieId == film.Id, ct));
                    await Call("Radarr", () => arr.RemoveMovieAsync(radarr, film.Id, ct));
                }
            }
        }
        else
        {
            var tvdbId = record?.TvdbId ?? open.Select(r => r.Media?.TvdbId).FirstOrDefault(t => t is > 0)
                ?? (await db.MediaItems.IgnoreQueryFilters().Where(i => i.Kind == MediaKind.Series && i.TmdbId == tmdb && i.TvdbId != null)
                    .Select(i => i.TvdbId).FirstOrDefaultAsync(ct) is { } stored && int.TryParse(stored, CultureInfo.InvariantCulture, out var parsed) ? parsed : null);
            // The whole show means the seasons its open requests asked for, not every season without files: one
            // the admin monitored by hand in Sonarr isn't anyone's request. Without Seerr's seasons, every one not yet in.
            var asked = open.SelectMany(r => r.Seasons ?? []).Select(s => s.SeasonNumber).Where(n => n > 0).ToHashSet();
            if (tvdbId is { } tvdb)
            {
                foreach (var sonarr in options.Arr.Where(a => a.Kind == ArrKind.Sonarr))
                {
                    foreach (var series in await Call("Sonarr", () => arr.GetSeriesByTvdbIdAsync(sonarr, tvdb, ct)))
                    {
                        (chosen, var complete) = await UndoSeasonsAsync(arr, sonarr, series, chosen, asked.Count > 0 ? asked : null, ct);
                        onDisk.UnionWith(complete);
                    }
                }
            }
        }

        // Then Seerr: every request the undo covers. A show's request that also asked for seasons still wanted stays;
        // seasons already on disk don't keep one open, or the whole show's undo could never close it.
        var covered = open.Where(r => movie || chosen is null
            || (r.Seasons is { Count: > 0 } seasons && seasons.All(s => chosen.Contains(s.SeasonNumber) || onDisk.Contains(s.SeasonNumber)))).ToList();
        foreach (var request in covered)
        {
            await Call("Seerr", () => seerrClient.DeleteRequestAsync(seerr, request.Id, ct));
        }

        // With none left, Seerr's own record goes too, so it reads as not requested rather than still processing. A
        // show that's partly in keeps it: Seerr's next sync with Sonarr keeps that right.
        var left = open.Count - covered.Count;
        if (record is { } media && left == 0 && media.Status is not (PartlyAvailable or Available))
        {
            await Call("Seerr", () => seerrClient.DeleteMediaAsync(seerr, media.Id, ct));
        }

        if (left == 0)
        {
            foreach (var asked in await db.MediaRequests.Where(r => r.Kind == kind && r.TmdbId == body.TmdbId && r.FulfilledAt == null).ToListAsync(ct))
            {
                asked.FulfilledAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
        }

        foreach (var showId in movie ? [] : inLibrary)
        {
            ShowMissingEndpoints.Forget(cache, options, showId);
        }

        UpcomingEndpoints.ForgetArrCalls();
        RequestRequested.Forget();
        // The watchlist keeps what Seerr said for hours: it said requested, and isn't any more.
        Watchlist.WatchlistService.Forget(cache, body.MediaType, body.TmdbId);
        return Results.NoContent();
    }

    /// <summary>
    /// Whether a title that isn't in a library yet is headed for one the profile can see, worked out as the Requested
    /// list does (<see cref="RequestRequested.ListAsync"/>): the folder Radarr or Sonarr keeps it in, else the one
    /// Seerr sent it to, through the path mappings; else the oldest library of its kind. Radarr and Sonarr are only
    /// asked when some library of its kind is hidden from the profile.
    /// </summary>
    private static async Task<bool> HeadedWhereSeenAsync(bool movie, int tmdbId, SeerrMediaRecord? record, List<SeerrRequest> open, ArrClient arr, FlexarrOptions options,
        FlexarrDbContext db, CancellationToken ct)
    {
        var type = movie ? LibraryType.Movies : LibraryType.TvShows;
        var libraries = await db.Libraries.IgnoreQueryFilters().AsNoTracking().Where(l => l.Type == type).Select(l => new { l.Id, l.CreatedAt }).ToListAsync(ct);
        var seen = await db.Libraries.Where(l => l.Type == type).Select(l => l.Id).ToListAsync(ct);
        if (seen.Count == libraries.Count)
        {
            return true; // nothing of its kind is hidden from this profile
        }

        string? path = null;
        if (movie)
        {
            foreach (var radarr in options.Arr.Where(a => a.Kind == ArrKind.Radarr))
            {
                path ??= (await Call("Radarr", () => arr.GetMoviesByTmdbIdAsync(radarr, tmdbId, ct))).Select(f => f.Path).FirstOrDefault(p => !string.IsNullOrEmpty(p));
            }
        }
        else if ((record?.TvdbId ?? open.Select(r => r.Media?.TvdbId).FirstOrDefault(t => t is > 0)) is { } tvdb)
        {
            foreach (var sonarr in options.Arr.Where(a => a.Kind == ArrKind.Sonarr))
            {
                path ??= (await Call("Sonarr", () => arr.GetSeriesByTvdbIdAsync(sonarr, tvdb, ct))).Select(s => s.Path).FirstOrDefault(p => !string.IsNullOrEmpty(p));
            }
        }

        path ??= open.Select(r => r.RootFolder).FirstOrDefault(f => !string.IsNullOrEmpty(f));
        var local = string.IsNullOrEmpty(path) ? null : PathMapper.Map(path, options.PathMappings);
        var created = libraries.ToDictionary(l => l.Id, l => l.CreatedAt);
        var folders = (await db.LibraryFolders.IgnoreQueryFilters().AsNoTracking().Select(f => new { f.LibraryId, f.Path }).ToListAsync(ct))
            .Where(f => created.ContainsKey(f.LibraryId)).ToList();
        var homes = local is null ? [] : folders.Where(f => PathComparison.IsUnder(local, f.Path, PathComparison.FileSystemComparison)).Select(f => f.LibraryId).Distinct().ToList();
        if (homes.Count == 0 && folders.OrderBy(f => created[f.LibraryId]).ThenBy(f => f.LibraryId).FirstOrDefault() is { } oldest)
        {
            homes = [oldest.LibraryId];
        }

        return homes.Any(seen.Contains);
    }

    /// <summary>
    /// Sonarr stops wanting the seasons (given, or else those asked for, or every one, that aren't all on disk) and
    /// their downloads go; a partly downloaded season keeps its files. A show with nothing on disk at all, and no
    /// season still wanted, leaves Sonarr.
    /// </summary>
    /// <returns>The seasons undone, for matching Seerr's requests (null for "the whole show"), and the seasons all on disk.</returns>
    private static async Task<(HashSet<int>? Undone, HashSet<int> Complete)> UndoSeasonsAsync(ArrClient arr, ArrInstance sonarr, SonarrSeries series,
        HashSet<int>? chosen, HashSet<int>? asked, CancellationToken ct)
    {
        var episodes = await Call("Sonarr", () => arr.GetEpisodesAsync(sonarr, series.Id, ct));
        var seasonsWithFiles = episodes.Where(e => e.HasFile).Select(e => e.SeasonNumber).ToHashSet();

        // A season is on disk only when every wanted episode that has aired has its file: one still part way through
        // keeps downloading otherwise, while its request closes. An episode the admin unmonitored doesn't count against
        // it, and one with no air date counts as aired, since its season (it has files) has started.
        var now = DateTime.UtcNow;
        var complete = seasonsWithFiles.Where(n => episodes.All(e => e.SeasonNumber != n || e.HasFile || !e.Monitored || e.AirDateUtc > now)).ToHashSet();
        var scope = chosen ?? [.. (asked ?? series.Seasons.Select(s => s.SeasonNumber)).Where(n => n > 0 && !complete.Contains(n))];

        var missing = episodes.Where(e => scope.Contains(e.SeasonNumber) && !e.HasFile && e.Monitored).Select(e => e.Id).ToList();
        if (missing.Count > 0)
        {
            await Call("Sonarr", () => arr.SetEpisodesMonitoredAsync(sonarr, missing, monitored: false, ct));
        }

        await Call("Sonarr", () => arr.SetSeasonsMonitoredAsync(sonarr, series.Id, scope, monitored: false, ct));
        var inScope = episodes.Where(e => scope.Contains(e.SeasonNumber)).Select(e => e.Id).ToHashSet();
        await Call("Sonarr", () => DropDownloadsAsync(arr, sonarr, q => q.SeriesId == series.Id && q.EpisodeId is { } e && inScope.Contains(e), ct));

        var stillWanted = series.Seasons.Any(s => s.Monitored && s.SeasonNumber > 0 && !scope.Contains(s.SeasonNumber));
        if (seasonsWithFiles.Count == 0 && !stillWanted)
        {
            await Call("Sonarr", () => arr.RemoveSeriesAsync(sonarr, series.Id, ct));
        }

        return (chosen is null && seasonsWithFiles.Count == 0 ? null : scope, complete);
    }

    /// <summary>
    /// Takes the title's downloads out of the queue. A download that also carries something else (a pack with other
    /// seasons, say) stays, as music's does: removing it would stop the rest importing too.
    /// </summary>
    private static async Task DropDownloadsAsync(ArrClient arr, ArrInstance instance, Func<ArrQueueItem, bool> mine, CancellationToken ct)
    {
        var queue = await arr.GetQueueAsync(instance, ct);
        var ours = queue.Where(mine).ToList();
        var shared = queue.Where(q => !mine(q) && q.DownloadId is not null).Select(q => q.DownloadId!).ToHashSet();
        foreach (var queued in ours.Where(q => q.DownloadId is null || !shared.Contains(q.DownloadId)))
        {
            await arr.RemoveFromQueueAsync(instance, queued.Id, ct);
        }
    }

    private static async Task<T> Call<T>(string service, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (HttpRequestException ex)
        {
            throw new ServiceException(service, ex);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            throw new ServiceException(service, ex); // HttpClient's own timeout, not the caller going away
        }
    }

    private static async Task Call(string service, Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (HttpRequestException ex)
        {
            throw new ServiceException(service, ex);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            throw new ServiceException(service, ex); // HttpClient's own timeout, not the caller going away
        }
    }
}
