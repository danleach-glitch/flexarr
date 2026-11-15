using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Flexarr.Core.Data;

/// <summary>
/// How the SQLite database is run beyond WAL (set once, at startup): each connection syncs to disk only at checkpoints
/// rather than on every commit. The planner is deliberately given no statistics (no ANALYZE or PRAGMA optimize). They
/// count a column's nulls as one value, and MediaItems' are lopsided: ParentId is null for every film and show, and
/// LinkGroupId for nearly every item, so with them those indexes look useless; a library's few artists or seasons
/// look like a third of it. On a copy of a real library (21,000 items, November 2026), ANALYZE turned 199 of the
/// 1,803 statements the server's tests run into reads of every item; the 33 it spared a full read were of genre
/// links, credits and play states, never of items.
/// Queries are written for the plans SQLite picks without them (LibraryScanWorkerTests checks one such plan, and that
/// nothing gathers them).
/// </summary>
public static class SqliteTuning
{
    /// <summary>
    /// With WAL, NORMAL can't corrupt the database: a power cut loses at most the last few commits, not the file.
    /// FULL would sync the log to disk on every commit, which every player's progress every few seconds is.
    /// </summary>
    private const string PerConnection = "PRAGMA synchronous=NORMAL;";

    /// <summary>Sets <see cref="PerConnection"/> as each connection opens (straight on the connection: it writes nothing).</summary>
    internal static readonly IInterceptor Interceptor = new Pragmas();

    private sealed class Pragmas : DbConnectionInterceptor
    {
        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            if (connection is SqliteConnection)
            {
                using var command = connection.CreateCommand();
                command.CommandText = PerConnection;
                command.ExecuteNonQuery();
            }
        }

        public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (connection is SqliteConnection)
            {
                var command = connection.CreateCommand();
                await using (command)
                {
                    command.CommandText = PerConnection;
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
            }
        }
    }
}
