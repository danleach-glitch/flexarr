using System.Globalization;
using Flexarr.Api.Configuration;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Localization;
using Microsoft.Extensions.Caching.Memory;

namespace Flexarr.Api.Endpoints;

/// <summary>
/// Each profile's Home, kept for up to a minute so opening the apps again doesn't work it all out afresh. It's dropped
/// the moment anything is written to the database (<see cref="DataVersion"/>: progress, a title marked watched, a scan,
/// a layout or watchlist change, by anyone) or the server's settings change, and it never outlives a programme on the
/// Live TV row it shows. Kept apart for everything else Home's answer turns on: the profile and what it may see, the
/// language, the day (the featured titles and genre rows rotate daily) and what the app said it can open.
/// </summary>
internal static class HomeCache
{
    public static readonly TimeSpan KeptFor = TimeSpan.FromMinutes(1);

    internal sealed record Entry(long Version, FlexarrOptions Options, HomeDto Home);

    /// <summary>Everything Home's answer turns on besides the database and the settings.</summary>
    public static string Key(FlexarrDbContext db, CurrentProfile profile, string? listening, DateTime now) => string.Join('|',
        "home", profile.Id, profile.IsMainProfile, listening ?? "", Loc.Language ?? "", CultureInfo.CurrentCulture.Name,
        DateOnly.FromDateTime(now).DayNumber, db.ServesKids, db.MaxRatingAge?.ToString(CultureInfo.InvariantCulture) ?? "",
        string.Join(',', db.HiddenLibraryIds.Order()), string.Join(',', db.KidsLibraryIds.Order()));

    public static bool TryGet(IMemoryCache cache, string key, long version, FlexarrOptions options, out HomeDto home)
    {
        if (cache.TryGetValue(key, out Entry? entry) && entry is not null && entry.Version == version && ReferenceEquals(entry.Options, options))
        {
            home = entry.Home;
            return true;
        }

        home = null!;
        return false;
    }

    /// <param name="version">The <see cref="DataVersion"/> read before Home was worked out, so a write meanwhile drops it.</param>
    public static void Set(IMemoryCache cache, string key, long version, FlexarrOptions options, HomeDto home, DateTime now)
    {
        var keep = KeptFor;
        // What's on now changes when a programme ends, so the row is worked out again then.
        foreach (var channel in home.Rows.SelectMany(r => r.Channels ?? []))
        {
            foreach (var change in new[] { channel.Now?.End, channel.Next?.Start })
            {
                if (change is { } at && at - now < keep)
                {
                    keep = at - now;
                }
            }
        }

        if (keep > TimeSpan.Zero)
        {
            cache.Set(key, new Entry(version, options, home), keep);
        }
    }
}
