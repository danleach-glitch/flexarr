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

        var requests = await WaitForAsync(setup, r => r.Path == setup.Root);
        Assert.Equal(setup.Root, Assert.Single(requests).Path);
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
