using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Configuration;
using Flexarr.Api.Scanning;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
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
