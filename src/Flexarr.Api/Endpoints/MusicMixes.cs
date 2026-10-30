using System.Text;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Flexarr.Api.Endpoints;

/// <summary>An artist the listener plays, as the mix planner sees them: how much, and the genres they and their albums carry.</summary>
public sealed record MixArtist(Guid Id, string Name, int Plays, IReadOnlyList<string> Genres);

/// <summary>A mix before its tracks are picked: who's in it and what it's called.</summary>
/// <param name="Genre">The genre it's built around, or null for a mix of artists that didn't fit a genre.</param>
public sealed record MixPlan(string Id, string Name, string Description, IReadOnlyList<Guid> ArtistIds, string? Genre);

/// <param name="Covers">Up to four of its tracks' album covers, for artwork made of them.</param>
public sealed record MixSummaryDto(string Id, string Name, string Description, IReadOnlyList<string> Covers, int TrackCount);

public sealed record MixDetailDto(MixSummaryDto Summary, IReadOnlyList<ItemSummaryDto> Tracks);

/// <summary>A day's mix for one profile, as cached: its summary and the tracks in order (summarised when asked for).</summary>
internal sealed record Mix(MixSummaryDto Summary, IReadOnlyList<Guid> TrackIds);

/// <summary>
/// "Made for you": up to four daily mixes built from what a profile plays. Artists are clustered by the genres they
/// share (Lidarr's, on the artist or their albums); artists no genre gathers get mixes of their own. Nothing is stored:
/// a day's mixes are computed when first asked for and kept in memory until midnight, so a mix is the same all day
/// and new tomorrow.
/// </summary>
public static class MusicMixes
{
    public const string HomeRowId = "music-mixes";

    public const int MixLength = 40;

    public const int MaxMixes = 4;

    /// <summary>Plays before there's enough listening to say what someone likes.</summary>
    public const int MinPlays = 20;

    /// <summary>A mix with fewer tracks than this looks thin, and is left out.</summary>
    public const int MinLength = 8;

    /// <summary>How many artists a mix of artists (one without a genre) gathers.</summary>
    private const int ArtistsPerMix = 6;

    /// <summary>
    /// A genre whose artists are mostly ones an earlier mix already has (by Jaccard similarity of the two sets) doesn't
    /// get a mix of its own: "Rock" and "Classic Rock" would otherwise be the same mix twice.
    /// </summary>
    private const double TooAlike = 0.6;

    public static RouteGroupBuilder MapMusicMixEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/music/mixes").WithTags("Music");

        group.MapGet("/", async (FlexarrDbContext db, IMemoryCache cache, CurrentProfile profile, CancellationToken ct) =>
            (await TodaysAsync(db, cache, profile.Id, ct)).Select(m => m.Summary).ToList());

        group.MapGet("/{id}", async (string id, FlexarrDbContext db, IMemoryCache cache, CurrentProfile profile, CancellationToken ct) =>
        {
            // Yesterday's ids aren't today's: a stale link is simply not found.
            if ((await TodaysAsync(db, cache, profile.Id, ct)).FirstOrDefault(m => m.Summary.Id == id) is not { } mix)
            {
                return Results.NotFound();
            }

            return Results.Ok(new MixDetailDto(mix.Summary, await MusicDiscovery.InOrderAsync(db, mix.TrackIds, profile.Id, ct)));
        });

