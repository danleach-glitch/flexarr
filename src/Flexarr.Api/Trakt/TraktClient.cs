using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Trakt;

/// <summary>Trakt said no, or couldn't be reached. <see cref="Status"/> is null when it couldn't be reached.</summary>
public sealed class TraktException(HttpStatusCode? status, string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;

    /// <summary>Trakt's Retry-After, on a 429.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>The token is no good (revoked, expired): refresh, or connect again.</summary>
    public bool Unauthorized => Status == HttpStatusCode.Unauthorized;

    /// <summary>A free account's limit (Trakt answers 420 for "account limit exceeded", e.g. a full watchlist).</summary>
    public bool AccountLimit => (int?)Status == 420;
}

/// <summary>Trakt's answer to polling a device code.</summary>
public enum DeviceTokenState
{
    Approved,

    /// <summary>Not yet approved: keep polling.</summary>
    Pending,

    /// <summary>Polling too fast: wait longer.</summary>
    SlowDown,

    /// <summary>Expired, used already, or not known: start again.</summary>
    Expired,

    Denied,

    /// <summary>Trakt or the relay couldn't be reached: try again at the next interval.</summary>
    Unreachable,
}

/// <summary>
/// Talks to Trakt. Every call but the token ones needs only the client id and the person's own token, so it goes
/// straight to Trakt; the token calls (device token, refresh, revoke) need the client secret too, so they go through
/// the relay, which holds it, unless the admin set up their own app (see <see cref="TraktOptions"/>).
/// </summary>
public sealed partial class TraktClient(HttpClient http, IOptionsMonitor<FlexarrOptions> options, ILogger<TraktClient> logger)
{
    private static readonly TimeSpan ClientIdLifetime = TimeSpan.FromHours(6);
    private static readonly SemaphoreSlim ClientIdGate = new(1, 1);
    private static (string Relay, string? Id, DateTime At)? relayClientId;

    private TraktOptions Options => options.CurrentValue.Trakt;

    private Uri Api(string path) => new(Options.ApiUrl.TrimEnd('/') + path);

    /// <summary>Forgets the client id the relay gave (tests, and a relay that changed apps).</summary>
    internal static void ForgetClientId() => relayClientId = null;

    /// <summary>How long "the relay has no Trakt app" is believed before asking again.</summary>
    private static readonly TimeSpan NotSetUpFor = TimeSpan.FromMinutes(10);

    /// <summary>The relay is asked for the id with a short wait of its own: a slow relay mustn't hold up every call.</summary>
    private static readonly TimeSpan RelayTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The Trakt app's client id: the admin's own, or Flexarr's from the relay (cached a few hours). Null when there's
    /// neither, and Trakt stays hidden. "Not set up" is remembered a few minutes; while the relay is unreachable or
    /// answers with an error, the id it gave before is kept.
    /// </summary>
    public async Task<string?> ClientIdAsync(CancellationToken ct)
    {
        var o = Options;
        if (o.HasOwnApp)
        {
            return o.ClientId;
        }

        if (o.RelayUrl.Length == 0 || !Uri.TryCreate(o.RelayUrl, UriKind.Absolute, out var relay))
        {
            return null;
        }

        static bool Fresh((string Relay, string? Id, DateTime At)? c, string url) =>
            c is { } v && v.Relay == url && DateTime.UtcNow - v.At < (v.Id is null ? NotSetUpFor : ClientIdLifetime);

        if (Fresh(relayClientId, o.RelayUrl))
        {
            return relayClientId!.Value.Id;
        }

        await ClientIdGate.WaitAsync(ct);
        try
        {
            if (Fresh(relayClientId, o.RelayUrl))
            {
                return relayClientId!.Value.Id;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RelayTimeout);
            using var response = await http.GetAsync(relay, timeout.Token);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<RelayClientId>(timeout.Token) is { ClientId.Length: > 0 } answer)
            {
                relayClientId = (o.RelayUrl, answer.ClientId, DateTime.UtcNow);
                return answer.ClientId;
            }

            if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.NotFound)
            {
                // Not set up there (or not there yet): nothing to offer for a while.
                relayClientId = (o.RelayUrl, null, DateTime.UtcNow);
                return null;
            }

