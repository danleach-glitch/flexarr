using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Configuration;
using Flexarr.Core.Localization;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Trakt;

/// <summary>Trakt said no, or couldn't be reached. <see cref="Status"/> is null when it couldn't be reached.</summary>
/// <param name="message">English, marked with Loc.Mark: <see cref="Exception.Message"/> stays English (it's logged, and
/// kept as a profile's last error), and <see cref="Localized"/> is it in the request's language.</param>
public sealed class TraktException(HttpStatusCode? status, string message, TimeSpan? retryAfter = null, object? args = null)
    : Exception(English(message, args))
{
    /// <summary>The message in the request's language, for an answer.</summary>
    public string Localized => Loc.T(message, args);

    public HttpStatusCode? Status { get; } = status;

    /// <summary>Trakt's Retry-After, on a 429.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>The token is no good (revoked, expired): refresh, or connect again.</summary>
    public bool Unauthorized => Status == HttpStatusCode.Unauthorized;

    /// <summary>A free account's limit (Trakt answers 420 for "account limit exceeded", e.g. a full watchlist).</summary>
    public bool AccountLimit => (int?)Status == 420;

    private static string English(string message, object? args)
    {
        using (Loc.Use(null))
        {
            return Loc.T(message, args);
        }
    }
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

    /// <summary>Trakt couldn't be reached: try again at the next interval.</summary>
    Unreachable,
}

/// <summary>
/// Talks to Trakt, straight from this server: every call needs only the Trakt app's public client id (and, once
/// signed in, the person's own token). Trakt's device code sign-in, token refresh and revoke need no client secret, so
/// none ships and nothing stands between a server and Trakt. An admin's own app may still carry a secret from before;
/// it's sent with the token calls if so.
/// </summary>
public sealed partial class TraktClient(HttpClient http, IOptionsMonitor<FlexarrOptions> options, ILogger<TraktClient> logger)
{
    private TraktOptions Options => options.CurrentValue.Trakt;

    private Uri Api(string path) => new(Options.ApiUrl.TrimEnd('/') + path);

    /// <summary>The Trakt app's client id: the admin's own, or Flexarr's. Null when there's neither, and Trakt stays hidden.</summary>
    public string? ClientId => Options.ActiveClientId;

    private string RequireClientId() => ClientId ?? throw new TraktException(null, Loc.Mark("Trakt isn’t set up on this server."));

    // --- Connecting: device code, then its token.

    public async Task<TraktDeviceCode> DeviceCodeAsync(CancellationToken ct)
    {
        var id = RequireClientId();
        using var response = await SendAsync(HttpMethod.Post, Api("/oauth/device/code"), new { client_id = id }, null, id, ct);
        return await ReadAsync<TraktDeviceCode>(response, ct);
    }

    public async Task<(DeviceTokenState State, TraktToken? Token)> DeviceTokenAsync(string deviceCode, CancellationToken ct)
    {
        try
        {
            using var response = await TokenCallAsync("device_token", "/oauth/device/token", new() { ["code"] = deviceCode }, ct);
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
            new() { ["refresh_token"] = refreshToken, ["redirect_uri"] = "urn:ietf:wg:oauth:2.0:oob", ["grant_type"] = "refresh_token" }, ct);
        if (response.IsSuccessStatusCode)
        {
            return await ReadAsync<TraktToken>(response, ct);
        }

        // Only Trakt turning the refresh token down signs the profile out: a 400 or 401 saying invalid_grant. Anything
        // else (Trakt unwell, or invalid_client from a client id that's wrong) is tried again later, so a slip in the
        // app's settings doesn't sign every profile out.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest
            && (await response.Content.ReadAsStringAsync(ct)).Contains("invalid_grant", StringComparison.Ordinal))
        {
            return null;
        }

        throw new TraktException(response.StatusCode, Loc.Mark("Couldn’t refresh the Trakt sign-in."));
    }

    /// <summary>Tells Trakt to forget a token; best effort, as Disconnect clears it here either way.</summary>
    public async Task RevokeAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var response = await TokenCallAsync("revoke", "/oauth/revoke", new() { ["token"] = accessToken }, ct);
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

    /// <summary>A token call: the body plus the client id, and the secret only when the admin's own app has one.</summary>
    private async Task<HttpResponseMessage> TokenCallAsync(string grant, string path, Dictionary<string, string> body, CancellationToken ct)
    {
        body["client_id"] = RequireClientId();
        if (Options.OwnSecret is { } secret)
        {
            body["client_secret"] = secret;
        }

        try
        {
            return await http.PostAsJsonAsync(Api(path), body, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LogTokenUnreachable(grant, ex.Message);
            throw new TraktException(null, Loc.Mark("Couldn’t reach Trakt."));
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
        var id = RequireClientId();
        using var response = await SendAsync(HttpMethod.Post, Api(path), body, token, id, ct);
        return await ReadAsync<TraktSyncResult>(response, ct);
    }

    /// <param name="action">"start", "pause" or "stop".</param>
    public async Task ScrobbleAsync(string action, TraktScrobbleBody body, string token, CancellationToken ct)
    {
        var id = RequireClientId();
        using var response = await SendAsync(HttpMethod.Post, Api($"/scrobble/{action}"), body, token, id, ct, okStatuses: [HttpStatusCode.Conflict]);
    }

    private async Task<T> GetAsync<T>(string path, string token, CancellationToken ct)
    {
        var id = RequireClientId();
        using var response = await SendAsync(HttpMethod.Get, Api(path), null, token, id, ct);
        return await ReadAsync<T>(response, ct);
    }

    private async Task<List<T>> GetAllPagesAsync<T>(string path, string token, CancellationToken ct, int limit = 250)
    {
        var id = RequireClientId();
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
            throw new TraktException(null, Loc.Mark("Couldn’t reach Trakt."));
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
            401 => Loc.Mark("Trakt didn’t accept the sign-in."),
            403 => Loc.Mark("Trakt refused this app."),
            420 => Loc.Mark("Your Trakt account’s limit is reached."),
            423 => Loc.Mark("Trakt has locked this account."),
            429 => Loc.Mark("Trakt asked Flexarr to slow down."),
            >= 500 => Loc.Mark("Trakt is having problems."),
            _ => Loc.Mark("Trakt answered {status}."),
        }, retryAfter, new { status = (int)status });
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(TraktJson.Options, ct) ?? throw new TraktException(response.StatusCode, Loc.Mark("Trakt sent nothing back."));
        }
        catch (JsonException)
        {
            throw new TraktException(response.StatusCode, Loc.Mark("Trakt’s answer didn’t make sense."));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Couldn't reach Trakt for {Grant}: {Reason}")]
    private partial void LogTokenUnreachable(string grant, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Couldn't revoke a Trakt token: {Reason}")]
    private partial void LogRevokeFailed(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Couldn't reach Trakt for {Path}: {Reason}")]
    private partial void LogUnreachable(string path, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Trakt refused {Path} with {Status}")]
    private partial void LogRefused(string path, int status);
}
