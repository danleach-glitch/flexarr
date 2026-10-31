using System.Globalization;
using System.Text.Json;
using Flexarr.Api.Endpoints;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Integrations.Arr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Flexarr.Api.Trakt;

/// <summary>
/// Trakt's picks for a profile as a Discover row, "For you, from Trakt": titles here first, then ones to request, with
/// what's been watched here left out. Trakt is asked a few times a day; each title's poster and details come from
/// Seerr (which has them from TMDB), and are kept a day.
/// </summary>
public sealed partial class TraktRecommendations(FlexarrDbContext db, TraktClient trakt, TraktAccounts accounts, SeerrClient seerr, IMemoryCache cache,
    ILogger<TraktRecommendations> logger)
{
    public const string RowId = "trakt";
    private static readonly TimeSpan PicksFor = TimeSpan.FromHours(6);
    private static readonly TimeSpan TitleFor = TimeSpan.FromDays(1);
    private const int RowSize = 20;

    private sealed record Pick(string MediaType, int TmdbId);

    /// <summary>The row, or null when the profile isn't connected or Trakt has nothing.</summary>
    internal async Task<ExternalRowDto?> RowAsync(string profileId, SeerrInstance instance, Dictionary<string, Guid> library, IReadOnlySet<string> watchlist,
        IReadOnlySet<Flexarr.Core.Libraries.LibraryType> hidden, CancellationToken ct)
    {
        if (!await TraktOutbox.ConnectedAsync(db, profileId, ct))
        {
            return null;
        }

        var picks = await PicksAsync(profileId, ct);
        if (picks is not { Count: > 0 })
        {
            return null;
        }

        var watched = await db.PlaybackStates.Where(p => p.UserId == profileId && p.Watched && p.MediaItem!.Kind == MediaKind.Movie)
            .Select(p => p.MediaItemId).ToListAsync(ct);
        var titles = await Task.WhenAll(picks.Where(p => !hidden.Hides(RequestEndpoints.KindOf(p.MediaType)) || library.ContainsKey(Key(p)))
            .Take(RowSize * 2).Select(p => TitleAsync(instance, p, ct)));
        var items = titles.OfType<SeerrResult>()
            .Select(r => RequestEndpoints.ToDto(r, library, watchlist))
            .Where(t => t.LibraryItemId is not { } id || !watched.Contains(id))
            .OrderBy(t => t.LibraryItemId is null)
            .Take(RowSize)
            .ToList();
        return items.Count == 0 ? null : new ExternalRowDto(RowId, "For you, from Trakt", items);
    }

    private static string Key(Pick p) => $"{p.MediaType}:{p.TmdbId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Trakt's picks, films and shows taken in turn.</summary>
    private async Task<List<Pick>?> PicksAsync(string profileId, CancellationToken ct)
    {
        var key = $"trakt:picks:{profileId}";
        if (cache.TryGetValue(key, out List<Pick>? cached))
        {
            return cached;
        }

        try
        {
            var (ok, picks) = await accounts.WithTokenAsync(profileId, async token =>
            {
                var movies = await trakt.RecommendationsAsync(token, "movies", ct);
                var shows = await trakt.RecommendationsAsync(token, "shows", ct);
                var list = new List<Pick>();
                for (var i = 0; i < Math.Max(movies.Count, shows.Count); i++)
                {
                    if (i < movies.Count && movies[i].Ids.Tmdb is { } m) list.Add(new Pick("movie", m));
                    if (i < shows.Count && shows[i].Ids.Tmdb is { } s) list.Add(new Pick("tv", s));
                }

                return list;
            }, ct);
            var result = ok ? picks : null;
            cache.Set(key, result, result is null ? TimeSpan.FromMinutes(10) : PicksFor);
            return result;
        }
        catch (TraktException ex)
        {
            LogPicksFailed(ex.Message);
            cache.Set(key, (List<Pick>?)null, TimeSpan.FromMinutes(10));
            return null;
        }
    }

    private async Task<SeerrResult?> TitleAsync(SeerrInstance instance, Pick pick, CancellationToken ct)
    {
        var key = $"trakt:title:{Key(pick)}";
        if (cache.TryGetValue(key, out SeerrResult? cached))
        {
            return cached;
        }

        SeerrResult? result = null;
        try
        {
            if (await seerr.GetAsync(instance, $"{pick.MediaType}/{pick.TmdbId.ToString(CultureInfo.InvariantCulture)}", ct) is { } json)
            {
                result = json.Deserialize<SeerrResult>(Web) is { } r ? r with { MediaType = pick.MediaType, Id = pick.TmdbId } : null;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            return null;
        }

        cache.Set(key, result, TitleFor);
        return result;
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [LoggerMessage(Level = LogLevel.Information, Message = "Couldn't get Trakt's recommendations: {Reason}")]
    private partial void LogPicksFailed(string reason);
}
