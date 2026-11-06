using Flexarr.Api.Playback;

namespace Flexarr.Api.Tests;

/// <summary>When finding intros and credits waits for people watching, before each episode it analyses.</summary>
public class MarkerDetectionWorkerTests
{
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithNobodyWatching_ItDoesntWait(bool asked)
    {
        Assert.Equal(0, await MarkerDetectionWorker.GiveWayAsync(() => false, () => false, asked, Pause, most: 30, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhileSomeoneWatchesAFileAsItIs_ItPausesOnceAnEpisode(bool asked)
    {
        Assert.Equal(1, await MarkerDetectionWorker.GiveWayAsync(() => false, () => true, asked, Pause, most: 30, CancellationToken.None));
    }

    [Fact]
    public async Task WhileAStreamIsBeingConverted_TheBackgroundSweepWaitsUntilItsOver()
    {
        var checks = 0;

        var pauses = await MarkerDetectionWorker.GiveWayAsync(() => ++checks <= 4, () => true, asked: false, Pause, most: 30, CancellationToken.None);

        Assert.Equal(4, pauses);
        Assert.Equal(5, checks);
    }

    [Fact]
    public async Task OnAServerThatsAlwaysConverting_TheSweepStillGetsOnWithIt_AfterItsLongestWait()
    {
        Assert.Equal(30, await MarkerDetectionWorker.GiveWayAsync(() => true, () => true, asked: false, Pause, most: 30, CancellationToken.None));
    }

    [Fact]
    public async Task ASeasonSomeoneJustStartedWatching_OnlyWaitsOnePauseAnEpisode_EvenWhileItsBeingConverted()
    {
        Assert.Equal(1, await MarkerDetectionWorker.GiveWayAsync(() => true, () => true, asked: true, Pause, most: 30, CancellationToken.None));
    }

    [Fact]
    public async Task ShuttingDown_EndsTheWait()
    {
        using var stopping = new CancellationTokenSource();
        var waiting = MarkerDetectionWorker.GiveWayAsync(() => true, () => true, asked: false, TimeSpan.FromMinutes(1), most: 30, stopping.Token);

        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}
