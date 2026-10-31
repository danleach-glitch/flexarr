using System.Collections.Concurrent;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Flexarr.Api.Trakt;

/// <summary>
/// A profile's Trakt token, kept fresh: refreshed ahead of its 24 hours, once at a time per profile, and the
/// connection marked "needs sign-in again" when Trakt refuses the refresh.
/// </summary>
public sealed partial class TraktAccounts(FlexarrDbContext db, TraktClient trakt, ILogger<TraktAccounts> logger)
{
    /// <summary>Refreshed when it has less than this left.</summary>
    internal static readonly TimeSpan RefreshAhead = TimeSpan.FromHours(2);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    /// <summary>
    /// Runs <paramref name="call"/> with a working token for <paramref name="profileId"/>: refreshed first if it's
    /// close to expiring, and once more if Trakt turns it down. Null when the profile isn't connected or needs to sign
    /// in again.
    /// </summary>
    public async Task<(bool Ok, T? Result)> WithTokenAsync<T>(string profileId, Func<string, Task<T>> call, CancellationToken ct)
    {
        var token = await TokenAsync(profileId, force: false, ct);
        if (token is null)
        {
            return (false, default);
        }

        try
        {
            return (true, await call(token));
        }
        catch (TraktException ex) when (ex.Unauthorized)
        {
            token = await TokenAsync(profileId, force: true, ct);
            if (token is null)
            {
                return (false, default);
            }

            return (true, await call(token));
        }
    }

    /// <summary>A token good for a while yet, refreshing it if need be; null if there's no connection that works.</summary>
    public async Task<string?> TokenAsync(string profileId, bool force, CancellationToken ct)
    {
        var connection = await db.TraktConnections.AsNoTracking().FirstOrDefaultAsync(c => c.ProfileId == profileId, ct);
        if (connection is not { Status: TraktStatus.Connected })
        {
            return null;
        }

        if (!force && connection.ExpiresAt - DateTime.UtcNow > RefreshAhead)
        {
            return connection.AccessToken;
        }

        var gate = Gates.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Read from the database, not this context: a row it tracks (the sync loads one) may predate another
            // scope's refresh, and refreshing again with a used refresh token would sign the profile out.
            var fresh = await db.TraktConnections.AsNoTracking().FirstOrDefaultAsync(c => c.ProfileId == profileId, ct);
            if (fresh is not { Status: TraktStatus.Connected })
            {
                return null;
            }

            if (fresh.AccessToken != connection.AccessToken || (!force && fresh.ExpiresAt - DateTime.UtcNow > RefreshAhead))
            {
                return fresh.AccessToken;
            }

            TraktToken? token;
            try
            {
                token = await trakt.RefreshAsync(fresh.RefreshToken, ct);
            }
            catch (TraktException ex)
            {
                // Trakt or the relay is away: the old token may have time left, and the next try refreshes.
                await SaveAsync(profileId, c => c.SetProperty(x => x.LastError, ex.Message), ct);
                return fresh.ExpiresAt > DateTime.UtcNow ? fresh.AccessToken : null;
            }

            if (token is null)
            {
                LogNeedsSignIn(profileId);
                await SaveAsync(profileId, c => c.SetProperty(x => x.Status, TraktStatus.NeedsSignIn).SetProperty(x => x.LastError, "Trakt signed Flexarr out. Connect again."), ct);
                return null;
            }

            Apply(fresh, token);
            await SaveAsync(profileId, c => c.SetProperty(x => x.AccessToken, fresh.AccessToken).SetProperty(x => x.RefreshToken, fresh.RefreshToken)
                .SetProperty(x => x.ExpiresAt, fresh.ExpiresAt).SetProperty(x => x.Status, TraktStatus.Connected).SetProperty(x => x.LastError, (string?)null), ct);
            return fresh.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Writes the connection straight to the database, and into a copy this context tracks (if any) so a later save of
    /// that copy doesn't put the old values back.
    /// </summary>
    private async Task SaveAsync(string profileId,
        Action<UpdateSettersBuilder<TraktConnection>> set,
        CancellationToken ct)
    {
        await db.TraktConnections.Where(c => c.ProfileId == profileId).ExecuteUpdateAsync(set, ct);
        if (db.ChangeTracker.Entries<TraktConnection>().FirstOrDefault(e => e.Entity.ProfileId == profileId) is { } tracked)
        {
            await tracked.ReloadAsync(ct);
        }
    }

    internal static void Apply(TraktConnection connection, TraktToken token)
    {
        if (string.IsNullOrEmpty(token.AccessToken) || string.IsNullOrEmpty(token.RefreshToken))
        {
            throw new TraktException(null, "Trakt sent back an empty sign-in.");
        }

        connection.AccessToken = token.AccessToken;
        connection.RefreshToken = token.RefreshToken;
        var created = token.CreatedAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(token.CreatedAt).UtcDateTime : DateTime.UtcNow;
        // Trusting our own clock over Trakt's when they disagree by much: expiry is about when we'll next refresh.
        if (Math.Abs((created - DateTime.UtcNow).TotalHours) > 1)
        {
            created = DateTime.UtcNow;
        }

        connection.ExpiresAt = created.AddSeconds(token.ExpiresIn > 0 ? token.ExpiresIn : 86_400);
        connection.Status = TraktStatus.Connected;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Trakt refused the refresh for profile {Profile}: it needs to connect again")]
    private partial void LogNeedsSignIn(string profile);
}
