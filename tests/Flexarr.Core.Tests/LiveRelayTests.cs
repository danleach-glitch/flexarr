using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Flexarr.Streaming;
using Flexarr.Streaming.Live;

namespace Flexarr.Core.Tests;

/// <summary>
/// A local HTTP server playing an Xtream provider: <c>/live/flexarr/s3cret/7.ts</c> sends <see cref="PacketsPerConnection"/>
/// MPEG-TS packets and then drops the connection, for the first <see cref="ConnectionsBeforeGone"/> connections; after
/// that, and for any other address, it answers 404.
/// </summary>
public sealed class FakeXtreamProvider : IAsyncDisposable
{
    public const string User = "flexarr";
    public const string Password = "s3cret";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serving;
    private int _connections;

    public FakeXtreamProvider()
    {
        _listener.Start();
        _serving = ServeAsync();
    }

    public int PacketsPerConnection { get; init; } = 10;

    public int ConnectionsBeforeGone { get; init; } = 2;

    /// <summary>Sends packets until told to stop, rather than dropping (for a channel that's stopped while it plays).</summary>
    public bool Endless { get; init; }

    public int Connections => Volatile.Read(ref _connections);

    public ConcurrentQueue<string> UserAgents { get; } = new();

    public string Root => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    public LiveSource Channel(int id = 7) => new($"{Root}/live/{User}/{Password}/{id}.ts", Password);

    /// <summary>Packet <paramref name="n"/> of a connection: a sync byte, then its number, so what arrives can be checked.</summary>
    public static byte[] PacketFor(int n)
    {
        var packet = new byte[188];
        packet[0] = 0x47;
        BitConverter.GetBytes(n).CopyTo(packet, 1);
        return packet;
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => AnswerAsync(client));
        }
    }

    private async Task AnswerAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var head = new StringBuilder();
            var buffer = new byte[4096];
            while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                {
                    return;
                }

                head.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            var lines = head.ToString().Split("\r\n");
            var path = lines[0].Split(' ')[1];
            if (lines.FirstOrDefault(l => l.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase)) is { } agent)
            {
                UserAgents.Enqueue(agent["User-Agent:".Length..].Trim());
            }

            var number = Interlocked.Increment(ref _connections);
            if (path != $"/live/{User}/{Password}/7.ts" || (!Endless && number > ConnectionsBeforeGone))
            {
                await stream.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), _stop.Token);
                return;
            }

            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: video/mp2t\r\nConnection: close\r\n\r\n"u8.ToArray(), _stop.Token);
            for (var i = 0; Endless || i < PacketsPerConnection; i++)
            {
                await stream.WriteAsync(PacketFor(i), _stop.Token);
                if (Endless)
                {
                    await Task.Delay(5, _stop.Token);
                }
            }

            // Then half a packet, and the connection drops, as a provider does mid-stream.
            await stream.WriteAsync(PacketFor(99).AsMemory(0, 90), _stop.Token);
            client.Client.LingerState = new LingerOption(true, 0);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _serving;
        _stop.Dispose();
    }
}

public sealed class LiveRelayTests
{
    private static LiveRelay Relay() => new() { FirstReconnectDelay = TimeSpan.FromMilliseconds(50), MaxReconnectDelay = TimeSpan.FromMilliseconds(200), MaxReconnects = 2 };