        return api;
    }

    /// <summary>The "Made for you" row for Home and a music library's Browse, or null when the profile has no mixes yet.</summary>
    public static async Task<HomeRowDto?> HomeRowAsync(FlexarrDbContext db, IMemoryCache cache, string profileId, CancellationToken ct)
    {
        var mixes = await TodaysAsync(db, cache, profileId, ct);
        return mixes.Count == 0 ? null : new HomeRowDto(HomeRowId, "Made for you", "mixes", [], null, null, mixes.Select(m => m.Summary).ToList());
    }

    /// <summary>
    /// Groups the artists someone plays into up to <paramref name="max"/> mixes. Genres first, the ones that account
    /// for most of the listening leading, each needing two artists and skipped when it would mostly repeat an earlier
    /// mix. Artists no genre mix took are then gathered a few at a time, most played first. Each mix is named after its
    /// genre, or after its most-played artists when it has no genre.
    /// </summary>
    public static List<MixPlan> Plan(IReadOnlyList<MixArtist> artists, int max = MaxMixes)
    {
        var played = artists.Where(a => a.Plays > 0).OrderByDescending(a => a.Plays).ThenBy(a => a.Name, StringComparer.Ordinal).ToList();
        var plans = new List<MixPlan>();
        var clusters = new List<HashSet<Guid>>();

        var genres = played.SelectMany(a => a.Genres.Distinct(StringComparer.Ordinal).Select(g => (Genre: g, Artist: a)))
            .GroupBy(x => x.Genre, StringComparer.Ordinal)
            .Select(g => (Name: g.Key, Members: g.Select(x => x.Artist).ToList()))
            .Where(g => g.Members.Count >= 2)
            .OrderByDescending(g => g.Members.Sum(a => a.Plays)).ThenByDescending(g => g.Members.Count).ThenBy(g => g.Name, StringComparer.Ordinal);
        foreach (var genre in genres)
        {
            if (plans.Count >= max)
            {
                break;
            }

            var ids = genre.Members.Select(a => a.Id).ToHashSet();
            if (clusters.Any(c => Jaccard(c, ids) >= TooAlike))
            {
                continue;
            }

            clusters.Add(ids);
            plans.Add(new MixPlan(Id($"genre:{genre.Name}"), $"{genre.Name} Mix", ListNames(genre.Members.Select(a => a.Name)), genre.Members.Select(a => a.Id).ToList(), genre.Name));
        }

        var covered = clusters.SelectMany(c => c).ToHashSet();
        foreach (var chunk in played.Where(a => !covered.Contains(a.Id)).Chunk(ArtistsPerMix))
        {
            if (plans.Count >= max)
            {
                break;
            }

            var ids = chunk.Select(a => a.Id).ToList();
            var name = chunk.Length == 1 ? $"{chunk[0].Name} Mix" : ListNames(chunk.Select(a => a.Name));
            plans.Add(new MixPlan(Id($"artists:{string.Join(",", ids.Order())}"), name, "Made from the artists you play", ids, null));
        }

        return plans;
    }

    /// <summary>
    /// Picks a mix's tracks the way artist radio does, favouring artists and tracks the listener plays, with no track
    /// twice and no artist taking more than twice their fair share. The order is shuffled. The same seed always gives
    /// the same mix.
    /// </summary>
    public static List<Guid> Pick(IReadOnlyList<RadioCandidate> candidates, int seed, int count = MixLength)
    {
        var random = new Random(seed);
        var pool = candidates.DistinctBy(c => c.TrackId).ToList();
        var artists = pool.Select(c => c.ArtistId).Distinct().Count();
        if (artists == 0)
        {
            return [];
        }

        var cap = (int)Math.Ceiling(count * 2.0 / artists);
        var picks = MusicDiscovery.Draw(pool, count, random, c => 1 + Math.Log2(1 + c.ArtistPlays) + Math.Log2(1 + c.TrackPlays), cap);
        for (var i = picks.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (picks[i], picks[j]) = (picks[j], picks[i]);
        }

        return picks.Select(c => c.TrackId).ToList();
    }

    /// <summary>"Pulp", "Pulp &amp; Blur", "Pulp, Blur &amp; Oasis", or "Pulp, Blur &amp; more" when there are more than three.</summary>
    public static string ListNames(IEnumerable<string> names)
    {
        var list = names.Take(4).ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            2 => $"{list[0]} & {list[1]}",
            3 => $"{list[0]}, {list[1]} & {list[2]}",
            _ => $"{list[0]}, {list[1]} & more",
        };
    }

    /// <summary>Today's mixes for a profile, built on first asking and kept until midnight UTC.</summary>
    internal static async Task<List<Mix>> TodaysAsync(FlexarrDbContext db, IMemoryCache cache, string profileId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // A new age limit, or Allow and Block, makes them afresh: their names and covers come from what they hold.
        var key = $"music-mixes:{profileId}:{today.DayNumber}:{db.MaxRatingAge}:{LibraryVisibility.KidsSettingsVersion}";
        if (cache.TryGetValue(key, out List<Mix>? cached) && cached is not null)
        {
            return cached;
        }

        var mixes = await BuildAsync(db, profileId, today.DayNumber, ct);
        // Nothing yet isn't kept: someone who crosses the listening threshold today gets their mixes today.
        if (mixes.Count > 0)
        {
            var midnight = new DateTimeOffset(today.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            cache.Set(key, mixes, new MemoryCacheEntryOptions { AbsoluteExpiration = midnight });
        }

        return mixes;
    }

    private static async Task<List<Mix>> BuildAsync(FlexarrDbContext db, string profileId, int day, CancellationToken ct)
    {
        var plays = await db.PlaybackStates.Where(p => p.UserId == profileId && p.MediaItem!.Kind == MediaKind.Track && p.PlayCount > 0)
            .GroupBy(p => p.MediaItem!.Parent!.ParentId!.Value)
            .Select(g => new { ArtistId = g.Key, Plays = g.Sum(p => p.PlayCount) })
            .ToListAsync(ct);
        if (plays.Sum(p => p.Plays) < MinPlays)
        {
            return [];
        }

        // An artist's genres are their own and their albums': Lidarr tags some artists only at the album level.
        var artistIds = plays.Select(p => p.ArtistId).ToList();
        var tagged = (await db.MediaItems
            .Where(i => (i.Kind == MediaKind.Artist && artistIds.Contains(i.Id)) || (i.Kind == MediaKind.Album && artistIds.Contains(i.ParentId!.Value)))
            .Select(i => new { ArtistId = i.Kind == MediaKind.Artist ? i.Id : i.ParentId!.Value, Name = i.Kind == MediaKind.Artist ? i.Title : null, Genres = i.Genres.Select(g => g.Name).ToList() })
            .ToListAsync(ct))
            .ToLookup(i => i.ArtistId);
        var artists = plays.Select(p => new MixArtist(
                p.ArtistId,
                tagged[p.ArtistId].Select(t => t.Name).FirstOrDefault(n => n != null) ?? "Unknown artist",
                p.Plays,
                tagged[p.ArtistId].SelectMany(t => t.Genres).Distinct(StringComparer.Ordinal).ToList()))
            .ToList();

        var plans = Plan(artists);
        if (plans.Count == 0)
        {
            return [];
        }

        var wanted = plans.SelectMany(p => p.ArtistIds).Distinct().ToList();
        var tracks = await db.MediaItems.Where(t => t.Kind == MediaKind.Track && wanted.Contains(t.Parent!.ParentId!.Value))
            .Select(t => new
            {
                t.Id,
                ArtistId = t.Parent!.ParentId!.Value,
                AlbumId = t.ParentId!.Value,
                HasCover = t.Parent.PosterUrl != null,
                CoverVersion = t.Parent.MetadataUpdatedAt,
                Plays = t.PlaybackStates.Where(p => p.UserId == profileId).Select(p => p.PlayCount).FirstOrDefault(),
            })
            .ToListAsync(ct);
        var byArtist = tracks.ToLookup(t => t.ArtistId);
        var byId = tracks.ToDictionary(t => t.Id);
        var artistPlays = plays.ToDictionary(p => p.ArtistId, p => p.Plays);

        var mixes = new List<Mix>();
        foreach (var plan in plans)
        {
            var candidates = plan.ArtistIds.SelectMany(a => byArtist[a])
                .Select(t => new RadioCandidate(t.Id, t.ArtistId, 0, artistPlays.GetValueOrDefault(t.ArtistId), t.Plays)).ToList();
            var picks = Pick(candidates, Seed(day, profileId, plan.Id));
            if (picks.Count < MinLength)
            {
                continue;
            }

            var covers = picks.Select(id => byId[id]).Where(t => t.HasCover).DistinctBy(t => t.AlbumId).Take(4)
                .Select(t => ArtworkUrls.For(t.AlbumId, t.CoverVersion == null ? 0 : t.CoverVersion.Value.Ticks / 10_000_000, true, false, false, false).Poster!)
                .ToList();
            mixes.Add(new Mix(new MixSummaryDto(plan.Id, plan.Name, plan.Description, covers, picks.Count), picks));
        }

        return mixes;
    }

    /// <summary>How alike two sets are: the share of their union they have in common (1 when identical, 0 when disjoint).</summary>
    private static double Jaccard(HashSet<Guid> a, HashSet<Guid> b) => (double)a.Intersect(b).Count() / a.Union(b).Count();

    /// <summary>The day, profile and mix together decide the picks, so a mix is the same all day however many times it's asked for, and across a restart.</summary>
    private static int Seed(int day, string profileId, string mixId) => unchecked((int)(Fnv1a($"{day}:{profileId}:{mixId}")));

    /// <summary>A mix's id: a short, stable hash of what it's built from, so the same mix has the same id from day to day.</summary>
    private static string Id(string key) => Fnv1a(key).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A stable string hash: <see cref="string.GetHashCode()"/> changes from one run to the next.</summary>
    private static uint Fnv1a(string text)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash = (hash ^ b) * 16777619u;
        }

        return hash;
    }
}
