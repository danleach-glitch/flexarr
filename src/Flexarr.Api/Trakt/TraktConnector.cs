using System.Collections.Concurrent;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Trakt;

/// <summary>Where a connect is: waiting for the code to be entered, or how it ended.</summary>
public enum TraktConnectState
{
    Waiting,
    Connected,
    Expired,
    Denied,

    /// <summary>That Trakt account is already connected to another profile here.</summary>
    InUse,

    Failed,
}

/// <param name="UserCode">What to type at <paramref name="VerificationUrl"/>.</param>
/// <param name="QrUrl">The address to put in a QR code: Trakt's activation page (the code is typed there).</param>
public sealed record TraktPendingConnect(TraktConnectState State, string UserCode, string VerificationUrl, string QrUrl, DateTime ExpiresAt, string? Error);

/// <summary>
/// Connects profiles by Trakt's device code: hands out the code, then polls Trakt for it at Trakt's interval, here on
/// the server, so the app showing the code can be closed. One connect at a time per profile.
/// </summary>
public sealed partial class TraktConnector(IServiceScopeFactory scopes, ILogger<TraktConnector> logger)
{
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    /// <summary>How often to poll at the least, whatever Trakt says (tests set it lower).</summary>
    internal TimeSpan MinimumInterval { get; set; } = TimeSpan.FromSeconds(1);

    private sealed class Pending(TraktDeviceCode code, DateTime expiresAt)
    {
        public TraktDeviceCode Code { get; } = code;
        public DateTime ExpiresAt { get; } = expiresAt;
        public CancellationTokenSource Stop { get; } = new();
        public TraktConnectState State { get; set; } = TraktConnectState.Waiting;
        public string? Error { get; set; }
        public DateTime? EndedAt { get; set; }

        public TraktPendingConnect Dto() => new(State, Code.UserCode, Code.VerificationUrl, Code.VerificationUrl, ExpiresAt, Error);
    }

    /// <summary>The profile's connect in progress, or the one that just ended (for a minute, so the app can say how).</summary>
    public TraktPendingConnect? Current(string profileId)
    {
        if (!_pending.TryGetValue(profileId, out var pending))
        {
            return null;
        }

        if (pending.EndedAt is { } ended && DateTime.UtcNow - ended > TimeSpan.FromMinutes(1))
        {
            if (_pending.TryRemove(new KeyValuePair<string, Pending>(profileId, pending)))
            {
                pending.Stop.Dispose();
            }

            return null;
        }

        return pending.Dto();
    }

    /// <summary>A profile can ask Trakt for a new code at most this often.</summary>
    internal TimeSpan StartGap { get; set; } = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, DateTime> _started = new(StringComparer.Ordinal);

    public async Task<TraktPendingConnect> StartAsync(string profileId, CancellationToken ct)
    {
        if (DateTime.UtcNow - _started.GetValueOrDefault(profileId) < StartGap && Current(profileId) is { State: TraktConnectState.Waiting } waiting)
        {
            return waiting; // Pressed twice: the code already showing.
        }

        _started[profileId] = DateTime.UtcNow;
        Cancel(profileId);
        await using var scope = scopes.CreateAsyncScope();
        var trakt = scope.ServiceProvider.GetRequiredService<TraktClient>();
        var code = await trakt.DeviceCodeAsync(ct);
        var pending = new Pending(code, DateTime.UtcNow.AddSeconds(Math.Clamp(code.ExpiresIn, 60, 3600)));
        _pending[profileId] = pending;
        _ = Task.Run(() => PollAsync(profileId, pending), CancellationToken.None);
        return pending.Dto();
    }

    public void Cancel(string profileId)
    {
        if (_pending.TryRemove(profileId, out var old))
        {
            old.Stop.Cancel();
            old.Stop.Dispose();
        }
    }

