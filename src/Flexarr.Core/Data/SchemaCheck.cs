using Microsoft.EntityFrameworkCore;

namespace Flexarr.Core.Data;

/// <summary>
/// A database a newer Flexarr has migrated holds tables and columns this build doesn't know, and an older build run on
/// it (a rollback, a backup from a newer server restored here) would start fine and fail later, writing as it went.
/// So startup stops first: the migration history naming a migration later than this build's last means the database is
/// newer. Ids start with when the migration was made, so they sort by age. An unknown id older than this build's last is
/// left alone: a migration renamed before it was released (the podcasts one, in October 2026) is still in the history
/// of a database a dev build of the time migrated.
/// </summary>
public static class SchemaCheck
{
    /// <summary>The migrations the database has had that are later than any this build knows, oldest first.</summary>
    public static IReadOnlyList<string> Unknown(IEnumerable<string> applied, IEnumerable<string> known)
    {
        ArgumentNullException.ThrowIfNull(applied);
        var last = known.Order(StringComparer.Ordinal).LastOrDefault() ?? "";
        return applied.Where(id => string.CompareOrdinal(id, last) > 0).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Throws when the database has been migrated by a newer Flexarr than this one.</summary>
    public static async Task EnsureNotNewerAsync(FlexarrDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var unknown = Unknown(await db.Database.GetAppliedMigrationsAsync(ct), db.Database.GetMigrations());
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"The database was last used by a newer version of Flexarr (it has migration {unknown[^1]}, which this version doesn't know). " +
                "Start the newer version again, or restore the backup Flexarr kept before it upgraded (a pre-upgrade backup in the backups folder).");
        }
    }
}
