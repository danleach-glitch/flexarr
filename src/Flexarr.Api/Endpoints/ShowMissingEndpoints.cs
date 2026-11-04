using System.Globalization;
using System.Text.Json;
using Flexarr.Api.Configuration;
using Flexarr.Api.Profiles;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Flexarr.Api.Localization;

namespace Flexarr.Api.Endpoints;

/// <summary>A season of a show that the library has none of.</summary>
/// <param name="Episodes">How many of its episodes have aired.</param>
/// <param name="Availability">Requested when Sonarr is already looking for it (or Seerr says so).</param>
public sealed record MissingSeasonDto(int SeasonNumber, string? Name, int Episodes, int? Year, string? Poster, AvailabilityDto Availability);

/// <summary>An episode: one that aired and isn't in the library, or one still to air.</summary>
/// <param name="Monitored">Sonarr is looking for it.</param>
public sealed record ShowEpisodeDto(int SeasonNumber, int EpisodeNumber, string? Title, DateTime? AirDate, string? Still, bool Monitored);

/// <summary>Of a season the library has: how many of its aired episodes it has.</summary>
public sealed record SeasonCountDto(int SeasonNumber, int Have, int Aired);

/// <summary>What a show has that the library doesn't, and what's coming.</summary>
/// <param name="Source">"sonarr" (seasons and episodes), "seerr" (whole seasons only) or "none".</param>
/// <param name="CanRequest">This profile can request a missing season through Seerr.</param>
/// <param name="CanSearch">This profile can ask Sonarr to search for missing episodes (the main profile, for a show Sonarr manages).</param>
/// <param name="Seasons">Seasons that have aired and the library has none of, oldest first. Specials are left out.</param>
/// <param name="Episodes">Aired episodes missing from seasons the library has some of.</param>
/// <param name="Upcoming">The next episodes Sonarr has air dates for, up to five.</param>
/// <param name="Counts">Each season the library has, with how many of its aired episodes are in it (Sonarr only).</param>
public sealed record ShowMissingDto(
    string Source,
    bool CanRequest,
    bool CanSearch,
    IReadOnlyList<MissingSeasonDto> Seasons,
    IReadOnlyList<ShowEpisodeDto> Episodes,
    IReadOnlyList<ShowEpisodeDto> Upcoming,
    IReadOnlyList<SeasonCountDto> Counts)
{
    public static readonly ShowMissingDto None = new("none", false, false, [], [], [], []);
}

/// <param name="SeasonNumber">The season whose missing episodes to look for.</param>
/// <param name="EpisodeNumbers">Just these; null or empty for every aired episode of the season the library lacks.</param>
public sealed record MissingSearchRequest(int SeasonNumber, IReadOnlyList<int>? EpisodeNumbers = null);

public static class ShowMissingEndpoints
{
    private static readonly TimeSpan EpisodesTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(8);
    private const int UpcomingCount = 5;

