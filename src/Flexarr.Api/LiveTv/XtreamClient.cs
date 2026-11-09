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

/// <summary>The source failed in a way with words of its own, said in the language of whoever's asking when they read <see cref="Message"/>.</summary>
/// <param name="detail">What the words quote: the connection's own message, or the answer's status code.</param>
public sealed class XtreamException(LiveTvRefreshProblem problem, string? detail = null, Exception? inner = null) : Exception(null, inner)
{
    public LiveTvRefreshProblem Problem { get; } = problem;
    public string? Detail { get; } = detail;
    public override string Message => LiveTvEndpoints.RefreshProblemText(Problem, Detail) ?? "";
}

/// <summary>
/// Reads channels and the guide from an Xtream Codes API. Every address carries the password, so failures are
/// reported in words and the HTTP client's own logging is off (see Program.cs).
/// </summary>
public sealed class XtreamClient(HttpClient http, IHttpClientFactory clients)
{
    /// <summary>
    /// The client logos are fetched with. It doesn't follow redirects itself: <see cref="GetLogoAsync"/> follows only
    /// those that stay on the source or the community collection.
    /// </summary>
    public const string LogoClientName = "XtreamLogos";

    private const int MaxLogoRedirects = 5;

    /// <summary>Most a channel logo may weigh.</summary>
    private const int MaxLogoBytes = 2 * 1024 * 1024;

    public async Task<XtreamAccountInfo> CheckAsync(XtreamAccount account, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        using var doc = await GetJsonAsync(account.Api(), ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("user_info", out var user))
        {
            throw new XtreamException(LiveTvRefreshProblem.NotDispatcharr);
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
            throw new XtreamException(LiveTvRefreshProblem.NoChannelList);
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

    /// <summary>How long the guide may go without a byte arriving before the download is given up.</summary>
    internal TimeSpan GuideIdleTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The source's XMLTV guide, as it downloads. The caller disposes it. The client's timeout only covers waiting for
    /// the answer to start, so a download that goes quiet part-way is given up after <see cref="GuideIdleTimeout"/>.
    /// </summary>
    public async Task<Stream> OpenGuideAsync(XtreamAccount account, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        var response = await SendAsync(account.Guide(), HttpCompletionOption.ResponseHeadersRead, ct);
        return new IdleTimeoutStream(await response.Content.ReadAsStreamAsync(ct), GuideIdleTimeout);
    }

    /// <summary>A channel logo from the source itself or the community collection, as bytes and type; null when it isn't one.</summary>
    public async Task<(byte[] Bytes, string ContentType)?> GetLogoAsync(XtreamAccount account, Uri logo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(logo);
        // The source's own logos, or the community collection's; never anywhere else a channel list might point, or a
        // redirect from the source sends it.
        var client = clients.CreateClient(LogoClientName);
        var url = logo;
        HttpResponseMessage? response = null;
        for (var hop = 0; ; hop++)
        {
            if (hop > MaxLogoRedirects || !account.Serves(url) && !Core.LiveTv.CommunityLogos.IsCommunity(url))
            {
                response?.Dispose();
                return null;
            }

            response?.Dispose();
            response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }

            break;
        }

        using var _ = response;
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
            throw new XtreamException(LiveTvRefreshProblem.NotDispatcharr, inner: ex);
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
            throw new XtreamException(LiveTvRefreshProblem.Unreachable, ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new XtreamException(LiveTvRefreshProblem.TimedOut, inner: ex);
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            response.Dispose();
            throw new XtreamException(LiveTvRefreshProblem.Refused);
        }

        if (!response.IsSuccessStatusCode)
        {
            var code = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            response.Dispose();
            throw new XtreamException(LiveTvRefreshProblem.AnsweredError, code);
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

/// <summary>A download that fails with <see cref="XtreamException"/> when a read waits longer than the limit for data.</summary>
internal sealed class IdleTimeoutStream(Stream inner, TimeSpan idle) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(idle);
        try
        {
            return await inner.ReadAsync(buffer, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new XtreamException(LiveTvRefreshProblem.StoppedPartway, inner: ex);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