    private static HttpClient Ffmpeg()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Lavf/61.7.100");
        return client;
    }

    [Theory]
    [InlineData("http://dispatcharr:9191/live/flexarr/s3cret/7.ts", "s3cret", true)]
    [InlineData("http://dispatcharr:9191/live/flexarr/s3cret/7.ts", "", true)]
    [InlineData("http://me:pw@iptv.example/channel/1.ts", "", true)]
    [InlineData("http://iptv.example/stream/1.ts?token=abc", "", true)]
    [InlineData("http://iptv.example/get.php?username=a&password=b&type=ts", "", true)]
    [InlineData("http://iptv.example/stream.ts?Signature=abc&expires=1", "", true)]
    [InlineData("http://dispatcharr:9191/proxy/ts/stream/2f0c7a", "", false)]
    [InlineData("http://iptv.example/stream/1.ts?quality=hd", "", false)]
    public void Relays_only_addresses_that_carry_a_secret(string url, string secret, bool relayed) =>
        Assert.Equal(relayed, LiveRelay.NeedsRelay(new LiveSource(url, secret)));

    [Fact]
    public async Task The_address_ffmpeg_is_given_never_holds_the_password()
    {
        await using var relay = Relay();
        var source = new LiveSource("http://dispatcharr:9191/live/flexarr/s3cret/7.ts", "s3cret");
        using var lease = relay.Open(source);
        var settings = new TranscodeSettings { CacheDirectory = "/cache" };

        var live = LiveCommand.Hls(lease.Source, new LivePlan(true, false, 720, 4_000_000, true), "/out", settings, TimeSpan.FromHours(1), resume: false);
        var recording = LiveRecorder.RecordArguments(lease.Source, "/out/part001.ts", TimeSpan.FromMinutes(30));

        Assert.True(lease.Relayed);
        Assert.StartsWith("http://127.0.0.1:", lease.Source.Url, StringComparison.Ordinal);
        foreach (var arg in live.Concat(recording))
        {
            Assert.DoesNotContain("s3cret", arg, StringComparison.Ordinal);
            Assert.DoesNotContain("flexarr/", arg, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_source_without_a_secret_goes_straight_to_ffmpeg()
    {
        await using var relay = Relay();
        var source = new LiveSource("http://dispatcharr:9191/proxy/ts/stream/2f0c7a", "");
        using var lease = relay.Open(source);

        Assert.False(lease.Relayed);
        Assert.Same(source, lease.Source);
        Assert.Equal(0, relay.OpenLeases);
    }

    [Fact]
    public async Task Streams_the_provider_through_and_carries_on_across_a_drop()
    {
        await using var provider = new FakeXtreamProvider { PacketsPerConnection = 10, ConnectionsBeforeGone = 2 };
        await using var relay = Relay();
        using var lease = relay.Open(provider.Channel());
        using var ffmpeg = Ffmpeg();

        using var response = await ffmpeg.GetAsync(lease.Source.Url, HttpCompletionOption.ResponseHeadersRead);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        // Both connections' whole packets, none of the half packets each dropped on, then a clean end when the provider
        // was gone for good.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(20 * 188, bytes.Length);
        Assert.Equal(FakeXtreamProvider.PacketFor(9), bytes.AsSpan(9 * 188, 188).ToArray());
        Assert.Equal(FakeXtreamProvider.PacketFor(0), bytes.AsSpan(10 * 188, 188).ToArray());
        Assert.True(provider.Connections >= 3);
        // The provider sees what ffmpeg sent, as it did when ffmpeg read it itself.
        Assert.All(provider.UserAgents, agent => Assert.Equal("Lavf/61.7.100", agent));
    }

    [Fact]
    public async Task Refuses_a_reused_or_wrong_token()
    {
        await using var provider = new FakeXtreamProvider { Endless = true };
        await using var relay = Relay();
        using var lease = relay.Open(provider.Channel());
        using var ffmpeg = Ffmpeg();

        using var first = await ffmpeg.GetAsync(lease.Source.Url, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var again = await ffmpeg.GetAsync(lease.Source.Url, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);

        var port = new Uri(lease.Source.Url).Port;
        using var wrong = await ffmpeg.GetAsync($"http://127.0.0.1:{port}/relay/{new string('0', 64)}.ts");
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        using var nonsense = await ffmpeg.GetAsync($"http://127.0.0.1:{port}/live/{FakeXtreamProvider.User}/{FakeXtreamProvider.Password}/7.ts");
        Assert.Equal(HttpStatusCode.Forbidden, nonsense.StatusCode);
    }

    [Fact]
    public async Task Stops_with_the_channel()
    {
        await using var provider = new FakeXtreamProvider { Endless = true };
        await using var relay = Relay();
        var lease = relay.Open(provider.Channel());
        var url = lease.Source.Url;
        using var ffmpeg = Ffmpeg();

        using var response = await ffmpeg.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[188];
        await body.ReadExactlyAsync(buffer);

        lease.Dispose();
        using var reading = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var rest = 0L;
        try
        {
            while (await body.ReadAsync(new byte[65536], reading.Token) is var read and > 0)
            {
                rest += read;
            }
        }
        catch (IOException)
        {
            // Cut off: just as good.
        }

        Assert.False(reading.IsCancellationRequested, "the relay kept streaming after the channel stopped");
        Assert.Equal(0, relay.OpenLeases);
        using var after = await ffmpeg.GetAsync(url);
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
    }

    [Fact]
    public async Task Passes_on_the_providers_refusal()
    {
        await using var provider = new FakeXtreamProvider();
        await using var relay = Relay();
        using var lease = relay.Open(provider.Channel(id: 8));
        using var ffmpeg = Ffmpeg();

        using var response = await ffmpeg.GetAsync(lease.Source.Url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("doesn’t have this channel", LiveStreamException.FromError(lease.UpstreamError!).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keeps_why_when_the_provider_cant_be_reached()
    {
        // A port nothing listens on.
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        await using var relay = Relay();
        using var lease = relay.Open(new LiveSource($"http://127.0.0.1:{port}/live/a/b/7.ts", "b"));
        using var ffmpeg = Ffmpeg();

        // ffmpeg gets nothing it can play: no stream at all, or an empty one.
        try
        {
            using var response = await ffmpeg.GetAsync(lease.Source.Url);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }
        catch (HttpRequestException)
        {
        }

        Assert.Contains("refused", lease.UpstreamError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("can’t reach", LiveStreamException.FromError(lease.UpstreamError!).Message, StringComparison.Ordinal);
    }
}
