using System.Net;
using Flexarr.Api.Security;

namespace Flexarr.Api.Tests;

/// <summary>Who owns a tailnet address: one shared lookup, and a request that gives up doesn't spoil it for the rest.</summary>
public class WhoisCacheTests
{
    private static readonly IPAddress Address = IPAddress.Parse("100.70.1.2");

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task RequestsAtOnce_ShareOneLookup_AndOneGivingUpDoesntSpoilIt()
    {
        var calls = 0;
        var answer = new TaskCompletionSource<WhoisAnswer>();
        var cache = new WhoisCache(_ => { Interlocked.Increment(ref calls); return answer.Task; }, new ManualClock());
        using var gaveUp = new CancellationTokenSource();

        var abandoned = cache.LoginAsync(Address, gaveUp.Token);
        var waiting = Enumerable.Range(0, 20).Select(_ => cache.LoginAsync(Address, CancellationToken.None)).ToList();
        await gaveUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        answer.SetResult(new WhoisAnswer("sam@example.com", Definite: true));

        Assert.All(await Task.WhenAll(waiting), login => Assert.Equal("sam@example.com", login));
        Assert.Equal("sam@example.com", await cache.LoginAsync(Address, CancellationToken.None)); // remembered
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ALookupThatTimedOut_IsntRemembered()
    {
        var calls = 0;
        var cache = new WhoisCache(_ => Task.FromResult(++calls == 1 ? new WhoisAnswer(null, Definite: false) : new WhoisAnswer("sam@example.com", Definite: true)),
            new ManualClock());

        var first = await cache.LoginAsync(Address, CancellationToken.None);
        await Task.Delay(50); // let the first lookup settle
        var second = await cache.LoginAsync(Address, CancellationToken.None);

        Assert.Null(first);
        Assert.Equal("sam@example.com", second);
    }

    [Fact]
    public async Task Nobody_IsOnlyRememberedBriefly()
    {
        var clock = new ManualClock();
        var calls = 0;
        var cache = new WhoisCache(_ => Task.FromResult(++calls == 1 ? new WhoisAnswer(null, Definite: true) : new WhoisAnswer("sam@example.com", Definite: true)), clock);

        await cache.LoginAsync(Address, CancellationToken.None);
        await Task.Delay(50);
        var soon = await cache.LoginAsync(Address, CancellationToken.None);
        clock.Now += WhoisCache.UnknownLifetime + TimeSpan.FromSeconds(1);
        var later = await cache.LoginAsync(Address, CancellationToken.None);

        Assert.Null(soon);
        Assert.Equal("sam@example.com", later);
    }
}
