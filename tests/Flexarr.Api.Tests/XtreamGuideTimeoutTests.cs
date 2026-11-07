using System.Net;
using System.Text;
using Flexarr.Api.LiveTv;
using Flexarr.Core.LiveTv;

namespace Flexarr.Api.Tests;

/// <summary>A guide download that goes quiet part-way is given up, rather than holding Live TV's refresh for ever.</summary>
public class XtreamGuideTimeoutTests
{
    private static readonly XtreamAccount Account = new(new Uri("http://dispatcharr:9191"), "flexarr", "secret");

    [Fact]
    public async Task A_guide_that_stops_arriving_part_way_fails_in_words()
    {
        using var http = new HttpClient(new Handler(stallAfterFirstChunk: true));
        var client = new XtreamClient(http) { GuideIdleTimeout = TimeSpan.FromMilliseconds(200) };

        await using var guide = await client.OpenGuideAsync(Account, CancellationToken.None);
        var read = Task.Run(async () =>
        {
            await foreach (var _ in XmltvParser.ProgrammesAsync(guide))
            {
            }
        });

        var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(read, finished);
        var ex = await Assert.ThrowsAsync<XtreamException>(() => read);
        Assert.DoesNotContain("secret", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_guide_that_keeps_arriving_is_read_whole()
    {
        using var http = new HttpClient(new Handler(stallAfterFirstChunk: false));
        var client = new XtreamClient(http) { GuideIdleTimeout = TimeSpan.FromSeconds(10) };

        await using var guide = await client.OpenGuideAsync(Account, CancellationToken.None);
        var titles = new List<string>();
        await foreach (var p in XmltvParser.ProgrammesAsync(guide))
        {
            titles.Add(p.Title);
        }

        Assert.Equal(["The News", "Doctor Who"], titles);
    }

    private sealed class Handler(bool stallAfterFirstChunk) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Chunks(stallAfterFirstChunk)) });
    }

    /// <summary>The guide in two halves; with a stall, the second never comes (until the reader gives up).</summary>
    private sealed class Chunks(bool stall) : Stream
    {
        private readonly Queue<byte[]> _chunks = new([
            Encoding.UTF8.GetBytes("""<?xml version="1.0" encoding="UTF-8"?><tv><programme start="20260101000000 +0000" stop="20260101010000 +0000" channel="1"><title>The News</title></programme>"""),
            Encoding.UTF8.GetBytes("""<programme start="20260101010000 +0000" stop="20260101020000 +0000" channel="1"><title>Doctor Who</title></programme></tv>"""),
        ]);

        private bool _first = true;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_first && stall)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            _first = false;
            if (!_chunks.TryDequeue(out var chunk))
            {
                return 0;
            }

            chunk.CopyTo(buffer);
            return chunk.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
