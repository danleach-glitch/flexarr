using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Flexarr.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Security;

/// <summary>A one-use Tailscale auth key, as minted for an invite.</summary>
public sealed record TailscaleKey(string Id, string Key, DateTime ExpiresAt);

/// <summary>What Tailscale said when asked to check a policy.</summary>
/// <param name="Problems">Empty when the policy (and its tests) passed.</param>
public sealed record PolicyValidation(IReadOnlyList<string> Problems)
{
    public bool Passed => Problems.Count == 0;
}

public sealed class TailscaleApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// The few calls Flexarr makes to Tailscale's API, with the owner's OAuth client: create a one-use guest key, delete
/// one, read the tailnet policy and ask Tailscale to validate a policy. The client is limited to <c>auth_keys</c> (for
/// the guest tag) and <c>policy_file:read</c>, so it can't change the policy or touch devices.
/// </summary>
public sealed partial class TailscaleApi(IHttpClientFactory clients, IOptionsMonitor<FlexarrOptions> options, TimeProvider clock, ILogger<TailscaleApi> logger) : IDisposable
{
    /// <summary>The named HttpClient it uses (tests give it a fake Tailscale).</summary>
    public const string HttpClientName = "tailscale";

    private HttpClient Http => clients.CreateClient(HttpClientName);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>What the client may do, as Tailscale's tokens list it: making keys and reading the policy, nothing else.</summary>
    private static readonly HashSet<string> AllowedScopes = new(StringComparer.OrdinalIgnoreCase) { "auth_keys", "auth_keys:read", "policy_file:read" };

    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    /// <summary>The scopes of the last token Tailscale gave (null when it didn't say).</summary>
    private string? _scope;
    private (string ClientId, string Token, DateTimeOffset Until)? _token;

    private RemoteGuestOptions Settings => options.CurrentValue.RemoteGuests;

    private Uri Url(string path) => new(new Uri(Settings.ApiUrl.TrimEnd('/') + "/"), path);

    /// <summary>
    /// Mints a key that works once, is approved already, carries only the guest tag and expires at
    /// <paramref name="expiresAt"/>. The device it adds stays until the owner deletes it.
    /// </summary>
    public Task<TailscaleKey> CreateGuestKeyAsync(DateTime expiresAt, CancellationToken ct) => GuardAsync(() => CreateGuestKeyCoreAsync(expiresAt, ct), ct);

    private async Task<TailscaleKey> CreateGuestKeyCoreAsync(DateTime expiresAt, CancellationToken ct)
    {
        var seconds = (long)Math.Clamp((expiresAt - clock.GetUtcNow().UtcDateTime).TotalSeconds, 60, TimeSpan.FromDays(90).TotalSeconds);
        var body = new
        {
            capabilities = new { devices = new { create = new { reusable = false, ephemeral = false, preauthorized = true, tags = new[] { Settings.Tag } } } },
            expirySeconds = seconds,
            description = "Flexarr invite",
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("api/v2/tailnet/-/keys")) { Content = JsonContent.Create(body, options: Json) };
        using var response = await SendAsync(request, ct);
        var created = await response.Content.ReadFromJsonAsync<CreatedKey>(Json, ct)
            ?? throw new TailscaleApiException("Tailscale didn't return a key.");
        if (string.IsNullOrEmpty(created.Key) || string.IsNullOrEmpty(created.Id))
        {
            throw new TailscaleApiException("Tailscale didn't return a key.");
        }

        LogMinted(created.Id);
        return new TailscaleKey(created.Id, created.Key, created.Expires?.UtcDateTime ?? clock.GetUtcNow().UtcDateTime.AddSeconds(seconds));
    }

    /// <summary>Deletes an unused key (a cancelled invite). A key that's gone already is fine.</summary>
    public Task DeleteKeyAsync(string id, CancellationToken ct) => GuardAsync(async () =>
    {
        await DeleteKeyCoreAsync(id, ct);
        return true;
    }, ct);

