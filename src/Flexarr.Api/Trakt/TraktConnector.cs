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
            _pending.TryRemove(new KeyValuePair<string, Pending>(profileId, pending));
            return null;
        }

        return pending.Dto();
    }

    public async Task<TraktPendingConnect> StartAsync(string profileId, CancellationToken ct)
    {
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
            // A new account (or another one than before) starts its sync afresh.
            if (connection is not null)
            {
                db.TraktConnections.Remove(connection);
                await db.SaveChangesAsync(ct);
            }

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
        TraktSyncWorker.Nudge();
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