    private async Task PollAsync(string profileId, Pending pending)
    {
        var ct = pending.Stop.Token;
        var interval = TimeSpan.FromSeconds(pending.Code.Interval);
        if (interval < MinimumInterval)
        {
            interval = MinimumInterval;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct);
                if (DateTime.UtcNow >= pending.ExpiresAt)
                {
                    End(pending, TraktConnectState.Expired, null);
                    return;
                }

                await using var scope = scopes.CreateAsyncScope();
                var trakt = scope.ServiceProvider.GetRequiredService<TraktClient>();
                var (state, token) = await trakt.DeviceTokenAsync(pending.Code.DeviceCode, ct);
                switch (state)
                {
                    case DeviceTokenState.Pending or DeviceTokenState.Unreachable:
                        continue;
                    case DeviceTokenState.SlowDown:
                        interval += TimeSpan.FromSeconds(1);
                        continue;
                    case DeviceTokenState.Expired:
                        End(pending, TraktConnectState.Expired, null);
                        return;
                    case DeviceTokenState.Denied:
                        End(pending, TraktConnectState.Denied, null);
                        return;
                }

                await SaveAsync(scope.ServiceProvider, profileId, pending, token!, ct);
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled, or a newer connect took over.
        }
        catch (Exception ex)
        {
            LogConnectFailed(ex, profileId);
            End(pending, TraktConnectState.Failed, ex is TraktException ? ex.Message : "Something went wrong connecting to Trakt.");
        }
    }

    private async Task SaveAsync(IServiceProvider services, string profileId, Pending pending, TraktToken token, CancellationToken ct)
    {
        var trakt = services.GetRequiredService<TraktClient>();
        var db = services.GetRequiredService<FlexarrDbContext>();
        var user = (await trakt.SettingsAsync(token.AccessToken, ct)).User;
        var slug = user.Ids.Slug ?? user.Username;

        // One profile per Trakt account, or every play would be scrobbled twice.
        if (await db.TraktConnections.AnyAsync(c => c.UserSlug == slug && c.ProfileId != profileId, ct))
        {
            await trakt.RevokeAsync(token.AccessToken, ct);
            End(pending, TraktConnectState.InUse, $"{user.Username} is already connected to another profile.");
            return;
        }

        var connection = await db.TraktConnections.FirstOrDefaultAsync(c => c.ProfileId == profileId, ct);
        if (connection is null || connection.UserSlug != slug)
        {
            // A new account (or another one than before) starts afresh: nothing queued for, held from or listed by the
            // old one carries over.
            if (connection is not null)
            {
                db.TraktConnections.Remove(connection);
                await db.SaveChangesAsync(ct);
            }

            await TraktConnections.ForgetAsync(db, profileId, ct);

            connection = new TraktConnection
            {
                ProfileId = profileId, AccessToken = token.AccessToken, RefreshToken = token.RefreshToken, UserSlug = slug, Username = user.Username,
            };
            db.TraktConnections.Add(connection);
        }

        TraktAccounts.Apply(connection, token);
        connection.Username = user.Username;
        connection.LastError = null;
        connection.ConnectedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        LogConnected(profileId);
        End(pending, TraktConnectState.Connected, null);
        TraktSyncWorker.Nudge(profileId);
    }

    private static void End(Pending pending, TraktConnectState state, string? error)
    {
        pending.State = state;
        pending.Error = error;
        pending.EndedAt = DateTime.UtcNow;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Profile {Profile} connected to Trakt")]
    private partial void LogConnected(string profile);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connecting profile {Profile} to Trakt failed")]
    private partial void LogConnectFailed(Exception ex, string profile);
}

/// <summary>Ending a profile's Trakt connection: Disconnect, a switch to another account, becoming a kids profile, deletion.</summary>
public static class TraktConnections
{
    /// <summary>Removes what belongs to the profile's Trakt account: its queue, its held plays and its Trakt-list collections.</summary>
    public static async Task ForgetAsync(FlexarrDbContext db, string profileId, CancellationToken ct)
    {
        await db.TraktOutbox.Where(o => o.ProfileId == profileId).ExecuteDeleteAsync(ct);
        await db.TraktHistoryHolds.Where(h => h.ProfileId == profileId).ExecuteDeleteAsync(ct);
        await db.Collections.Where(c => c.OwnerProfileId == profileId && c.Kind == CollectionKind.TraktList).ExecuteDeleteAsync(ct);
        // What was on the old account's list says nothing about the next one's.
        await db.WatchlistEntries.Where(w => w.ProfileId == profileId && w.OnTraktAt != null).ExecuteUpdateAsync(u => u.SetProperty(w => w.OnTraktAt, (DateTime?)null), ct);
    }

    /// <summary>Disconnects the profile: Trakt forgets the token (best effort), and so does Flexarr, with all of the above.</summary>
    public static async Task DisconnectAsync(FlexarrDbContext db, TraktClient trakt, TraktConnector connector, string profileId, CancellationToken ct)
    {
        connector.Cancel(profileId);
        if (await db.TraktConnections.AsNoTracking().FirstOrDefaultAsync(c => c.ProfileId == profileId, ct) is not { } connection)
        {
            return;
        }

        // Forgotten here first, so a slow Trakt can't leave it half done; then Trakt is told, briefly.
        await db.TraktConnections.Where(c => c.ProfileId == profileId).ExecuteDeleteAsync(ct);
        await ForgetAsync(db, profileId, ct);
        using var brief = CancellationTokenSource.CreateLinkedTokenSource(ct);
        brief.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await trakt.RevokeAsync(connection.AccessToken, brief.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Trakt didn't answer in time: the token expires by itself within a day.
        }
    }
}
