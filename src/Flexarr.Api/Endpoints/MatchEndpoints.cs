using System.Globalization;
using System.Text;
using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Endpoints;

/// <summary>
/// A show (by TVDB id) or film (by TMDB id) a library item could be, from Sonarr's or Radarr's search. ManagedBy names
/// the instance that already has it, and ManagedPath is where (as Flexarr sees it).
/// </summary>
public sealed record MatchCandidateDto(int Id, string Title, int? Year, string? Overview, string? Poster, string? Network, bool Current, string? ManagedBy, string? ManagedPath);

public sealed record FixMatchRequest(int Id);

/// <summary>
/// Added: the title was new to Sonarr or Radarr and was added (unmonitored, no search). Complete: they had finished
/// reading the folder, so episode details and cast are in; otherwise the next refresh fills them in.
/// </summary>
public sealed record FixMatchResultDto(string Instance, bool Added, bool Complete);

/// <summary>
/// Fix match: choose which show or film a library folder really is. The choice is kept through refreshes, and the
/// title is always managed by Sonarr or Radarr (added there when it isn't already).
/// </summary>
public static partial class MatchEndpoints
{
    private static readonly TimeSpan ArrReadTimeout = TimeSpan.FromSeconds(20);

    public static RouteGroupBuilder MapMatchEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/items/{id:guid}/match", async (Guid id, string? q, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, ArrClient arr, CancellationToken ct) =>
        {
            var item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id && (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series), ct);
            if (item is null)
            {
                return Results.NotFound();
            }

            var config = options.CurrentValue;
            var kind = KindFor(item);
            var instances = config.Arr.Where(a => a.Kind == kind).ToList();
            if (instances.Count == 0)
            {
                return Results.Problem($"Fixing a match needs {kind}. Add it under Settings › Sonarr and Radarr.", statusCode: 409);
            }

