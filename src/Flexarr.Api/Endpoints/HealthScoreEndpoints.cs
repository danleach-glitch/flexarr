using Flexarr.Api.Configuration;
using Flexarr.Api.Profiles;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Scanning;
using Flexarr.Integrations.Arr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Endpoints;

/// <summary>
/// Library health's score kept for a while (audit perf-60), so Settings › Overview needn't build the whole report on
/// every visit. Dropped when a scan finishes, a health check finishes or the connections change, and after
/// <see cref="KeptFor"/> at most; built afresh while a scan or check is running; and replaced whenever the full report
/// is built (as Library health does after each fix).
/// </summary>
internal static class HealthScoreCache
{
    internal static readonly TimeSpan KeptFor = TimeSpan.FromMinutes(5);

    /// <summary>One per profile: what a profile may see (its libraries) shapes the score.</summary>
    private static string Key(string profileId) => $"library-health:score:{profileId}";

    private sealed record Kept(Fingerprint Of, HealthScoreDto Score);

    /// <summary>What the score was worked out against: a change to any of these means it may be out of date.</summary>
    internal sealed record Fingerprint(int Libraries, DateTime? LastScan, DateTime? CheckFinished, int Arrs, bool Tmdb);

    /// <summary>The fingerprint now, or null while a scan or a health check is running (the score is changing).</summary>
    internal static async Task<Fingerprint?> FingerprintAsync(FlexarrDbContext db, HealthCheck check, ScanProgress progress,
        IOptionsMonitor<FlexarrOptions> options, CancellationToken ct)
    {
        var status = check.Status;
        if (status.Running || progress.Current is not null)
        {
            return null;
        }

        var libraries = await db.Libraries.AsNoTracking().Select(l => l.LastScannedAt).ToListAsync(ct);
        var o = options.CurrentValue;
        return new Fingerprint(libraries.Count, libraries.Max(), status.FinishedAt, o.Arr.Count, o.Tmdb?.ApiKey is { Length: > 0 });
    }

    internal static HealthScoreDto? Get(IMemoryCache memory, string profileId, Fingerprint? now) =>
        now is not null && memory.TryGetValue(Key(profileId), out Kept? kept) && kept is not null && kept.Of == now ? kept.Score : null;

    internal static void Set(IMemoryCache memory, string profileId, Fingerprint? now, HealthScoreDto score)
    {
        if (now is null)
        {
            memory.Remove(Key(profileId));
        }
        else
        {
            memory.Set(Key(profileId), new Kept(now, score), KeptFor);
        }
    }
}

public static partial class HealthEndpoints
{
    private static RouteGroupBuilder MapLibraryHealthEndpoints(this RouteGroupBuilder api,
        Func<FlexarrDbContext, CurrentProfile, HealthCheck, IOptionsMonitor<FlexarrOptions>, ArrClient, CancellationToken, Task<LibraryHealthDto>> report)
    {
        // The full report, as before; its score is kept for /library-health/score.
        api.MapGet("/library-health", async (FlexarrDbContext db, CurrentProfile profile, HealthCheck check, IOptionsMonitor<FlexarrOptions> options, ArrClient arr,
            IMemoryCache memory, ScanProgress progress, CancellationToken ct) =>
        {
            var before = await HealthScoreCache.FingerprintAsync(db, check, progress, options, ct);
            var health = await report(db, profile, check, options, arr, ct);
            HealthScoreCache.Set(memory, profile.Id, before, health.Score);
            return health;
        }).WithTags("Library health").RequireAuthorization(FlexarrClaims.AdminPolicy);

        // GET /library-health/score (new in the release after 0.62.1; absent before, so fall back to /library-health's
        // `score`): the report's `score` object alone, the same HealthScoreDto, kept for up to five minutes and dropped
        // when a scan or health check finishes. Admins only, like the report.
        api.MapGet("/library-health/score", async (FlexarrDbContext db, CurrentProfile profile, HealthCheck check, IOptionsMonitor<FlexarrOptions> options,
            ArrClient arr, IMemoryCache memory, ScanProgress progress, CancellationToken ct) =>
        {
            var now = await HealthScoreCache.FingerprintAsync(db, check, progress, options, ct);
            if (HealthScoreCache.Get(memory, profile.Id, now) is { } kept)
            {
                return kept;
            }

            var score = (await report(db, profile, check, options, arr, ct)).Score;
            HealthScoreCache.Set(memory, profile.Id, now, score);
            return score;
        }).WithTags("Library health").RequireAuthorization(FlexarrClaims.AdminPolicy);

        return api;
    }
}