            return KeepStale(o.RelayUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            LogRelayUnreachable(ex.Message);
            return KeepStale(o.RelayUrl);
        }
        finally
        {
            ClientIdGate.Release();
        }
    }

    /// <summary>An id fetched before stays good while the relay is away, trusted for another while before asking again.</summary>
    private static string? KeepStale(string relayUrl)
    {
        if (relayClientId is { Id: { } id } stale && stale.Relay == relayUrl)
        {
            relayClientId = (relayUrl, id, DateTime.UtcNow - ClientIdLifetime + TimeSpan.FromMinutes(30));
            return id;
        }

        relayClientId = (relayUrl, null, DateTime.UtcNow);
        return null;
    }

    // --- Connecting: device code, then its token.

    public async Task<TraktDeviceCode> DeviceCodeAsync(CancellationToken ct)
    {
        var id = await ClientIdAsync(ct) ?? throw new TraktException(null, "Trakt isn’t set up on this server.");
        using var response = await SendAsync(HttpMethod.Post, Api("/oauth/device/code"), new { client_id = id }, null, id, ct);
        return await ReadAsync<TraktDeviceCode>(response, ct);
    }

    public async Task<(DeviceTokenState State, TraktToken? Token)> DeviceTokenAsync(string deviceCode, CancellationToken ct)
    {
        try
        {
            using var response = await TokenCallAsync("device_token", "/oauth/device/token",
                own => new { code = deviceCode, client_id = own.ClientId, client_secret = own.ClientSecret },
                new { grant = "device_token", code = deviceCode }, ct);
            return (int)response.StatusCode switch
            {
                200 => (DeviceTokenState.Approved, await ReadAsync<TraktToken>(response, ct)),
                400 => (DeviceTokenState.Pending, null),
                429 => (DeviceTokenState.SlowDown, null),
                404 or 409 or 410 => (DeviceTokenState.Expired, null),
                418 => (DeviceTokenState.Denied, null),
                _ => (DeviceTokenState.Unreachable, null),
            };
        }
        catch (TraktException ex) when (ex.Status is null)
        {
            return (DeviceTokenState.Unreachable, null);
        }
    }

    /// <summary>A new token for a refresh token; null when Trakt refused it (connect again).</summary>
    public async Task<TraktToken?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        using var response = await TokenCallAsync("refresh_token", "/oauth/token",
            own => new
            {
                refresh_token = refreshToken, client_id = own.ClientId, client_secret = own.ClientSecret,
                redirect_uri = "urn:ietf:wg:oauth:2.0:oob", grant_type = "refresh_token",
            },
            new { grant = "refresh_token", refreshToken }, ct);
        if (response.IsSuccessStatusCode)
        {
            return await ReadAsync<TraktToken>(response, ct);
        }

        // Only Trakt turning the refresh token down signs the profile out: a 400 or 401 saying invalid_grant. Anything
        // else (the relay or Trakt unwell, the relay's own checks) is tried again later.
        // Not invalid_client and the like: a wrong secret on the relay must not sign every profile out.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest
            && (await response.Content.ReadAsStringAsync(ct)).Contains("invalid_grant", StringComparison.Ordinal))
        {
            return null;
        }

        throw new TraktException(response.StatusCode, "Couldn’t refresh the Trakt sign-in.");
    }

    /// <summary>Tells Trakt to forget a token; best effort, as Disconnect clears it here either way.</summary>
    public async Task RevokeAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var response = await TokenCallAsync("revoke", "/oauth/revoke",
                own => new { token = accessToken, client_id = own.ClientId, client_secret = own.ClientSecret },
                new { grant = "revoke", token = accessToken }, ct);
            if (!response.IsSuccessStatusCode)
            {
                LogRevokeFailed($"answered {(int)response.StatusCode}");
            }
        }
        catch (TraktException ex)
        {
            LogRevokeFailed(ex.Message);
        }
    }

    private async Task<HttpResponseMessage> TokenCallAsync(string grant, string path, Func<TraktOptions, object> direct, object viaRelay, CancellationToken ct)
    {
        var o = Options;
        try
        {
            if (o.HasOwnApp)
            {
                return await http.PostAsJsonAsync(Api(path), direct(o), ct);
            }

            if (!Uri.TryCreate(o.RelayUrl, UriKind.Absolute, out var relay))
            {
                throw new TraktException(null, "Trakt isn’t set up on this server.");
            }

            return await http.PostAsJsonAsync(relay, viaRelay, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LogTokenUnreachable(grant, ex.Message);
            throw new TraktException(null, "Couldn’t reach Trakt.");
        }
    }

    // --- Signed-in calls.

    public Task<TraktUserSettings> SettingsAsync(string token, CancellationToken ct) => GetAsync<TraktUserSettings>("/users/settings", token, ct);

    public Task<TraktLastActivities> LastActivitiesAsync(string token, CancellationToken ct) => GetAsync<TraktLastActivities>("/sync/last_activities", token, ct);

    public Task<List<TraktWatchedMovie>> WatchedMoviesAsync(string token, CancellationToken ct) => GetAsync<List<TraktWatchedMovie>>("/sync/watched/movies", token, ct);

    public Task<List<TraktWatchedShow>> WatchedShowsAsync(string token, CancellationToken ct) => GetAsync<List<TraktWatchedShow>>("/sync/watched/shows", token, ct);

    public Task<List<TraktPlayback>> PlaybackAsync(string token, CancellationToken ct) => GetAsync<List<TraktPlayback>>("/sync/playback?limit=100", token, ct);

    public Task<List<TraktWatchlistItem>> WatchlistAsync(string token, CancellationToken ct) => GetAllPagesAsync<TraktWatchlistItem>("/sync/watchlist", token, ct);

    public Task<List<TraktList>> ListsAsync(string token, CancellationToken ct) => GetAsync<List<TraktList>>("/users/me/lists", token, ct);

    public Task<List<TraktLikedList>> LikedListsAsync(string token, CancellationToken ct) => GetAsync<List<TraktLikedList>>("/users/likes/lists?limit=100", token, ct);

    public Task<List<TraktListItem>> ListItemsAsync(string token, string userSlug, string listId, CancellationToken ct) =>
        GetAllPagesAsync<TraktListItem>($"/users/{Uri.EscapeDataString(userSlug)}/lists/{Uri.EscapeDataString(listId)}/items", token, ct);

    /// <param name="kind">"movies" or "shows".</param>
    public Task<List<TraktRecommendation>> RecommendationsAsync(string token, string kind, CancellationToken ct) =>
        GetAsync<List<TraktRecommendation>>($"/recommendations/{kind}?ignore_collected=true&ignore_watchlisted=true&limit=40", token, ct);

    /// <summary>Plays after <paramref name="since"/>, oldest first, every page.</summary>
    public async Task<List<TraktHistoryItem>> HistorySinceAsync(string token, DateTime since, CancellationToken ct)
    {
        var start = Uri.EscapeDataString(since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture));
        var items = await GetAllPagesAsync<TraktHistoryItem>($"/sync/history?start_at={start}", token, ct);
        items.Sort((a, b) => a.WatchedAt.CompareTo(b.WatchedAt));
        return items;
    }

    /// <summary>Sends a <c>/sync</c> change ("/sync/history", "/sync/watchlist", "/sync/watchlist/remove").</summary>
    public async Task<TraktSyncResult> SyncAsync(string path, TraktSyncBody body, string token, CancellationToken ct)
    {
        var id = await ClientIdAsync(ct) ?? throw new TraktException(null, "Trakt isn’t set up on this server.");
        using var response = await SendAsync(HttpMethod.Post, Api(path), body, token, id, ct);
        return await ReadAsync<TraktSyncResult>(response, ct);
    }

    /// <param name="action">"start", "pause" or "stop".</param>
    public async Task ScrobbleAsync(string action, TraktScrobbleBody body, string token, CancellationToken ct)
    {
        var id = await ClientIdAsync(ct) ?? throw new TraktException(null, "Trakt isn’t set up on this server.");
        using var response = await SendAsync(HttpMethod.Post, Api($"/scrobble/{action}"), body, token, id, ct, okStatuses: [HttpStatusCode.Conflict]);
    }

    private async Task<T> GetAsync<T>(string path, string token, CancellationToken ct)
    {
        var id = await ClientIdAsync(ct) ?? throw new TraktException(null, "Trakt isn’t set up on this server.");
        using var response = await SendAsync(HttpMethod.Get, Api(path), null, token, id, ct);
        return await ReadAsync<T>(response, ct);
    }

    private async Task<List<T>> GetAllPagesAsync<T>(string path, string token, CancellationToken ct, int limit = 250)
    {
        var id = await ClientIdAsync(ct) ?? throw new TraktException(null, "Trakt isn’t set up on this server.");
        var all = new List<T>();
        var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        for (var page = 1; page <= 200; page++)
        {
            using var response = await SendAsync(HttpMethod.Get, Api($"{path}{separator}page={page}&limit={limit}"), null, token, id, ct);
            all.AddRange(await ReadAsync<List<T>>(response, ct));
            var pages = response.Headers.TryGetValues("X-Pagination-Page-Count", out var values) && int.TryParse(values.FirstOrDefault(), out var count) ? count : 1;
            if (page >= pages)
            {
                break;
            }
        }

        return all;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri url, object? body, string? token, string clientId, CancellationToken ct,
        HttpStatusCode[]? okStatuses = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("trakt-api-version", "2");
        request.Headers.Add("trakt-api-key", clientId);
        request.Headers.UserAgent.ParseAdd($"Flexarr/{typeof(TraktClient).Assembly.GetName().Version?.ToString(3) ?? "0"}");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: TraktJson.Options);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LogUnreachable(url.AbsolutePath, ex.Message);
            throw new TraktException(null, "Couldn’t reach Trakt.");
        }

        if (response.IsSuccessStatusCode || okStatuses?.Contains(response.StatusCode) == true)
        {
            return response;
        }

        var status = response.StatusCode;
        var retryAfter = response.Headers.RetryAfter?.Delta;
        response.Dispose();
        LogRefused(url.AbsolutePath, (int)status);
        throw new TraktException(status, (int)status switch
        {
            401 => "Trakt didn’t accept the sign-in.",
            403 => "Trakt refused this app.",
            420 => "Your Trakt account’s limit is reached.",
            423 => "Trakt has locked this account.",
            429 => "Trakt asked Flexarr to slow down.",
            >= 500 => "Trakt is having problems.",
            _ => $"Trakt answered {(int)status}.",
        }, retryAfter);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(TraktJson.Options, ct) ?? throw new TraktException(response.StatusCode, "Trakt sent nothing back.");
        }
        catch (JsonException)
        {
            throw new TraktException(response.StatusCode, "Trakt’s answer didn’t make sense.");
        }
    }

    private sealed record RelayClientId(string? ClientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Couldn't reach the Trakt relay: {Reason}")]
    private partial void LogRelayUnreachable(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Couldn't reach Trakt for {Grant}: {Reason}")]
    private partial void LogTokenUnreachable(string grant, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Couldn't revoke a Trakt token: {Reason}")]
    private partial void LogRevokeFailed(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Couldn't reach Trakt for {Path}: {Reason}")]
    private partial void LogUnreachable(string path, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Trakt refused {Path} with {Status}")]
    private partial void LogRefused(string path, int status);
}