            var term = string.IsNullOrWhiteSpace(q) ? item.Title : q.Trim();
            try
            {
                var managed = await ManagedAsync(arr, instances, config.PathMappings, ct);
                var current = item.Kind == MediaKind.Movie ? item.TmdbId : item.TvdbId;
                var results = item.Kind == MediaKind.Movie
                    ? (await arr.LookupMoviesAsync(instances[0], term, ct)).Where(m => m.TmdbId > 0)
                        .Select(m => (Id: m.TmdbId, m.Title, m.Year, m.Overview, Poster: Poster(m.Images, m.RemotePoster), Network: m.Studio))
                    : (await arr.LookupSeriesAsync(instances[0], term, ct)).Where(s => s.TvdbId > 0)
                        .Select(s => (Id: s.TvdbId, s.Title, s.Year, s.Overview, Poster: Poster(s.Images, s.RemotePoster), s.Network));

                return Results.Ok(results.Take(20).Select(r =>
                {
                    var owner = managed.GetValueOrDefault(r.Id);
                    return new MatchCandidateDto(r.Id, r.Title, r.Year > 0 ? r.Year : null, NullIfEmpty(r.Overview), r.Poster, NullIfEmpty(r.Network),
                        r.Id.ToString(CultureInfo.InvariantCulture) == current, owner?.Instance.Name, owner?.Path);
                }).ToList());
            }
            catch (HttpRequestException ex)
            {
                return Results.Problem($"{instances[0].Name} didn't answer the search ({(int?)ex.StatusCode ?? 0}).", statusCode: 502);
            }
        }).WithTags("Match").RequireAuthorization(FlexarrClaims.AdminPolicy);

        api.MapPost("/items/{id:guid}/match", async (Guid id, FixMatchRequest request, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, ArrClient arr,
            MetadataRefresher refresher, ILoggerFactory logs, CancellationToken ct) =>
        {
            var item = await db.MediaItems.Include(i => i.Genres).Include(i => i.Credits)
                .FirstOrDefaultAsync(i => i.Id == id && (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series), ct);
            if (item is null)
            {
                return Results.NotFound();
            }

            var config = options.CurrentValue;
            var kind = KindFor(item);
            var noun = item.Kind == MediaKind.Movie ? "film" : "show";
            var instances = config.Arr.Where(a => a.Kind == kind).ToList();
            if (instances.Count == 0)
            {
                return Results.Problem($"Fixing a match needs {kind}. Add it under Settings › Sonarr and Radarr.", statusCode: 409);
            }

            ArrInstance instance;
            var added = false;
            var complete = true;
            try
            {
                var managed = await ManagedAsync(arr, instances, config.PathMappings, ct);
                if (managed.GetValueOrDefault(request.Id) is { } existing)
                {
                    instance = existing.Instance;
                }
                else
                {
                    // Sonarr and Radarr keep one title per folder: if they have this one as something else, that's
                    // theirs to change, and deleting it here would be too easy to get wrong.
                    if (item.Path is not null && managed.Values.FirstOrDefault(m => m.Path is not null && SamePath(m.Path, item.Path)) is { } owner)
                    {
                        return Results.Problem(
                            $"{owner.Instance.Name} already uses this folder for {owner.Title}. Remove that {noun} in {owner.Instance.Name} (keep its files), then fix the match again.",
                            statusCode: 409);
                    }

                    if (item.Path is null)
                    {
                        return Results.Problem($"Flexarr doesn't know this {noun}'s folder, so it can't add it to {kind}.", statusCode: 409);
                    }

                    var arrPath = PathMapper.Unmap(item.Path, config.PathMappings).TrimEnd('/').Normalize(NormalizationForm.FormC);
                    instance = await InstanceForAsync(arr, instances, arrPath, ct);
                    var profile = await QualityProfileAsync(arr, instance, managed.Values, ct);
                    if (item.Kind == MediaKind.Movie)
                    {
                        if (await arr.LookupMoviesAsync(instance, $"tmdb:{request.Id}", ct) is not [var found, ..])
                        {
                            return Results.Problem($"{instance.Name} couldn't find that film any more.", statusCode: 404);
                        }

                        var movie = await arr.AddMovieAsync(instance, request.Id, found.Title, arrPath, profile, ct);
                        complete = await WaitForAsync(async () => (await arr.GetMovieAsync(instance, movie.Id, ct)).HasFile, ct);
                    }
                    else
                    {
                        if (await arr.LookupSeriesAsync(instance, $"tvdb:{request.Id}", ct) is not [var found, ..])
                        {
                            return Results.Problem($"{instance.Name} couldn't find that show any more.", statusCode: 404);
                        }

                        var series = await arr.AddSeriesAsync(instance, request.Id, found.Title, arrPath, profile, ct);
                        complete = await WaitForAsync(async () => (await arr.GetEpisodesAsync(instance, series.Id, ct)).Any(e => e.HasFile), ct);
                    }

                    added = true;
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.BadRequest)
            {
                return Results.Problem($"{kind} wouldn't add it: {ex.Message}", statusCode: 409);
            }
            catch (HttpRequestException ex)
            {
                return Results.Problem($"{kind} didn't answer ({(int?)ex.StatusCode ?? 0}), so the match wasn't changed.", statusCode: 502);
            }

            // Forget everything the wrong match brought, so none of it carries over.
            item.TvdbId = item.Kind == MediaKind.Series ? request.Id.ToString(CultureInfo.InvariantCulture) : null;
            item.TmdbId = item.Kind == MediaKind.Movie ? request.Id.ToString(CultureInfo.InvariantCulture) : null;
            item.ImdbId = null;
            item.TvMazeId = null;
            item.ArrId = null;
            item.MatchLocked = true;
            db.Credits.RemoveRange(item.Credits);
            item.Credits.Clear();

            await refresher.RefreshItemAsync(item, ct);
            if (!complete)
            {
                item.MetadataUpdatedAt = null; // The next refresh picks up the episodes and cast.
            }

            await db.SaveChangesAsync(ct);
            LogFixed(logs.CreateLogger("Flexarr.Match"), item.Path ?? item.Title, item.Title, item.Year, instance.Name, added);
            return Results.Ok(new FixMatchResultDto(instance.Name, added, complete));
        }).WithTags("Match").RequireAuthorization(FlexarrClaims.AdminPolicy);

        // Back to automatic matching (by folder, then by title).
        api.MapDelete("/items/{id:guid}/match", async (Guid id, FlexarrDbContext db, MetadataRefresher refresher, CancellationToken ct) =>
        {
            var item = await db.MediaItems.Include(i => i.Genres).Include(i => i.Credits)
                .FirstOrDefaultAsync(i => i.Id == id && (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series), ct);
            if (item is null)
            {
                return Results.NotFound();
            }

            item.MatchLocked = false;
            await refresher.RefreshItemAsync(item, ct);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithTags("Match").RequireAuthorization(FlexarrClaims.AdminPolicy);

        return api;
    }

    private sealed record Managed(ArrInstance Instance, string Title, string? Path, int QualityProfileId);

    /// <summary>Every film (by TMDB id) or show (by TVDB id) the instances manage, with its folder as Flexarr sees it.</summary>
    private static async Task<Dictionary<int, Managed>> ManagedAsync(ArrClient arr, List<ArrInstance> instances, IReadOnlyList<PathMapping> mappings, CancellationToken ct)
    {
        var managed = new Dictionary<int, Managed>();
        foreach (var instance in instances)
        {
            if (instance.Kind == ArrKind.Radarr)
            {
                foreach (var m in await arr.GetMoviesAsync(instance, ct))
                {
                    managed.TryAdd(m.TmdbId, new Managed(instance, Label(m.Title, m.Year), Local(m.Path, mappings), m.QualityProfileId));
                }
            }
            else
            {
                foreach (var s in await arr.GetSeriesAsync(instance, ct))
                {
                    managed.TryAdd(s.TvdbId, new Managed(instance, Label(s.Title, s.Year), Local(s.Path, mappings), s.QualityProfileId));
                }
            }
        }

        return managed;

        static string Label(string title, int year) => year > 0 ? $"{title} ({year})" : title;
        static string? Local(string? path, IReadOnlyList<PathMapping> mappings) => path is null ? null : PathMapper.Map(path, mappings);
    }

    /// <summary>The instance with a root folder holding the path; else the first.</summary>
    private static async Task<ArrInstance> InstanceForAsync(ArrClient arr, List<ArrInstance> instances, string arrPath, CancellationToken ct)
    {
        if (instances.Count == 1)
        {
            return instances[0];
        }

        foreach (var instance in instances)
        {
            if ((await arr.GetRootFoldersAsync(instance, ct)).Any(r => PathComparison.IsUnder(arrPath, r.Path.Normalize(NormalizationForm.FormC))))
            {
                return instance;
            }
        }

        return instances[0];
    }

    /// <summary>The quality profile most of the instance's titles use, so an added one fits in; else its first.</summary>
    private static async Task<int> QualityProfileAsync(ArrClient arr, ArrInstance instance, IEnumerable<Managed> managed, CancellationToken ct)
    {
        var usual = managed.Where(m => m.Instance == instance && m.QualityProfileId > 0)
            .GroupBy(m => m.QualityProfileId).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        if (usual > 0)
        {
            return usual;
        }

        var profiles = await arr.GetQualityProfilesAsync(instance, ct);
        return profiles.Count > 0 ? profiles[0].Id : throw new HttpRequestException($"{instance.Name} has no quality profiles.", null, System.Net.HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Sonarr and Radarr read a newly added title's folder in the background; wait (briefly) until they've found its
    /// files, so its episodes get their titles straight away.
    /// </summary>
    private static async Task<bool> WaitForAsync(Func<Task<bool>> done, CancellationToken ct)
    {
        var until = DateTime.UtcNow + ArrReadTimeout;
        while (true)
        {
            if (await done())
            {
                return true;
            }

            if (DateTime.UtcNow >= until)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private static ArrKind KindFor(MediaItem item) => item.Kind == MediaKind.Movie ? ArrKind.Radarr : ArrKind.Sonarr;

    // macOS may hand back decomposed accents where the *arr apps store composed ones.
    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('/').Normalize(NormalizationForm.FormC), b.TrimEnd('/').Normalize(NormalizationForm.FormC), StringComparison.Ordinal);

    private static string? Poster(IReadOnlyList<Integrations.Arr.Models.ArrImage> images, string? remotePoster) =>
        ImageEndpoints.RemoteUrl(images.FirstOrDefault(i => i.CoverType == "poster" && !string.IsNullOrEmpty(i.RemoteUrl))?.RemoteUrl ?? remotePoster);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [LoggerMessage(Level = LogLevel.Information, Message = "Fixed the match for {Path}: now {Title} ({Year}) in {Instance} (added there: {Added})")]
    private static partial void LogFixed(ILogger logger, string path, string title, int? year, string instance, bool added);
}