    private async Task DeleteKeyCoreAsync(string id, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Url($"api/v2/tailnet/-/keys/{Uri.EscapeDataString(id)}"));
        try
        {
            using var _ = await SendAsync(request, ct);
        }
        catch (TailscaleApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
        }
    }

    /// <summary>The tailnet policy, as JSON (comments dropped).</summary>
    public Task<JsonObject> GetPolicyAsync(CancellationToken ct) => GuardAsync(() => GetPolicyCoreAsync(ct), ct);

    private async Task<JsonObject> GetPolicyCoreAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url("api/v2/tailnet/-/acl"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                ?? throw new TailscaleApiException("Tailscale's policy file isn't a JSON object.");
        }
        catch (JsonException)
        {
            throw new TailscaleApiException("Couldn't read the tailnet policy Tailscale returned.");
        }
    }

    /// <summary>Asks Tailscale whether a policy is valid and its tests pass, without saving it.</summary>
    public Task<PolicyValidation> ValidateAsync(JsonObject policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return GuardAsync(() => ValidateCoreAsync(policy, ct), ct);
    }

    private async Task<PolicyValidation> ValidateCoreAsync(JsonObject policy, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("api/v2/tailnet/-/acl/validate"))
        {
            Content = new StringContent(policy.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var response = await SendAsync(request, ct, acceptBadRequest: true);
        var text = await response.Content.ReadAsStringAsync(ct);
        return new PolicyValidation(Problems(text, response.IsSuccessStatusCode));
    }

    /// <summary>
    /// Tailscale's answer to a validation: an empty object when everything passed; otherwise a message, often with
    /// the failing tests and their errors under <c>data</c>.
    /// </summary>
    internal static List<string> Problems(string body, bool success)
    {
        JsonNode? root;
        try
        {
            root = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return success ? [] : [body.Trim() is { Length: > 0 } t ? t[..Math.Min(t.Length, 300)] : "Tailscale refused the policy."];
        }

        var problems = new List<string>();
        if (root is JsonObject obj)
        {
            if (obj["data"] is JsonArray data)
            {
                foreach (var entry in data.OfType<JsonObject>())
                {
                    var who = entry["user"]?.GetValue<string>();
                    if (entry["errors"] is JsonArray errors)
                    {
                        problems.AddRange(errors.Select(e => e?.ToString()).OfType<string>().Select(e => who is { Length: > 0 } ? $"{who}: {e}" : e));
                    }
                }
            }

            if (problems.Count == 0 && obj["message"]?.ToString() is { Length: > 0 } message)
            {
                problems.Add(message);
            }
        }

        if (problems.Count == 0 && !success)
        {
            problems.Add("Tailscale refused the policy.");
        }

        return problems;
    }

    /// <summary>Checks the OAuth client works, without creating anything.</summary>
    public async Task<string?> CheckClientAsync(CancellationToken ct)
    {
        try
        {
            await GuardAsync(() => TokenAsync(ct, forceNew: true), ct);
            // Tailscale says what the client may do. Anything beyond making guest keys and reading the policy is more
            // than Flexarr should hold: a leaked secret could then change the policy or the owner's devices.
            var extra = (_scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(s => !AllowedScopes.Contains(s)).ToList();
            return extra.Count == 0
                ? null
                : $"This OAuth client can do more than Flexarr needs ({string.Join(", ", extra)}). Make one with only Auth Keys (write) and Policy File (read).";
        }
        catch (TailscaleApiException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Runs a call to Tailscale's API, turning everything that can go wrong on the way (no connection, a timeout, an
    /// answer that isn't JSON, a proxy's page) into a <see cref="TailscaleApiException"/>, so callers have one thing
    /// to catch and nothing escapes to stop the server. Cancelling <paramref name="ct"/> still cancels.
    /// </summary>
    private static async Task<T> GuardAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        try
        {
            return await call();
        }
        catch (HttpRequestException ex)
        {
            throw new TailscaleApiException($"Couldn't reach Tailscale ({ex.Message}).");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TailscaleApiException("Tailscale didn't answer in time. Try again in a minute.");
        }
        catch (JsonException)
        {
            throw new TailscaleApiException("Tailscale's answer couldn't be read. Is something between this server and Tailscale?");
        }
        catch (NotSupportedException)
        {
            // ReadFromJsonAsync on an answer that isn't JSON at all (a captive portal's HTML page).
            throw new TailscaleApiException("Tailscale's answer couldn't be read. Is something between this server and Tailscale?");
        }
    }

    public void Dispose() => _tokenLock.Dispose();

    /// <summary>Forgets the access token, so a changed client is used straight away.</summary>
    public void Forget() => _token = null;

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct, bool acceptBadRequest = false)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        var response = await Http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode || acceptBadRequest && response.StatusCode == HttpStatusCode.BadRequest)
        {
            return response;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _token = null;
            }

            var said = await response.Content.ReadAsStringAsync(ct);
            throw new TailscaleApiException(Explain(response.StatusCode, said), response.StatusCode);
        }
    }

    private static string Explain(HttpStatusCode status, string body)
    {
        string? message = null;
        try
        {
            message = JsonNode.Parse(body)?["message"]?.ToString();
        }
        catch (JsonException)
        {
        }

        return status switch
        {
            HttpStatusCode.Unauthorized => "Tailscale didn't accept the OAuth client. Check its ID and secret.",
            HttpStatusCode.Forbidden => $"The OAuth client isn't allowed to do that{(message is null ? "" : $" ({message})")}. It needs auth_keys for the guest tag and policy_file:read.",
            HttpStatusCode.TooManyRequests => "Tailscale asked Flexarr to slow down. Try again in a minute.",
            _ => $"Tailscale answered {(int)status}{(message is null ? "" : $": {message}")}.",
        };
    }

    private async Task<string> TokenAsync(CancellationToken ct, bool forceNew = false)
    {
        var settings = Settings;
        if (!settings.HasClient)
        {
            throw new TailscaleApiException("No Tailscale OAuth client is set up.");
        }

        await _tokenLock.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            if (!forceNew && _token is { } kept && kept.ClientId == settings.ClientId && kept.Until > now)
            {
                return kept.Token;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, Url("api/v2/oauth/token"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = settings.ClientId,
                    ["client_secret"] = settings.ClientSecret,
                    ["grant_type"] = "client_credentials",
                }),
            };
            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new TailscaleApiException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden
                    ? "Tailscale didn't accept the OAuth client. Check its ID and secret."
                    : $"Tailscale answered {(int)response.StatusCode} when signing in.", response.StatusCode);
            }

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(Json, ct);
            if (string.IsNullOrEmpty(token?.AccessToken))
            {
                throw new TailscaleApiException("Tailscale didn't return an access token.");
            }

            // Tokens last an hour; renew a little early.
            var lifetime = TimeSpan.FromSeconds(Math.Max(60, (token.ExpiresIn ?? 3600) - 120));
            _token = (settings.ClientId, token.AccessToken, now + lifetime);
            _scope = token.Scope;
            return token.AccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string? AccessToken, [property: JsonPropertyName("expires_in")] int? ExpiresIn,
        [property: JsonPropertyName("scope")] string? Scope = null);

    private sealed record CreatedKey(string? Id, string? Key, DateTimeOffset? Expires);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created a one-use Tailscale key {KeyId} for an invite")]
    private partial void LogMinted(string keyId);
}
