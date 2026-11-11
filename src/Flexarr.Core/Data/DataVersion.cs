using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Flexarr.Core.Data;

/// <summary>
/// A number that moves on after every write to the database, of any kind: a save that changed something, a bulk update
/// or delete, raw SQL, a transaction committed. It moves once the write is in, so a cache built from what it read under
/// one number can be kept while the number stays put (Home keeps each profile's for a minute). Unlike
/// <see cref="CatalogVersion"/> it covers everything, and it moves far more often.
/// </summary>
public static class DataVersion
{
    private static long value;

    public static long Current => Interlocked.Read(ref value);

    private static readonly AsyncLocal<bool> quiet = new();

    public static void Bump() => Interlocked.Increment(ref value);

    /// <summary>
    /// For bookkeeping nothing cached on this number shows (a download's progress, a podcast feed checked, loudness
    /// read, intro and credits found): writes in the scope it opens leave the number alone, so Home isn't worked out
    /// again for them. A save in it that changes a title, person, credit, genre or watch state still moves it.
    /// </summary>
    public static IDisposable Quietly()
    {
        var was = quiet.Value;
        quiet.Value = true;
        return new Restore(was);
    }

    /// <summary>Whether writes now are in a <see cref="Quietly"/> scope.</summary>
    public static bool IsQuiet => quiet.Value;

    private sealed class Restore(bool was) : IDisposable
    {
        public void Dispose() => quiet.Value = was;
    }

    /// <summary>Moves it on after commands that change rows (bulk updates and deletes, raw SQL) and after each commit.</summary>
    internal static readonly IInterceptor[] Interceptors = [new Commands(), new Transactions()];

    private sealed class Commands : DbCommandInterceptor
    {
        public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
        {
            Moved(result);
            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Moved(result);
            return ValueTask.FromResult(result);
        }

        // An update or delete that matched nothing changed nothing; anything else (-1 for a statement that isn't one) might have.
        private static void Moved(int result)
        {
            if (result != 0 && !IsQuiet)
            {
                Bump();
            }
        }
    }

    private sealed class Transactions : DbTransactionInterceptor
    {
        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
        {
            if (!IsQuiet)
            {
                Bump();
            }
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            TransactionCommitted(transaction, eventData);
            return Task.CompletedTask;
        }
    }
}
