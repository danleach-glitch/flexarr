using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Flexarr.Streaming.Live;

namespace Flexarr.Api.LiveTv;

/// <summary>A sign-in to a server with an Xtream Codes API: Dispatcharr, for Flexarr.</summary>
public sealed record XtreamAccount(Uri BaseUrl, string Username, string Password)
{
    private string Root => BaseUrl.ToString().TrimEnd('/');

    /// <summary>A channel's stream. It carries the password in its path (that's the API), so it's never logged or sent to a client.</summary>
    public LiveSource StreamOf(int sourceId) =>
        new($"{Root}/live/{Uri.EscapeDataString(Username)}/{Uri.EscapeDataString(Password)}/{sourceId.ToString(CultureInfo.InvariantCulture)}.ts", Password);

    internal Uri Api(string? action = null) => new(
        $"{Root}/player_api.php?username={Uri.EscapeDataString(Username)}&password={Uri.EscapeDataString(Password)}" + (action is null ? "" : $"&action={action}"));

    internal Uri Guide() => new($"{Root}/xmltv.php?username={Uri.EscapeDataString(Username)}&password={Uri.EscapeDataString(Password)}");

    /// <summary>The same server, so a logo link from it can't send Flexarr anywhere else.</summary>
    public bool Serves(Uri url) => url.Scheme == BaseUrl.Scheme && string.Equals(url.Host, BaseUrl.Host, StringComparison.OrdinalIgnoreCase) && url.Port == BaseUrl.Port;

    /// <summary>Kept out of logs and exceptions: the password is in every address.</summary>
    public override string ToString() => $"{Root} as {Username}";
}

/// <summary>A channel in the source's list.</summary>
public sealed record XtreamChannel(int StreamId, double Number, string Name, string? Group, string? LogoUrl, string? GuideId);

/// <summary>What the source says about the sign-in.</summary>
public sealed record XtreamAccountInfo(bool SignedIn, string? Status, int? MaxConnections, DateTime? ExpiresAt);

public sealed class XtreamException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads channels and the guide from an Xtream Codes API. Every address carries the password, so failures are
/// reported in words and the HTTP client's own logging is off (see Program.cs).
/// </summary>
public sealed class XtreamClient(HttpClient http)
{
    /// <summary>Most a channel logo may weigh.</summary>
    private const int MaxLogoBytes = 2 * 1024 * 1024;

    public async Task<XtreamAccountInfo> CheckAsync(XtreamAccount account, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        using var doc = await GetJsonAsync(account.Api(), ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("user_info", out var user))
        {
            throw new XtreamException("That address answered, but not as Dispatcharr (no Xtream Codes API there).");
        }

        var signedIn = Int(user, "auth") == 1;
        var expires = Int(user, "exp_date") is { } seconds and > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : (DateTime?)null;
        return new XtreamAccountInfo(signedIn, Text(user, "status"), (int?)Int(user, "max_connections"), expires);
    }

    public async Task<IReadOnlyList<XtreamChannel>> GetChannelsAsync(XtreamAccount account, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var categories = await GetJsonAsync(account.Api("get_live_categories"), ct))
        {
            if (categories.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in categories.RootElement.EnumerateArray())
                {
                    if (Text(c, "category_id") is { } id && Text(c, "category_name") is { } name)
                    {
                        groups[id] = name;
                    }
                }
            }
        }

        using var streams = await GetJsonAsync(account.Api("get_live_streams"), ct);
        if (streams.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new XtreamException("The Live TV source didn't send a channel list. Check the user name and password.");
        }

        var channels = new List<XtreamChannel>();
        foreach (var s in streams.RootElement.EnumerateArray())
        {
            if (Int(s, "stream_id") is not { } id || Text(s, "name") is not { Length: > 0 } name)
            {
                continue;
            }

            var group = Text(s, "category_id") is { } category && groups.TryGetValue(category, out var g) ? g : null;
            channels.Add(new XtreamChannel((int)id, Double(s, "num") ?? id, name.Trim(), group, Text(s, "stream_icon") is { Length: > 0 } logo ? logo : null,
                Text(s, "epg_channel_id") is { Length: > 0 } guide ? guide : null));
        }

        return channels;
    }

    /// <summary>The source's XMLTV guide, as it downloads. The caller disposes it.</summary>
    public async Task<Stream> OpenGuideAsync(XtreamAccount account, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        var response = await SendAsync(account.Guide(), HttpCompletionOption.ResponseHeadersRead, ct);
        return await response.Content.ReadAsStreamAsync(ct);
    }

    /// <summary>A channel logo from the source itself (never another server), as bytes and type; null when it isn't one.</summary>
    public async Task<(byte[] Bytes, string ContentType)?> GetLogoAsync(XtreamAccount account, Uri logo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(logo);
        if (!account.Serves(logo))
        {
            return null;
        }

        using var response = await http.GetAsync(logo, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType is not { MediaType: { } type } || !type.StartsWith("image/", StringComparison.Ordinal)
            || type.Contains("svg", StringComparison.Ordinal) || response.Content.Headers.ContentLength > MaxLogoBytes)
        {
            return null;
        }

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
        {
            if (copy.Length + read > MaxLogoBytes)
            {
                return null;
            }

            copy.Write(buffer, 0, read);
        }

        return (copy.ToArray(), type);
    }

    private async Task<JsonDocument> GetJsonAsync(Uri url, CancellationToken ct)
    {
        using var response = await SendAsync(url, HttpCompletionOption.ResponseContentRead, ct);
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            throw new XtreamException("That address answered, but not as Dispatcharr (no Xtream Codes API there).", ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri url, HttpCompletionOption completion, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await http.SendAsync(request, completion, ct);
        }
        catch (HttpRequestException ex)
        {
            // The message names the host and port at most, never the address with its password.
            throw new XtreamException($"Flexarr can't reach the Live TV source: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new XtreamException("The Live TV source didn't answer in time.", ex);
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            response.Dispose();
            throw new XtreamException("The Live TV source refused the user name or password.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            response.Dispose();
            throw new XtreamException($"The Live TV source answered with an error ({code}).");
        }

        return response;
    }

    // The API is loose with types: numbers come as numbers or as strings, depending on the server.
    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var p)
        ? p.ValueKind switch { JsonValueKind.String => p.GetString(), JsonValueKind.Number => p.GetRawText(), _ => null }
        : null;

    private static long? Int(JsonElement e, string name) =>
        long.TryParse(Text(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static double? Double(JsonElement e, string name) =>
        double.TryParse(Text(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
}
