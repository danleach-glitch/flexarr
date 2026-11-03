using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Flexarr.Core.Data;

/// <summary>
/// A number that moves on whenever a save adds, changes or removes an item, person, credit or genre, so caches built
/// from them (search's typo index) can tell nothing has changed without asking the database. Bulk updates and deletes
/// skip the change tracker and don't move it: a cache using it still checks the database now and then.
/// </summary>
public static class CatalogVersion
{
    private static long value;

    public static long Current => Interlocked.Read(ref value);

    public static void Bump() => Interlocked.Increment(ref value);

    /// <summary>Whether <paramref name="tracker"/> holds a change to something the catalog version covers.</summary>
    internal static bool Touches(ChangeTracker tracker) =>
        tracker.Entries().Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
            && e.Entity is MediaItem or Person or Credit or Genre);
}
