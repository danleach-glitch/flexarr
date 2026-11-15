using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Streaming.Live;

/// <summary>
/// Keeps a Live TV sign-in out of ffmpeg's command line, where any program on the machine can read it. A channel
/// whose address carries a secret (an Xtream account always does: the password is in its path) is fetched by Flexarr
/// itself, and ffmpeg or ffprobe is given a one-time address on 127.0.0.1 instead, which streams the bytes through.
/// <para>
/// Each run of ffmpeg or ffprobe gets its own <see cref="LiveRelayLease"/>, with a random token that works for one
/// connection and ends when the lease is disposed (the channel stops, or that run of ffmpeg is replaced). When the
/// provider drops, the relay connects again itself (a few times, waiting a little longer each time, as ffmpeg's own
/// <c>-reconnect</c> would), so ffmpeg reads on without noticing; when it gives up, it ends the response cleanly and
/// ffmpeg ends as it would have, so the channel's own restart (with a new lease) takes over. A provider's refusal
/// (401, 403, 404, 503…) is passed on as it came, so ffmpeg reports it in the same words.
/// </para>
/// Sources without a secret go straight to ffmpeg, as before. The listener is inside the server's own process, so it
/// also works in the Mac app's sandbox.
/// </summary>
public sealed partial class LiveRelay : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, LiveRelayLease> _leases = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private TcpListener? _listener;
    private Task? _accepting;

    /// <param name="upstream">How the provider is reached (tests stand in their own).</param>
    public LiveRelay(ILogger<LiveRelay>? logger = null, HttpMessageHandler? upstream = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _http = new HttpClient(upstream ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.None,
        }, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>How long the provider may send nothing before the connection counts as dropped (ffmpeg's <c>-rw_timeout</c>).</summary>
    internal TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Tries at connecting again, in a row, before the relay gives up and ends the response.</summary>
    internal int MaxReconnects { get; init; } = 5;

    /// <summary>The longest wait between tries (ffmpeg's <c>-reconnect_delay_max</c>).</summary>
    internal TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The first wait between tries; each one after waits twice as long, up to <see cref="MaxReconnectDelay"/>.</summary>
    internal TimeSpan FirstReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Leases not yet disposed (for tests).</summary>
    internal int OpenLeases => _leases.Count;

    /// <summary>
    /// Whether <paramref name="source"/> carries a secret that mustn't go on a command line: a password the source
    /// names (an Xtream account), a user name and password in the address (<c>http://user:pass@host</c>), an Xtream
    /// stream path (<c>/live/user/pass/1.ts</c>), or a query parameter that looks like one (password, token, key…).
    /// </summary>
    public static bool NeedsRelay(LiveSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Secret.Length > 0)
        {
            return true;
        }

        if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            return false;
        }

        return url.UserInfo.Length > 0 || XtreamPath().IsMatch(url.AbsolutePath)
            || url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => Uri.UnescapeDataString(p.Split('=', 2)[0]))
                .Any(name => SecretName().IsMatch(name));
    }

    /// <summary>
    /// The address one run of ffmpeg or ffprobe should read <paramref name="source"/> from: a one-time address on this
    /// machine when it carries a secret, else the source itself. Dispose the lease when that run is over.
    /// </summary>
    public LiveRelayLease Open(LiveSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!NeedsRelay(source))
        {
            return new LiveRelayLease(this, source, source, token: null);
        }

        var port = EnsureListening();
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var extension = Uri.TryCreate(source.Url, UriKind.Absolute, out var url) && Path.GetExtension(url.AbsolutePath) is { Length: > 1 and <= 6 } ext
            && ext[1..].All(char.IsAsciiLetterOrDigit) ? ext : ".ts";
        var relayed = new LiveSource($"http://127.0.0.1:{port}/relay/{token}{extension}", source.Secret);
        var lease = new LiveRelayLease(this, source, relayed, token);
        _leases[token] = lease;
        return lease;
    }

    internal void Forget(LiveRelayLease lease)
    {
        if (lease.Token is { } token)
        {
            _leases.TryRemove(new KeyValuePair<string, LiveRelayLease>(token, lease));
        }
    }

    private int EnsureListening()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping.IsCancellationRequested, this);
            if (_listener is null)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                _listener = listener;
                _accepting = AcceptAsync(listener, _stopping.Token);
            }

            return ((IPEndPoint)_listener.LocalEndpoint).Port;
        }
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken stopping)
    {
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        try
        {
            using var asking = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            asking.CancelAfter(TimeSpan.FromSeconds(10));
            var request = await ReadRequestAsync(stream, asking.Token).ConfigureAwait(false);
            if (request is null)
            {
                await RespondAsync(stream, 400, "Bad Request", stopping).ConfigureAwait(false);
                return;
            }

            // Only the token's own address, once: a token used already, or one never given out, is refused.
            var match = RelayPath().Match(request.Value.Path);
            if (!match.Success || !_leases.TryGetValue(match.Groups[1].Value, out var lease) || !lease.Claim())
            {
                LogRefused(_logger);
                await RespondAsync(stream, 403, "Forbidden", stopping).ConfigureAwait(false);
                return;
            }

            if (request.Value.Method is not ("GET" or "HEAD"))
            {
                await RespondAsync(stream, 405, "Method Not Allowed", stopping).ConfigureAwait(false);
                return;
            }

            using var ending = CancellationTokenSource.CreateLinkedTokenSource(stopping, lease.Ended);
            await RelayAsync(lease, request.Value, stream, ending.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // ffmpeg went, or the channel stopped.
        }
    }

    private async Task RelayAsync(LiveRelayLease lease, Request request, NetworkStream client, CancellationToken ct)
    {
        var sentHeaders = false;
        var failures = 0;
        // MPEG-TS goes in 188-byte packets: only whole ones are passed on, so a connection that drops mid-packet and
        // comes back doesn't leave ffmpeg half a packet glued to the next.
        const int Packet = 188;
        var buffer = new byte[Packet * 348];
        var held = 0;
        while (!ct.IsCancellationRequested)
        {
            HttpResponseMessage? response = null;
            try
            {
                using var ask = new HttpRequestMessage(HttpMethod.Get, lease.Upstream.Url);
                foreach (var name in (string[])["User-Agent", "Accept"])
                {
                    if (request.Headers.TryGetValue(name, out var value))
                    {
                        ask.Headers.TryAddWithoutValidation(name, value);
                    }
                }

                using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connecting.CancelAfter(ReadTimeout);
                    response = await _http.SendAsync(ask, HttpCompletionOption.ResponseHeadersRead, connecting.Token).ConfigureAwait(false);
                }

                if (!response.IsSuccessStatusCode)
                {
                    var code = (int)response.StatusCode;
                    LogUpstreamRefused(_logger, code);
                    if (!sentHeaders)
                    {
                        // Passed on as it came, so ffmpeg says the same as it would have reading the provider itself.
                        lease.UpstreamError = $"Server returned {code} {response.ReasonPhrase}";
                        await RespondAsync(client, code, response.ReasonPhrase ?? "Error", ct).ConfigureAwait(false);
                        return;
                    }

                    if (!await WaitToReconnectAsync(++failures, ct).ConfigureAwait(false))
                    {
                        return;
                    }

                    continue;
                }

                if (!sentHeaders)
                {
                    var type = response.Content.Headers.ContentType?.ToString() ?? "video/mp2t";
                    var head = $"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n";
                    await ToFfmpegAsync(client, Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
                    sentHeaders = true;
                    if (request.Method == "HEAD")
                    {
                        return;
                    }
                }

                await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                held = 0;
                while (true)
                {
                    int read;
                    using (var reading = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        reading.CancelAfter(ReadTimeout);
                        read = await body.ReadAsync(buffer.AsMemory(held), reading.Token).ConfigureAwait(false);
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    failures = 0;
                    held += read;
                    var whole = held - held % Packet;
                    if (whole > 0)
                    {
                        await ToFfmpegAsync(client, buffer.AsMemory(0, whole), ct).ConfigureAwait(false);
                        lease.AddRelayed(whole);
                        Buffer.BlockCopy(buffer, whole, buffer, 0, held - whole);
                        held -= whole;
                    }
                }

                LogUpstreamEnded(_logger);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && ex is not FfmpegGone && !ct.IsCancellationRequested)
            {
                // Couldn't reach the provider, or it stopped sending. (ffmpeg going away is FfmpegGone, which ends the relay.)
                LogUpstreamDropped(_logger, ex.GetType().Name);
                if (!sentHeaders && failures + 1 > InitialTries)
                {
                    // Nothing sent yet: ffmpeg gets nothing, and the reason is kept for the channel to give.
                    lease.UpstreamError = ex is HttpRequestException { InnerException: SocketException socket } ? socket.Message
                        : ex is OperationCanceledException ? "Connection timed out" : "Input/output error";
                    return;
                }
            }
            finally
            {
                response?.Dispose();
            }

            if (!await WaitToReconnectAsync(++failures, ct).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>Tries at the first connection, before ffmpeg is told nothing came (it gives up waiting itself soon after).</summary>
    private const int InitialTries = 2;

    /// <summary>ffmpeg (or ffprobe) closed its end: nothing more to relay.</summary>
    private sealed class FfmpegGone(Exception inner) : IOException("ffmpeg went", inner);

    private static async Task ToFfmpegAsync(NetworkStream client, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        try
        {
            await client.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            throw new FfmpegGone(ex);
        }
    }

    /// <summary>Waits before the next try; false when it's tried enough (the response then ends, and so does ffmpeg's read).</summary>
    private async Task<bool> WaitToReconnectAsync(int failures, CancellationToken ct)
    {
        if (failures > MaxReconnects)
        {
            LogGaveUp(_logger, MaxReconnects);
            return false;
        }

        var delay = TimeSpan.FromTicks(Math.Min(MaxReconnectDelay.Ticks, FirstReconnectDelay.Ticks * (1L << Math.Min(failures - 1, 20))));
        await Task.Delay(delay, ct).ConfigureAwait(false);
        return true;
    }

    private static async Task RespondAsync(NetworkStream stream, int code, string reason, CancellationToken ct)
    {
        var text = $"HTTP/1.1 {code} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), ct).ConfigureAwait(false);
    }

    private readonly record struct Request(string Method, string Path, Dictionary<string, string> Headers);

    /// <summary>The request line and headers, up to 16 KB; null when it isn't HTTP.</summary>
    private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var bytes = new byte[16 * 1024];
        var length = 0;
        while (true)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            length += read;
            var end = bytes.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
            if (end >= 0)
            {
                var lines = Encoding.ASCII.GetString(bytes, 0, end).Split("\r\n");
                var parts = lines[0].Split(' ');
                if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
                {
                    return null;
                }

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    var colon = line.IndexOf(':', StringComparison.Ordinal);
                    if (colon > 0)
                    {
                        headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                    }
                }

                return new Request(parts[0], parts[1], headers);
            }

            if (length == bytes.Length)
            {
                return null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        TcpListener? listener;
        Task? accepting;
        lock (_gate)
        {
            if (_stopping.IsCancellationRequested)
            {
                return;
            }

            _stopping.Cancel();
            (listener, accepting) = (_listener, _accepting);
        }

        listener?.Stop();
        if (accepting is not null)
        {
            await accepting.ConfigureAwait(false);
        }

        foreach (var lease in _leases.Values)
        {
            lease.Dispose();
        }

        _http.Dispose();
        _stopping.Dispose();
    }

    /// <summary><c>/live/user/pass/123.ts</c> and the like: Xtream's stream addresses, which hold the password.</summary>
    [GeneratedRegex(@"^/(?:live|movie|series|timeshift)/[^/]+/[^/]+/", RegexOptions.IgnoreCase)]
    private static partial Regex XtreamPath();

    /// <summary>Query parameters that sign in.</summary>
    [GeneratedRegex(@"^(?:pass|password|passwd|pwd|token|access_token|auth|key|apikey|api_key|api-key|secret|sig|signature|username|user)$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    [GeneratedRegex(@"^/relay/([0-9a-f]{64})(?:\.[A-Za-z0-9]{1,5})?$")]
    private static partial Regex RelayPath();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live TV relay: refused a request with a token that's used already or wasn't given out")]
    private static partial void LogRefused(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Live TV relay: the source answered {Code}")]
    private static partial void LogUpstreamRefused(ILogger logger, int code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Live TV relay: the source stopped sending; connecting again")]
    private static partial void LogUpstreamEnded(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Live TV relay: lost the source ({Reason}); connecting again")]
    private static partial void LogUpstreamDropped(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live TV relay: gave up on the source after {Tries} tries")]
    private static partial void LogGaveUp(ILogger logger, int tries);
}

/// <summary>
/// One run of ffmpeg or ffprobe's way to a channel: <see cref="Source"/> is the address to give it. Disposing it ends
/// the token, and any connection still open on it.
/// </summary>
public sealed class LiveRelayLease : IDisposable
{
    private readonly LiveRelay _relay;
    private readonly CancellationTokenSource _ended = new();
    private int _claimed;
    private long _relayed;
    private int _disposed;

    internal LiveRelayLease(LiveRelay relay, LiveSource upstream, LiveSource source, string? token)
    {
        _relay = relay;
        Upstream = upstream;
        Source = source;
        Token = token;
    }

    /// <summary>The address to give ffmpeg: on 127.0.0.1 when relayed, else the channel's own.</summary>
    public LiveSource Source { get; }

    /// <summary>Whether the channel goes through the relay (its address carries a secret).</summary>
    public bool Relayed => Token is not null;

    /// <summary>
    /// Why the provider couldn't be read before anything was sent (ffmpeg then sees only the relay's side): in ffmpeg's
    /// own words, such as "Connection refused", for <see cref="LiveStreamException.Explain"/>.
    /// </summary>
    public string? UpstreamError { get; internal set; }

    /// <summary>Bytes passed on to ffmpeg so far.</summary>
    public long BytesRelayed => Interlocked.Read(ref _relayed);

    internal LiveSource Upstream { get; }

    internal string? Token { get; }

    internal CancellationToken Ended => _ended.Token;

    /// <summary>Takes the token's one connection; false when it's been taken, or the lease has ended.</summary>
    internal bool Claim() => Volatile.Read(ref _disposed) == 0 && Interlocked.Exchange(ref _claimed, 1) == 0;

    internal void AddRelayed(long bytes) => Interlocked.Add(ref _relayed, bytes);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _relay.Forget(this);
        _ended.Cancel();
    }
}
