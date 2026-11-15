using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Flexarr.Core.Data;

/// <summary>
/// How the SQLite database is run beyond WAL (set once, at startup): each connection syncs to disk only at checkpoints
/// rather than on every commit. The planner is deliberately given no statistics (no ANALYZE or PRAGMA optimize):
/// MediaItems.ParentId is null for every film and show, so its statistics make the index look useless in a library of
/// mostly films, and the planner then reads every item where the index would have found a show's few
/// (LibraryScanWorkerTests checks one such plan). Queries are written for the plans SQLite picks without them.
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
