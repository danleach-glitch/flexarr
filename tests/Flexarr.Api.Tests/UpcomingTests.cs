using System.Net;
using Flexarr.Api.Endpoints;
using Flexarr.Api.Security;

namespace Flexarr.Api.Tests;

public class UpcomingTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    [Fact]
    public async Task AQueueFetchUnderWayWhenAWebhookArrives_IsNotCached()
    {
        var arr = new FakeArr();
        var sonarrQueue = 0;
        arr.OnRequest = request =>
        {
            if (request.RequestUri!.Host == "sonarr.test" && request.RequestUri.AbsolutePath == "/api/v3/queue" && Interlocked.Increment(ref sonarrQueue) == 1)
            {
                UpcomingEndpoints.ForgetArrCalls(); // A download finishes while Sonarr is answering.
            }
        };
        using var app = arr.Serve(factory);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/api/v1/downloads", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/api/v1/downloads", UriKind.Relative))).StatusCode);

        Assert.Equal(2, sonarrQueue); // Asked again: the first answer may predate the download.
    }

    [Theory]
    [InlineData("dans-mac-mini.tail1234.ts.net", "100.64.0.1")] // Browsers force HTTPS on .ts.net; Sonarr answers HTTP.
    [InlineData("192.168.1.20", "192.168.1.20")]
    [InlineData("flexarr.lan", "flexarr.lan")]
    public async Task ALinkIntoALocalhostArr_AvoidsTheTailscaleName(string host, string expected)
    {
        var linkHost = await UpcomingEndpoints.LinkHostAsync(host, new FakeTailnet(), CancellationToken.None);

        Assert.Equal(expected, linkHost);
    }

    [Fact]
    public async Task WithoutTailscaleAnswering_ALinkUsesTheShortMagicDnsName()
    {
        var linkHost = await UpcomingEndpoints.LinkHostAsync("dans-mac-mini.tail1234.ts.net", new FakeTailnet { Snapshot = null }, CancellationToken.None);

        Assert.Equal("dans-mac-mini", linkHost);
    }
}

[CollectionDefinition(nameof(ArrCallCacheTests), DisableParallelization = true)]
public sealed class ArrCallCacheCollection;

/// <summary>
/// The cache in front of Sonarr and Radarr. Run alone: a webhook anywhere in the process (<see cref="UpcomingEndpoints.ForgetArrCalls"/>)
/// drops what every server has kept.
/// </summary>
[Collection(nameof(ArrCallCacheTests))]
public sealed class ArrCallCacheTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task AnArrThatIsDown_IsAskedAgainInTheBackground_LessOftenEachTime_WithoutHoldingUpThePage()
    {
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var clock = new Clock();
        var calls = 0;
        var answer = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IReadOnlyList<int>> Call(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return calls == 1 ? Task.FromException<IReadOnlyList<int>>(new HttpRequestException("asleep")) : answer.Task;
        }

        Assert.Empty(await UpcomingEndpoints.CachedAsync(cache, "k", Ttl, Call, CancellationToken.None, clock)); // the first ask waits, and fails
        Assert.Empty(await UpcomingEndpoints.CachedAsync(cache, "k", Ttl, Call, CancellationToken.None, clock));
        Assert.Equal(1, calls); // left alone for 15 seconds

        clock.Now += TimeSpan.FromSeconds(16);
        // Asked again, but the page doesn't wait for it: the call below never answers until it's told to.
        var page = UpcomingEndpoints.CachedAsync(cache, "k", Ttl, Call, CancellationToken.None, clock);
        Assert.True(page.IsCompleted);
        Assert.Empty(await page);
        await WaitUntil(() => calls == 2);
        Assert.Empty(await UpcomingEndpoints.CachedAsync(cache, "k", Ttl, Call, CancellationToken.None, clock));
        Assert.Equal(2, calls); // one call at a time

        answer.SetResult([7]);
        IReadOnlyList<int> back = [];
        await WaitUntil(() => (back = UpcomingEndpoints.CachedAsync(cache, "k", Ttl, Call, CancellationToken.None, clock).Result).Count > 0);
        Assert.Equal([7], back);
    }

    [Fact]
    public async Task AnOutOfDateAnswer_IsShownWhileAFreshOneIsFetched()
    {
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var clock = new Clock();
        var calls = 0;
        var second = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IReadOnlyList<int>> Call(CancellationToken ct) =>
            Interlocked.Increment(ref calls) == 1 ? Task.FromResult<IReadOnlyList<int>>([1]) : second.Task;

        Assert.Equal([1], await UpcomingEndpoints.CachedAsync(cache, "k", Ttl, Call, CancellationToken.None, clock));
        Assert.Equal([1], await UpcomingEndpoints.CachedAsync(cache, "k", Ttl, Call, CancellationToken.None, clock));
        Assert.Equal(1, calls);

        clock.Now += Ttl + TimeSpan.FromSeconds(1);
        var page = UpcomingEndpoints.CachedAsync(cache, "k", Ttl, Call, CancellationToken.None, clock);
        Assert.True(page.IsCompleted); // not waiting on the slow Sonarr
        Assert.Equal([1], await page);
        await WaitUntil(() => calls == 2);
        Assert.Equal([1], await UpcomingEndpoints.CachedAsync(cache, "k", Ttl, Call, CancellationToken.None, clock));
        Assert.Equal(2, calls);

        second.SetResult([2]);
        IReadOnlyList<int> back = [];
        await WaitUntil(() => (back = UpcomingEndpoints.CachedAsync(cache, "k", Ttl, Call, CancellationToken.None, clock).Result)[0] == 2);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task PagesAskingTogether_ShareOneCall()
    {
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var calls = 0;
        var answer = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IReadOnlyList<int>> Call(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return answer.Task;
        }

        var pages = Enumerable.Range(0, 4).Select(_ => UpcomingEndpoints.CachedAsync(cache, "k", Ttl, Call, CancellationToken.None)).ToList();
        await WaitUntil(() => calls == 1);
        answer.SetResult([3]);
        foreach (var page in await Task.WhenAll(pages))
        {
            Assert.Equal([3], page);
        }

        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(1, 15)]
    [InlineData(2, 30)]
    [InlineData(3, 60)]
    [InlineData(5, 240)]
    [InlineData(6, 300)]
    [InlineData(40, 300)]
    public void AFailingArr_IsLeftLongerEachTime_UpToFiveMinutes(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), UpcomingEndpoints.Backoff(failures));
}
