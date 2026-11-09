using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Configuration;
using Flexarr.Api.Scanning;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Paths;
using Flexarr.Core.Scanning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Tests;

public class FolderWatcherTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private sealed class StaticOptions(FlexarrOptions value) : IOptionsMonitor<FlexarrOptions>
    {
        public FlexarrOptions CurrentValue => value;
        public FlexarrOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<FlexarrOptions, string?> listener) => null;
    }

    private sealed class Setup(FolderWatcher watcher, RecordingScanQueue queue, string root, FlexarrOptions options) : IDisposable
    {
        public FolderWatcher Watcher { get; } = watcher;
        public RecordingScanQueue Queue { get; } = queue;
        public string Root { get; } = root;
        public FlexarrOptions Options { get; } = options;

        public void Dispose() => Watcher.Dispose();
    }

    private async Task<Setup> WatchAsync(LibraryType type = LibraryType.Movies, string? name = null, bool create = true)
    {
        var root = Path.Combine(factory.MediaDirectory, $"watched-{Guid.NewGuid():N}");
        if (create)
        {
            Directory.CreateDirectory(root);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            db.Libraries.Add(new Library { Name = name ?? $"Watched {Guid.NewGuid():N}", Type = type, Folders = [new LibraryFolder { Path = root }] });
            await db.SaveChangesAsync();
        }

        var options = new FlexarrOptions();
        var queue = new RecordingScanQueue();
        var watcher = new FolderWatcher(queue, factory.Services.GetRequiredService<IServiceScopeFactory>(), new StaticOptions(options), TimeProvider.System,
            NullLogger<FolderWatcher>.Instance)
        {
            QuietFor = TimeSpan.FromMilliseconds(300),
            SettleFor = TimeSpan.FromMilliseconds(200),
            LookForMissingEvery = TimeSpan.Zero,
        };
        await watcher.SyncAsync(default);
        return new Setup(watcher, queue, root, options);
    }

    /// <summary>Flushes until a request for <paramref name="root"/>'s folder arrives, or a few seconds pass.</summary>
    private static async Task<List<LibraryScanRequest>> WaitForAsync(Setup setup, Func<LibraryScanRequest, bool> match, int seconds = 10)
    {
        for (var i = 0; i < seconds * 10; i++)
        {
            await setup.Watcher.FlushAsync(default);
            if (setup.Queue.Requests.Any(match))
            {
                break;
            }

            await Task.Delay(100);
        }

        return setup.Queue.Requests.Where(r => r.Path.StartsWith(setup.Root, StringComparison.Ordinal)).ToList();
    }

    [Fact]
    public async Task ANewFile_QueuesItsTitlesFolder_Once()
    {
        using var setup = await WatchAsync();
        Assert.Equal(FolderWatchState.Watching, setup.Watcher.StatusOf(setup.Root)?.State);
        Assert.True(setup.Watcher.KeepsUpWith(setup.Root));

        var title = Directory.CreateDirectory(Path.Combine(setup.Root, "Dune (2021)")).FullName;
        await File.WriteAllTextAsync(Path.Combine(title, "Dune (2021).mkv"), "x");
        await File.WriteAllTextAsync(Path.Combine(title, "Dune (2021).en.srt"), "1");

        var requests = await WaitForAsync(setup, r => r.Path == title);
        var request = Assert.Single(requests);
        Assert.Equal(title, request.Path);
        Assert.Equal(FolderWatcher.Reason, request.Reason);
        Assert.False(request.ForceMetadata);
    }

    [Fact]
    public async Task AFileLooseInTheLibraryFolder_QueuesItself()
    {
        using var setup = await WatchAsync();
        var film = Path.Combine(setup.Root, "Heat (1995).mkv");
        await File.WriteAllTextAsync(film, "x");

        var requests = await WaitForAsync(setup, r => r.Path == film);
        Assert.Equal(film, Assert.Single(requests).Path);
    }

    [Fact]
    public async Task HiddenAndUnfinishedFiles_QueueNothing()
    {
        using var setup = await WatchAsync();
        await File.WriteAllTextAsync(Path.Combine(setup.Root, ".DS_Store"), "x");
        await File.WriteAllTextAsync(Path.Combine(setup.Root, "Heat (1995).mkv.part"), "x");
        Directory.CreateDirectory(Path.Combine(setup.Root, "@eaDir"));

        var requests = await WaitForAsync(setup, _ => false, seconds: 3);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task AFileStillBeingWritten_WaitsUntilItStops()
    {
        using var setup = await WatchAsync();
        var title = Directory.CreateDirectory(Path.Combine(setup.Root, "Alien (1979)")).FullName;
        var film = Path.Combine(title, "Alien (1979).mkv");
        await using (var stream = new FileStream(film, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            for (var i = 0; i < 15; i++)
            {
                await stream.WriteAsync(new byte[4096]);
                await stream.FlushAsync();
                await setup.Watcher.FlushAsync(default);
                Assert.DoesNotContain(setup.Queue.Requests, r => r.Path == title);
                await Task.Delay(100);
            }
        }

        var requests = await WaitForAsync(setup, r => r.Path == title);
        Assert.Equal(title, Assert.Single(requests).Path);
    }

    [Fact]
    public async Task AReplacedCover_RefreshesTheTitlesDetails()
    {
        using var setup = await WatchAsync();
        var title = Directory.CreateDirectory(Path.Combine(setup.Root, "Up (2009)")).FullName;
        setup.Watcher.Changed(setup.Root, "Up (2009)/poster.jpg");

        var requests = await WaitForAsync(setup, r => r.Path == title);
        Assert.True(Assert.Single(requests).ForceMetadata);
    }

    [Fact]
    public async Task ACoverRewrittenOverAndOver_ReadsTheDetailsAfreshOnce()
    {
        using var setup = await WatchAsync();
        var title = Directory.CreateDirectory(Path.Combine(setup.Root, "Up (2009)")).FullName;
        setup.Watcher.Changed(setup.Root, "Up (2009)/poster.jpg");
        var first = await WaitForAsync(setup, r => r.Path == title);
        Assert.True(Assert.Single(first).ForceMetadata);

        setup.Watcher.Changed(setup.Root, "Up (2009)/poster.jpg");
        for (var i = 0; i < 100 && setup.Queue.Requests.Count(r => r.Path == title) < 2; i++)
        {
            await setup.Watcher.FlushAsync(default);
            await Task.Delay(50);
        }

        var both = setup.Queue.Requests.Where(r => r.Path == title).ToList();
        Assert.Equal(2, both.Count);
        Assert.False(both[1].ForceMetadata);
    }

    [Fact]
    public async Task OnlySubtitles_AreToldApart()
    {
        using var setup = await WatchAsync();
        var both = Directory.CreateDirectory(Path.Combine(setup.Root, "Both (2020)")).FullName;
        var subs = Directory.CreateDirectory(Path.Combine(setup.Root, "Subs (2020)")).FullName;
        setup.Watcher.Changed(setup.Root, "Subs (2020)/Subs (2020).en.srt");
        setup.Watcher.Changed(setup.Root, "Both (2020)/Both (2020).en.srt");
        setup.Watcher.Changed(setup.Root, "Both (2020)/Both (2020).mkv");

        var requests = await WaitForAsync(setup, r => r.Path == both);
        await WaitForAsync(setup, r => r.Path == subs);
        Assert.True(setup.Queue.Requests.Single(r => r.Path == subs).OnlySubtitles);
        Assert.False(setup.Queue.Requests.Single(r => r.Path == both).OnlySubtitles);
        Assert.NotNull(requests[0].LastChange);
    }

    [Fact]
    public async Task AFolderMovedIn_IsATitleWithDetailsToFind_ButASubtitleAddedToOneIsnt()
    {
        using var setup = await WatchAsync();
        var moved = Directory.CreateDirectory(Path.Combine(setup.Root, "Dune (2021)")).FullName;
        var subtitled = Directory.CreateDirectory(Path.Combine(setup.Root, "Heat (1995)")).FullName;
        setup.Watcher.Changed(setup.Root, "Dune (2021)"); // one event, for the folder itself
        setup.Watcher.Changed(setup.Root, "Heat (1995)"); // its change time, with the new subtitle
        setup.Watcher.Changed(setup.Root, "Heat (1995)/Heat (1995).en.srt");

        await WaitForAsync(setup, r => r.Path == moved);
        await WaitForAsync(setup, r => r.Path == subtitled);
        Assert.False(setup.Queue.Requests.Single(r => r.Path == moved).OnlySubtitles);
        Assert.True(setup.Queue.Requests.Single(r => r.Path == subtitled).OnlySubtitles);
    }

    [Fact]
    public async Task ATitleThatNeverGoesQuiet_IsScannedAnywayInTheEnd()
    {
        using var setup = await WatchAsync();
        setup.Watcher.WaitsAtMost = TimeSpan.FromSeconds(2);
        var title = Directory.CreateDirectory(Path.Combine(setup.Root, "Busy (2020)")).FullName;

        var stop = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < stop && !setup.Queue.Requests.Any(r => r.Path == title))
        {
            setup.Watcher.Changed(setup.Root, "Busy (2020)/Busy (2020).mkv");
            await setup.Watcher.FlushAsync(default);
            await Task.Delay(50);
        }

        Assert.Contains(setup.Queue.Requests, r => r.Path == title);
    }

    [Fact]
    public async Task TitlesDeleted_ScanTheWholeFolderOnce_SoTheMassRemovalGuardDecides()
    {
        using var setup = await WatchAsync();
        foreach (var name in new[] { "Heat (1995)", "Dune (2021)", "Alien (1979)" })
        {
            setup.Watcher.Changed(setup.Root, $"{name}/{name}.mkv"); // Deleted: nothing's there.
        }

        var requests = await WaitForAsync(setup, r => r.Path == setup.Root);
        Assert.Equal(setup.Root, Assert.Single(requests).Path);
        Assert.False(requests[0].ForceMetadata);
    }

    [Fact]
    public async Task OneTitleDeletedOrRenamed_ScansJustThatTitle()
    {
        using var setup = await WatchAsync();
        Directory.CreateDirectory(Path.Combine(setup.Root, "Still Here (2020)"));
        var gone = Path.Combine(setup.Root, "Heat (1995)");
        setup.Watcher.Changed(setup.Root, "Heat (1995)/Heat (1995).mkv");

        var requests = await WaitForAsync(setup, r => r.Path == gone);

        Assert.DoesNotContain(requests, r => r.Path == setup.Root);
    }

    [Fact]
    public async Task ALastTitleGone_LooksLikeAnUnmountedDrive_SoTheGuardDecides()
    {
        using var setup = await WatchAsync();
        await File.WriteAllTextAsync(Path.Combine(setup.Root, ".DS_Store"), "x"); // left on a mount point
        setup.Watcher.Changed(setup.Root, "Heat (1995)/Heat (1995).mkv");

        var requests = await WaitForAsync(setup, r => r.Path == setup.Root);

        Assert.DoesNotContain(requests, r => r.Path == Path.Combine(setup.Root, "Heat (1995)"));
    }

    [Fact]
    public async Task TitlesGoingOneAfterAnother_StillReachTheGuard()
    {
        using var setup = await WatchAsync();
        Directory.CreateDirectory(Path.Combine(setup.Root, "Still Here (2020)"));
        for (var i = 1; i < FolderWatcher.ManyTitlesGone; i++)
        {
            var gone = Path.Combine(setup.Root, $"Gone {i}");
            setup.Watcher.Changed(setup.Root, $"Gone {i}/Gone.mkv");
            await WaitForAsync(setup, r => r.Path == gone);
        }

        setup.Watcher.Changed(setup.Root, "Gone Last/Gone.mkv");

        var requests = await WaitForAsync(setup, r => r.Path == setup.Root);
        Assert.DoesNotContain(requests, r => r.Path == Path.Combine(setup.Root, "Gone Last"));
    }

    [Fact]
    public async Task AFileReplacedInATitle_StillScansJustTheTitle()
    {
        using var setup = await WatchAsync();
        var title = Directory.CreateDirectory(Path.Combine(setup.Root, "Heat (1995)")).FullName;
        await File.WriteAllTextAsync(Path.Combine(title, "Heat (1995) 4K.mkv"), "x");
        setup.Watcher.Changed(setup.Root, "Heat (1995)/Heat (1995) HD.mkv");

        var requests = await WaitForAsync(setup, r => r.Path == title);
        Assert.Equal(title, Assert.Single(requests).Path);
    }

    [Theory]
    [InlineData("\\")]
    [InlineData("\\\\\\")]
    [InlineData("/")]
    public async Task AnOddName_DoesNoHarm(string name)
    {
        using var setup = await WatchAsync();
        setup.Watcher.Changed(setup.Root, name);
        Assert.NotNull(setup.Watcher.StatusOf(setup.Root));
    }

    [Fact]
    public async Task ManyTitlesAtOnce_ScanTheWholeFolder()
    {
        using var setup = await WatchAsync();
        for (var i = 0; i <= FolderWatcher.MaxPendingPerFolder; i++)
        {
            setup.Watcher.Changed(setup.Root, $"Film {i}/Film {i}.mkv");
        }

        setup.Watcher.Changed(setup.Root, "Film 0/poster.jpg");

        var requests = await WaitForAsync(setup, r => r.Path == setup.Root);
        var request = Assert.Single(requests);
        Assert.Equal(setup.Root, request.Path);
        // A scan of the whole folder looks at every title's artwork anyway; forcing would look every title up again.
        Assert.False(request.ForceMetadata);
    }

    [Fact]
    public async Task AMissingFolder_IsWatchedAndScannedOnceItsBack()
    {
        using var setup = await WatchAsync(create: false);
        Assert.Equal(new FolderWatchStatus(FolderWatchState.Unavailable, FolderWatchProblem.Missing), setup.Watcher.StatusOf(setup.Root));
        Assert.False(setup.Watcher.KeepsUpWith(setup.Root));

        Directory.CreateDirectory(setup.Root);
        await setup.Watcher.SyncAsync(default);

        Assert.Equal(FolderWatchState.Watching, setup.Watcher.StatusOf(setup.Root)?.State);
        Assert.Contains(setup.Queue.Requests, r => r.Path == setup.Root && r.Reason == FolderWatcher.Reason);
    }

    [Fact]
    public async Task AMissingFolder_IsntLookedForEveryMinute()
    {
        using var setup = await WatchAsync(create: false);
        setup.Watcher.LookForMissingEvery = TimeSpan.FromHours(1);
        Directory.CreateDirectory(setup.Root);
        Directory.Delete(setup.Root);
        await setup.Watcher.SyncAsync(default); // Looks, and won't again for an hour.

        Directory.CreateDirectory(setup.Root);
        await setup.Watcher.SyncAsync(default);
        Assert.Equal(FolderWatchProblem.Missing, setup.Watcher.StatusOf(setup.Root)?.Problem);
    }

    [Fact]
    public async Task AWatchedFolderThatGoes_SaysSo_AndIsWatchedAndScannedOnceItsBack()
    {
        using var setup = await WatchAsync();
        Directory.Delete(setup.Root);
        await setup.Watcher.SyncAsync(default);
        Assert.Equal(new FolderWatchStatus(FolderWatchState.Unavailable, FolderWatchProblem.Missing), setup.Watcher.StatusOf(setup.Root));
        Assert.False(setup.Watcher.KeepsUpWith(setup.Root));

        Directory.CreateDirectory(setup.Root);
        await setup.Watcher.SyncAsync(default);
        Assert.Equal(FolderWatchState.Watching, setup.Watcher.StatusOf(setup.Root)?.State);
        Assert.Contains(setup.Queue.Requests, r => r.Path == setup.Root && r.Reason == FolderWatcher.Reason);
    }

    [Fact]
    public async Task AWatchedFolderMadeAgainBetweenLooks_IsWatchedAfresh_AndNewFilesInItAreNoticed()
    {
        using var setup = await WatchAsync();
        Directory.Delete(setup.Root);
        await Task.Delay(20); // So the new folder's birth time differs, on file systems that keep milliseconds.
        Directory.CreateDirectory(setup.Root);
        await setup.Watcher.SyncAsync(default);
        Assert.Contains(setup.Queue.Requests, r => r.Path == setup.Root && r.Reason == FolderWatcher.Reason);

        var film = Path.Combine(setup.Root, "Heat (1995).mkv");
        await File.WriteAllTextAsync(film, "x");
        Assert.Contains(await WaitForAsync(setup, r => r.Path == film), r => r.Path == film);
    }

    [Fact]
    public async Task AWatchedFolderThatStaysPut_KeepsItsWatcher()
    {
        using var setup = await WatchAsync();
        await setup.Watcher.SyncAsync(default);
        await setup.Watcher.SyncAsync(default);
        Assert.DoesNotContain(setup.Queue.Requests, r => r.Path == setup.Root);
    }

    [Fact]
    public async Task AFolderAddedToALibrary_IsWatched_WithoutASecondScan()
    {
        using var setup = await WatchAsync();
        var added = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, $"added-{Guid.NewGuid():N}")).FullName;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            db.Libraries.Add(new Library { Name = $"Added {Guid.NewGuid():N}", Type = LibraryType.Movies, Folders = [new LibraryFolder { Path = added }] });
            await db.SaveChangesAsync();
        }

        await setup.Watcher.SyncAsync(default);
        Assert.Equal(FolderWatchState.Watching, setup.Watcher.StatusOf(added)?.State);
        Assert.DoesNotContain(setup.Queue.Requests, r => r.Path == added);
    }

    [Fact]
    public async Task AShare_IsLookedAtLessOften_ButASwappedFolderIsStillNoticed()
    {
        using var setup = await WatchAsync(create: false);
        Directory.CreateDirectory(setup.Root);
        var started = 0;
        setup.Watcher.IsShare = _ => true;
        setup.Watcher.NewWatcher = root =>
        {
            if (root == setup.Root)
            {
                Interlocked.Increment(ref started);
            }

            return new FileSystemWatcher(root);
        };
        await setup.Watcher.SyncAsync(default);
        Assert.Equal(FolderWatchState.Share, setup.Watcher.StatusOf(setup.Root)?.State);
        Assert.Equal(1, started);

        Directory.Delete(setup.Root);
        Directory.CreateDirectory(setup.Root);
        await setup.Watcher.SyncAsync(default);
        Assert.Equal(1, started); // the minute's look leaves it alone

        setup.Watcher.LookAtSharesEvery = TimeSpan.Zero;
        await setup.Watcher.SyncAsync(default);
        Assert.Equal(2, started);
    }

    [Fact]
    public async Task AFolderThatHangs_DoesntHoldUpTheOthers_NorStartTwice()
    {
        using var hung = await WatchAsync(create: false);
        using var local = await WatchAsync(create: false);
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var started = 0;
        hung.Watcher.StartsWaitedFor = TimeSpan.FromMilliseconds(200);
        hung.Watcher.Enable = watcher =>
        {
            if (watcher.Path == hung.Root)
            {
                Interlocked.Increment(ref started);
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(20));
            }

            watcher.EnableRaisingEvents = true;
        };
        Directory.CreateDirectory(hung.Root);
        Directory.CreateDirectory(local.Root);

        await hung.Watcher.SyncAsync(default).WaitAsync(TimeSpan.FromSeconds(10)); // both folders are in the database, so this one's watching both
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(FolderWatchState.Watching, hung.Watcher.StatusOf(local.Root)?.State);
        Assert.NotEqual(FolderWatchState.Watching, hung.Watcher.StatusOf(hung.Root)?.State);

        await hung.Watcher.SyncAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, started);

        release.Set();
        await WaitUntilAsync(() => hung.Watcher.StatusOf(hung.Root)?.State == FolderWatchState.Watching);
    }

    /// <summary>Hangs when it's asked about <paramref name="hung"/>, as a mount that's stopped answering does, until the test lets go.</summary>
    private sealed class Hang(string hung) : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private int _asked;

        public ManualResetEventSlim Entered { get; } = new();
        public int Asked => Volatile.Read(ref _asked);

        public void If(string path)
        {
            if (path.StartsWith(hung, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _asked);
                Entered.Set();
                _release.Wait(TimeSpan.FromSeconds(30));
            }
        }

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
            Entered.Dispose();
        }
    }

    [Fact]
    public async Task AFolderThatStopsAnswering_IsAskedOnce_AndHoldsUpNoNewFolder()
    {
        using var setup = await WatchAsync();
        using var hang = new Hang(setup.Root);
        setup.Watcher.LooksWaitedFor = TimeSpan.FromMilliseconds(100);
        setup.Watcher.Identify = path =>
        {
            hang.If(path);
            return FolderKind.Identity(path);
        };

        await Task.Run(() => setup.Watcher.SyncAsync(default)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(hang.Entered.IsSet);

        using var added = await WatchAsync();
        for (var i = 0; i < 5; i++)
        {
            await Task.Run(() => setup.Watcher.SyncAsync(default)).WaitAsync(TimeSpan.FromSeconds(5)); // not held up by it, or waiting on it again
        }

        Assert.Equal(1, hang.Asked);
        Assert.Equal(FolderWatchState.Watching, setup.Watcher.StatusOf(added.Root)?.State);
        Assert.Equal(FolderWatchState.Watching, setup.Watcher.StatusOf(setup.Root)?.State); // unanswered isn't gone

        // Answering again, it's looked at again; what the hung look says by then has gone out of date.
        hang.Release();
        await WaitUntilAsync(() =>
        {
            setup.Watcher.SyncAsync(default).GetAwaiter().GetResult();
            return hang.Asked >= 2;
        });
        Assert.Equal(FolderWatchState.Watching, setup.Watcher.StatusOf(setup.Root)?.State);
    }

    [Fact]
    public async Task AFolderThatStopsAnswering_DoesntHoldUpTheSwitchInSettings()
    {
        using var setup = await WatchAsync();
        using var hang = new Hang(setup.Root);
        setup.Watcher.LooksWaitedFor = TimeSpan.FromMilliseconds(100);
        setup.Watcher.Identify = path =>
        {
            hang.If(path);
            return FolderKind.Identity(path);
        };
        await Task.Run(() => setup.Watcher.SyncAsync(default)).WaitAsync(TimeSpan.FromSeconds(10));

        setup.Options.WatchFolders.Enabled = false;
        await Task.Run(() => setup.Watcher.SyncAsync(default)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(FolderWatchState.Off, setup.Watcher.StatusOf(setup.Root)?.State);
    }

    [Fact]
    public async Task ALibraryFolderThatStopsAnswering_HoldsUpNoOtherFoldersTitles()
    {
        using var setup = await WatchAsync();
        using var other = await WatchAsync();
        await setup.Watcher.SyncAsync(default); // watching the two
        using var hang = new Hang(setup.Root);
        var read = setup.Watcher.ReadSnapshot;
        setup.Watcher.FlushesWaitedFor = TimeSpan.FromMilliseconds(100);
        setup.Watcher.ReadSnapshot = path =>
        {
            hang.If(path);
            return read(path);
        };

        foreach (var root in new[] { setup.Root, other.Root })
        {
            Directory.CreateDirectory(Path.Combine(root, "Film (2020)"));
            await File.WriteAllTextAsync(Path.Combine(root, "Film (2020)", "Film.mkv"), "film");
        }

        var theirs = Path.Combine(other.Root, "Film (2020)");
        var ours = Path.Combine(setup.Root, "Film (2020)");
        for (var i = 0; i < 100 && !setup.Queue.Requests.Any(r => r.Path == theirs); i++)
        {
            await Task.Run(() => setup.Watcher.FlushAsync(default)).WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(50);
        }

        Assert.Contains(setup.Queue.Requests, r => r.Path == theirs);
        Assert.Equal(1, hang.Asked);
        Assert.DoesNotContain(setup.Queue.Requests, r => r.Path == ours);

        // Once it answers, its titles carry on.
        hang.Release();
        var requests = await WaitForAsync(setup, r => r.Path == ours);
        Assert.Contains(requests, r => r.Path == ours);
    }

    private static async Task WaitUntilAsync(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++)
        {
            await Task.Delay(100);
        }

        Assert.True(done());
    }

    [Fact]
    public async Task ASlowStart_DoesntHoldUpTheLibrariesList()
    {
        using var setup = await WatchAsync(create: false);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        setup.Watcher.Enable = watcher =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(20));
            watcher.EnableRaisingEvents = true;
        };

        Directory.CreateDirectory(setup.Root);
        var sync = Task.Run(() => setup.Watcher.SyncAsync(default));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

        // A folder taking its time (a big tree, a mount that's stopped answering) leaves the status readable.
        var status = await Task.Run(() => setup.Watcher.StatusOf(setup.Root)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(FolderWatchState.Unavailable, status?.State);

        release.Set();
        await sync;
        Assert.Equal(FolderWatchState.Watching, setup.Watcher.StatusOf(setup.Root)?.State);
    }

    private sealed class RefusableWatcher(string root) : FileSystemWatcher(root)
    {
        public void Report(Exception ex) => OnError(new ErrorEventArgs(ex));
    }

    private static IOException LimitReached() => new("The configured user limit (8192) on the number of inotify watches has been reached", 28);

    /// <summary>The way Linux refuses: the Error event, during the start, with no exception. Other tests' folders are in the same database.</summary>
    private static void RefuseWith(Setup setup, Func<Exception> error)
    {
        setup.Watcher.NewWatcher = root => new RefusableWatcher(root);
        setup.Watcher.Enable = w =>
        {
            if (w.Path == setup.Root)
            {
                ((RefusableWatcher)w).Report(error());
            }
            else
            {
                w.EnableRaisingEvents = true;
            }
        };
    }

    [Fact]
    public async Task AWatchRefusedAsItStarts_SaysSo_AndWaitsBeforeTryingAgain()
    {
        using var setup = await WatchAsync(create: false);
        Directory.CreateDirectory(setup.Root);
        var tries = 0;
        RefuseWith(setup, () =>
        {
            tries++;
            return LimitReached();
        });

        await setup.Watcher.SyncAsync(default);
        Assert.Equal(new FolderWatchStatus(FolderWatchState.Unavailable, FolderWatchProblem.Limit), setup.Watcher.StatusOf(setup.Root));
        Assert.False(setup.Watcher.KeepsUpWith(setup.Root));
        Assert.DoesNotContain(setup.Queue.Requests, r => r.Path == setup.Root);

        // Each try walks the tree, so a minute later it isn't tried again.
        await setup.Watcher.SyncAsync(default);
        Assert.Equal(1, tries);

        // Its time up, it's tried again.
        setup.Watcher.RetryRefusedAfter = TimeSpan.Zero;
        setup.Options.WatchFolders.Enabled = false;
        await setup.Watcher.SyncAsync(default);
        setup.Options.WatchFolders.Enabled = true;
        await setup.Watcher.SyncAsync(default);
        Assert.Equal(2, tries);
        await setup.Watcher.SyncAsync(default);
        Assert.Equal(3, tries);

        // Once the system lets it, it watches, and looks for what it missed.
        Assert.DoesNotContain(setup.Queue.Requests, r => r.Path == setup.Root);
        setup.Watcher.NewWatcher = root => new FileSystemWatcher(root);
        setup.Watcher.Enable = w => w.EnableRaisingEvents = true;
        await setup.Watcher.SyncAsync(default);
        Assert.Equal(FolderWatchState.Watching, setup.Watcher.StatusOf(setup.Root)?.State);
        Assert.Contains(setup.Queue.Requests, r => r.Path == setup.Root && r.Reason == FolderWatcher.Reason);
    }

    [Fact]
    public async Task AWatchThatFailsAfterStarting_SaysSo_AndHearsNoMore()
    {
        using var setup = await WatchAsync(create: false);
        Directory.CreateDirectory(setup.Root);
        RefusableWatcher? watching = null;
        setup.Watcher.NewWatcher = root =>
        {
            var watcher = new RefusableWatcher(root);
            watching = root == setup.Root ? watcher : watching;
            return watcher;
        };
        await setup.Watcher.SyncAsync(default);
        Assert.Equal(FolderWatchState.Watching, setup.Watcher.StatusOf(setup.Root)?.State);

        watching!.Report(LimitReached());
        Assert.Equal(new FolderWatchStatus(FolderWatchState.Unavailable, FolderWatchProblem.Limit), setup.Watcher.StatusOf(setup.Root));
        await setup.Watcher.SyncAsync(default);
        Assert.Equal(FolderWatchProblem.Limit, setup.Watcher.StatusOf(setup.Root)?.Problem);
    }

    [Fact]
    public async Task LostEvents_ScanTheWholeFolderOnce_AndKeepTheWatch()
    {
        using var setup = await WatchAsync(create: false);
        Directory.CreateDirectory(setup.Root);
        RefusableWatcher? watching = null;
        setup.Watcher.NewWatcher = root =>
        {
            var watcher = new RefusableWatcher(root);
            watching = root == setup.Root ? watcher : watching;
            return watcher;
        };
        await setup.Watcher.SyncAsync(default);

        watching!.Report(new InternalBufferOverflowException());
        watching.Report(new InternalBufferOverflowException());
        Assert.Equal(FolderWatchState.Watching, setup.Watcher.StatusOf(setup.Root)?.State);

        var requests = await WaitForAsync(setup, r => r.Path == setup.Root);
        Assert.Equal(setup.Root, Assert.Single(requests).Path);
        Assert.False(requests[0].ForceMetadata);
    }

    [Fact]
    public async Task AnUnreadableFolderInside_LeavesTheRestWatched()
    {
        using var setup = await WatchAsync(create: false);
        Directory.CreateDirectory(setup.Root);
        setup.Watcher.NewWatcher = root => new RefusableWatcher(root);
        setup.Watcher.Enable = w =>
        {
            if (w.Path == setup.Root)
            {
                ((RefusableWatcher)w).Report(new UnauthorizedAccessException("lost+found"));
            }

            w.EnableRaisingEvents = true;
        };

        await setup.Watcher.SyncAsync(default);
        Assert.Equal(FolderWatchState.Watching, setup.Watcher.StatusOf(setup.Root)?.State);

        var film = Path.Combine(setup.Root, "Heat (1995).mkv");
        await File.WriteAllTextAsync(film, "x");
        Assert.Contains(await WaitForAsync(setup, r => r.Path == film), r => r.Path == film);
    }

    [Fact]
    public async Task ARealUnreadableFolderInside_LeavesTheRestWatched()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // File modes are for Linux and macOS.
        }

        using var setup = await WatchAsync(create: false);
        var locked = Directory.CreateDirectory(Path.Combine(setup.Root, "lost+found"));
        File.SetUnixFileMode(locked.FullName, UnixFileMode.None);
        try
        {
            await setup.Watcher.SyncAsync(default);
            Assert.Equal(FolderWatchState.Watching, setup.Watcher.StatusOf(setup.Root)?.State);

            var film = Path.Combine(setup.Root, "Heat (1995).mkv");
            await File.WriteAllTextAsync(film, "x");
            Assert.Contains(await WaitForAsync(setup, r => r.Path == film), r => r.Path == film);
        }
        finally
        {
            File.SetUnixFileMode(locked.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task Podcasts_AreNotWatched()
    {
        using var setup = await WatchAsync(LibraryType.Podcasts);
        Assert.Equal(FolderWatchState.Excluded, setup.Watcher.StatusOf(setup.Root)?.State);
    }

    [Fact]
    public async Task TheSwitchOff_StopsWatching_AndOnAgainScansWhatWasMissed()
    {
        using var setup = await WatchAsync();
        setup.Options.WatchFolders.Enabled = false;
        await setup.Watcher.SyncAsync(default);
        Assert.Equal(FolderWatchState.Off, setup.Watcher.StatusOf(setup.Root)?.State);

        await File.WriteAllTextAsync(Path.Combine(setup.Root, "Heat (1995).mkv"), "x");
        Assert.Empty(await WaitForAsync(setup, _ => false, seconds: 2));

        setup.Options.WatchFolders.Enabled = true;
        await setup.Watcher.SyncAsync(default);
        Assert.Equal(FolderWatchState.Watching, setup.Watcher.StatusOf(setup.Root)?.State);
        Assert.Contains(setup.Queue.Requests, r => r.Path == setup.Root);
    }

    [Fact]
    public async Task TheSwitchOff_DropsWhatWasWaiting()
    {
        using var setup = await WatchAsync();
        setup.Watcher.Changed(setup.Root, "Heat (1995)/Heat (1995).mkv");
        setup.Options.WatchFolders.Enabled = false;
        await setup.Watcher.SyncAsync(default);

        Assert.Empty(await WaitForAsync(setup, _ => false, seconds: 2));
    }

    [Fact]
    public async Task TheSwitch_IsSavedInSettings_AndOnByDefault()
    {
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        async Task<bool?> ReadAsync() =>
            (await admin.GetFromJsonAsync<JsonElement>("/api/v1/settings")).GetProperty("watchFolders").GetBoolean();

        Assert.True(await ReadAsync());
        Assert.True((await admin.PutAsJsonAsync("/api/v1/settings", new { watchFolders = false })).IsSuccessStatusCode);
        try
        {
            var off = true;
            for (var i = 0; i < 50 && off; i++)
            {
                off = await ReadAsync() != false;
                await Task.Delay(off ? 100 : 0);
            }

            Assert.False(off, "the switch should read back as off");
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/v1/settings", new { watchFolders = true });
        }
    }

    [Fact]
    public async Task TheLibrariesList_SaysHowEachFolderIsWatched()
    {
        var watched = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, $"listed-{Guid.NewGuid():N}")).FullName;
        var missing = Path.Combine(factory.MediaDirectory, $"listed-missing-{Guid.NewGuid():N}");
        var library = new Library { Name = $"Listed {Guid.NewGuid():N}", Type = LibraryType.Movies, Folders = [new LibraryFolder { Path = watched }, new LibraryFolder { Path = missing }] };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
        }

        // The server's own watcher (the test server runs no background services, so it's synced by hand).
        await factory.Services.GetRequiredService<FolderWatcher>().SyncAsync(default);
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var listed = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/libraries/{library.Id}");
        var watch = listed.GetProperty("folderWatch").EnumerateArray().ToDictionary(f => f.GetProperty("path").GetString()!, f => f);
        Assert.Equal("Watching", watch[watched].GetProperty("state").GetString());
        Assert.Equal("Unavailable", watch[missing].GetProperty("state").GetString());
        Assert.Equal("Missing", watch[missing].GetProperty("problem").GetString());
        factory.Services.GetRequiredService<FolderWatcher>().Dispose();
    }

    [Theory]
    [InlineData(".DS_Store", true)]
    [InlineData("Show/._Episode.mkv", true)]
    [InlineData("Show/Season 1/Episode.mkv.part", true)]
    [InlineData("Film/Film.mkv.!qB", true)]
    [InlineData("@eaDir/Film.mkv", true)]
    [InlineData("#recycle/Film.mkv", true)]
    [InlineData("Film/Film.mkv~", true)]
    [InlineData("#snapshot/Film.mkv", true)]
    [InlineData("Film/@EADIR/Film.mkv", true)]
    [InlineData("#Alive (2020)/#Alive (2020).mkv", false)]
    [InlineData("@Home (2021)/@Home (2021).mkv", false)]
    [InlineData("Film/Film.mkv", false)]
    [InlineData("Show/Season 1/Show - S01E01.en.srt", false)]
    public void Ignored(string name, bool ignored) => Assert.Equal(ignored, FolderWatcher.Ignored(name));

    [Theory]
    [InlineData("Film/poster.jpg", true)]
    [InlineData("Film/Film (2020)-fanart.png", true)]
    [InlineData("Film/movie.nfo", true)]
    [InlineData("Artist/Album/cover.jpg", true)]
    [InlineData("Show/season01-poster.jpg", true)]
    [InlineData("Film/Film.mkv", false)]
    [InlineData("Photos/holiday.jpg", false)]
    public void ArtworkOrNfo(string name, bool artwork) => Assert.Equal(artwork, FolderWatcher.IsArtworkOrNfo(name));
}