    public static RouteGroupBuilder MapShowMissingEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/items/{id:guid}/missing", async (Guid id, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, ArrClient arr, SeerrClient seerr,
            IMemoryCache cache, CurrentProfile current, CancellationToken ct) =>
        {
            var show = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id && i.Kind == MediaKind.Series, ct);
            if (show is null)
            {
                return Results.NotFound();
            }

            // Profiles with an age limit don't request, and what's missing isn't rated.
            if (db.IsLimited())
            {
                return Results.Ok(ShowMissingDto.None);
            }

            var have = await HaveAsync(db, id, ct);
            var seerrInstance = options.CurrentValue.Seerr?.Instance;
            var canRequest = seerrInstance is not null && show.TmdbId is not null
                && !(await LibraryVisibility.HiddenTypesAsync(db, ct)).Hides(MediaKind.Series);

            if (await SonarrSeriesAsync(show, options.CurrentValue, arr, cache, ct) is { } managed)
            {
                if (await EpisodesAsync(managed.Instance, managed.Series.Id, arr, cache, ct) is { } episodes)
                {
                    var gaps = Gaps(episodes, managed.Series.Seasons, have, DateTime.UtcNow);
                    return Results.Ok(gaps with { CanRequest = canRequest, CanSearch = current.IsMainProfile });
                }
            }

            if (seerrInstance is not null && show.TmdbId is { } tmdb && int.TryParse(tmdb, CultureInfo.InvariantCulture, out var tmdbId))
            {
                try
                {
                    using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    quick.CancelAfter(CallTimeout);
                    if (await seerr.GetAsync(seerrInstance, $"tv/{tmdbId}", quick.Token) is { } title)
                    {
                        return Results.Ok(new ShowMissingDto("seerr", canRequest, false, SeerrSeasons(title, have.Seasons, DateOnly.FromDateTime(DateTime.UtcNow)), [], [], []));
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    // Extras: the page goes on without them.
                }
            }

            return Results.Ok(ShowMissingDto.None with { CanRequest = canRequest });
        }).WithTags("Items");

        // Asks Sonarr to look for a season's missing episodes: monitors them, then searches.
        api.MapPost("/items/{id:guid}/missing/search", async (Guid id, MissingSearchRequest request, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options,
            ArrClient arr, IMemoryCache cache, CancellationToken ct) =>
        {
            var show = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id && i.Kind == MediaKind.Series, ct);
            if (show is null)
            {
                return Results.NotFound();
            }

            if (await SonarrSeriesAsync(show, options.CurrentValue, arr, cache, ct) is not { } managed)
            {
                return Problems.Of("Sonarr doesn’t manage this show, so there’s nothing to search with.", status: 409);
            }

            try
            {
                var have = await HaveAsync(db, id, ct);
                var now = DateTime.UtcNow;
                var wanted = request.EpisodeNumbers is { Count: > 0 } numbers ? numbers.ToHashSet() : null;
                var episodes = (await arr.GetEpisodesAsync(managed.Instance, managed.Series.Id, ct))
                    .Where(e => e.SeasonNumber == request.SeasonNumber && !e.HasFile && !have.Has(e.SeasonNumber, e.EpisodeNumber) && e.AirDateUtc <= now
                        && (wanted is null || wanted.Contains(e.EpisodeNumber)))
                    .Select(e => e.Id).ToList();
                if (episodes.Count == 0)
                {
                    return Problems.Of("Nothing in that season is missing.", status: 409);
                }

                await arr.SetEpisodesMonitoredAsync(managed.Instance, episodes, true, ct);
                await arr.SendCommandAsync(managed.Instance, new { name = "EpisodeSearch", episodeIds = episodes }, ct);
                Forget(cache, options.CurrentValue, id);
                return Results.Accepted();
            }
            catch (HttpRequestException ex)
            {
                return Problems.Of("{name} didn’t take the search ({status}).", new { name = managed.Instance.Name, status = (int?)ex.StatusCode ?? 0 }, status: 502);
            }
        }).WithTags("Items").RequireAuthorization(FlexarrClaims.MainProfilePolicy);

        return api;
    }

    /// <summary>The episodes and seasons the library has of a show.</summary>
    internal sealed record Have(IReadOnlySet<(int Season, int Episode)> Episodes, IReadOnlySet<int> Seasons)
    {
        public bool Has(int season, int episode) => Episodes.Contains((season, episode));
    }

    private static async Task<Have> HaveAsync(FlexarrDbContext db, Guid showId, CancellationToken ct)
    {
        // Missing files are filtered out by the context; an age limit can't hide part of a show it can see.
        var rows = await db.MediaItems.AsNoTracking().IgnoreQueryFilters()
            .Where(i => i.Kind == MediaKind.Episode && i.MissingSince == null && i.Parent!.ParentId == showId && i.SeasonNumber != null && i.EpisodeNumber != null)
            .Select(i => new { Season = i.SeasonNumber!.Value, First = i.EpisodeNumber!.Value, Last = i.EpisodeNumberEnd }).ToListAsync(ct);
        var episodes = new HashSet<(int, int)>();
        foreach (var r in rows)
        {
            for (var e = r.First; e <= Math.Max(r.First, r.Last ?? r.First); e++)
            {
                episodes.Add((r.Season, e));
            }
        }

        var seasons = await db.MediaItems.AsNoTracking().IgnoreQueryFilters()
            .Where(i => i.Kind == MediaKind.Season && i.MissingSince == null && i.ParentId == showId && i.SeasonNumber != null)
            .Select(i => i.SeasonNumber!.Value).ToListAsync(ct);
        return new Have(episodes, seasons.Concat(rows.Select(r => r.Season)).ToHashSet());
    }

    /// <summary>
    /// What Sonarr's episodes say the library lacks. An episode counts as had when the library has it or Sonarr has its
    /// file (it's on its way in). Specials (season 0) are left out, and episodes with no air date are neither missing nor upcoming.
    /// </summary>
    internal static ShowMissingDto Gaps(IReadOnlyList<SonarrEpisode> episodes, IReadOnlyList<SonarrSeason> seasons, Have have, DateTime now)
    {
        var regular = episodes.Where(e => e.SeasonNumber > 0).ToList();
        bool Had(SonarrEpisode e) => e.HasFile || have.Has(e.SeasonNumber, e.EpisodeNumber);
        var aired = regular.Where(e => e.AirDateUtc is { } d && d <= now).ToList();
        var missingSeasons = new List<MissingSeasonDto>();
        var missingEpisodes = new List<ShowEpisodeDto>();
        var counts = new List<SeasonCountDto>();

        foreach (var season in aired.GroupBy(e => e.SeasonNumber).OrderBy(g => g.Key))
        {
            var had = season.Count(Had);
            var inLibrary = have.Seasons.Contains(season.Key);
            if (had == 0 && !inLibrary)
            {
                var monitored = seasons.FirstOrDefault(s => s.SeasonNumber == season.Key)?.Monitored == true || season.Any(e => e.Monitored);
                var poster = seasons.FirstOrDefault(s => s.SeasonNumber == season.Key)?.Images?.FirstOrDefault(i => i.CoverType == "poster")?.RemoteUrl;
                missingSeasons.Add(new MissingSeasonDto(season.Key, null, season.Count(), season.Min(e => e.AirDateUtc)?.Year,
                    poster is null ? null : ImageEndpoints.RemoteUrl(poster), monitored ? AvailabilityDto.Requested : AvailabilityDto.None));
                continue;
            }

            counts.Add(new SeasonCountDto(season.Key, had, season.Count()));
            missingEpisodes.AddRange(season.Where(e => !Had(e)).OrderBy(e => e.EpisodeNumber).Select(Episode));
        }

        var upcoming = regular.Where(e => e.AirDateUtc is { } d && d > now && !Had(e)).OrderBy(e => e.AirDateUtc).ThenBy(e => e.EpisodeNumber)
            .Take(UpcomingCount).Select(Episode).ToList();
        return new ShowMissingDto("sonarr", false, false, missingSeasons, missingEpisodes, upcoming, counts);
    }

    private static ShowEpisodeDto Episode(SonarrEpisode e) =>
        new(e.SeasonNumber, e.EpisodeNumber, string.IsNullOrWhiteSpace(e.Title) || e.Title == "TBA" ? null : e.Title, e.AirDateUtc,
            e.Images.FirstOrDefault(i => i.CoverType == "screenshot")?.RemoteUrl is { } still ? ImageEndpoints.RemoteUrl(still) : null, e.Monitored);

    /// <summary>The seasons Seerr (TMDB) lists that have started airing and the library has none of.</summary>
    internal static List<MissingSeasonDto> SeerrSeasons(JsonElement title, IReadOnlySet<int> haveSeasons, DateOnly today)
    {
        var statuses = new Dictionary<int, int>();
        if (title.TryGetProperty("mediaInfo", out var info) && info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("seasons", out var infoSeasons) && infoSeasons.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in infoSeasons.EnumerateArray())
            {
                if (Int(s, "seasonNumber") is { } n && Int(s, "status") is { } status)
                {
                    statuses[n] = status;
                }
            }
        }

        if (!title.TryGetProperty("seasons", out var seasons) || seasons.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var missing = new List<MissingSeasonDto>();
        foreach (var s in seasons.EnumerateArray())
        {
            if (Int(s, "seasonNumber") is not ({ } n and > 0) || haveSeasons.Contains(n)
                || Str(s, "airDate") is not { } air || !DateOnly.TryParse(air, CultureInfo.InvariantCulture, out var aired) || aired > today)
            {
                continue;
            }

            var availability = RequestEndpoints.AvailabilityOf(statuses.TryGetValue(n, out var st) ? st : null);
            missing.Add(new MissingSeasonDto(n, Str(s, "name"), Int(s, "episodeCount") ?? 0, aired.Year,
                Str(s, "posterPath") is { } p ? ImageEndpoints.RemoteUrl($"https://image.tmdb.org/t/p/w500{p}") : null,
                availability == AvailabilityDto.Available ? AvailabilityDto.Requested : availability));
        }

        return [.. missing.OrderBy(m => m.SeasonNumber)];
    }

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// The Sonarr instance and series for a library show: the one its metadata came from, else any Sonarr with its
    /// TheTVDB id. Null when no Sonarr has it or none answers.
    /// </summary>
    private static async Task<(ArrInstance Instance, SonarrSeries Series)?> SonarrSeriesAsync(MediaItem show, FlexarrOptions options, ArrClient arr, IMemoryCache cache,
        CancellationToken ct)
    {
        var sonarrs = options.Arr.Where(a => a.Kind == ArrKind.Sonarr).OrderByDescending(a => a.Name == show.MetadataSource).ToList();
        foreach (var instance in sonarrs)
        {
            var key = $"show-missing:series:{instance.BaseUrl}:{show.Id}";
            if (cache.TryGetValue(key, out SonarrSeries? hit) && hit is not null)
            {
                return (instance, hit);
            }

            try
            {
                using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
                quick.CancelAfter(CallTimeout);
                SonarrSeries? series = null;
                if (instance.Name == show.MetadataSource && show.ArrId is { } arrId)
                {
                    series = await arr.GetOneSeriesAsync(instance, arrId, quick.Token);
                }
                else if (show.TvdbId is { } tvdb && int.TryParse(tvdb, CultureInfo.InvariantCulture, out var tvdbId))
                {
                    series = await arr.GetSeriesByTvdbIdAsync(instance, tvdbId, quick.Token) is [var first, ..] ? first : null;
                }

                if (series is not null)
                {
                    cache.Set(key, series, EpisodesTtl);
                    return (instance, series);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                // Try the next Sonarr, then Seerr.
            }
        }

        return null;
    }

    private static string EpisodesKey(ArrInstance instance, int seriesId) => $"show-missing:episodes:{instance.BaseUrl}:{seriesId}";

    private static async Task<IReadOnlyList<SonarrEpisode>?> EpisodesAsync(ArrInstance instance, int seriesId, ArrClient arr, IMemoryCache cache, CancellationToken ct)
    {
        var key = EpisodesKey(instance, seriesId);
        if (cache.TryGetValue(key, out IReadOnlyList<SonarrEpisode>? hit) && hit is not null)
        {
            return hit;
        }

        try
        {
            using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quick.CancelAfter(CallTimeout);
            var episodes = await arr.GetEpisodesAsync(instance, seriesId, quick.Token);
            cache.Set(key, episodes, EpisodesTtl);
            return episodes;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Forgets a show's cached Sonarr episodes, after a request or search changes what Sonarr is looking for.</summary>
    internal static void Forget(IMemoryCache cache, FlexarrOptions options, Guid showId)
    {
        foreach (var instance in options.Arr.Where(a => a.Kind == ArrKind.Sonarr))
        {
            if (cache.TryGetValue($"show-missing:series:{instance.BaseUrl}:{showId}", out SonarrSeries? series) && series is not null)
            {
                cache.Remove(EpisodesKey(instance, series.Id));
            }

            cache.Remove($"show-missing:series:{instance.BaseUrl}:{showId}");
        }
    }
}
