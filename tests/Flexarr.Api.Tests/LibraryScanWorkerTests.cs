using Flexarr.Api.Scanning;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Probing;
using Flexarr.Core.Scanning;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Api.Tests;

public class LibraryScanWorkerTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    [Fact]
    public async Task APathWithANullCharacter_IsDropped_AndTheWorkerKeepsScanning()
    {
        // The scan itself needs a probe; CI has no ffprobe, and a missing ffprobe fails the whole scan.
        using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IMediaProbe>(new FixedProbe())));
        var queue = new LibraryScanQueue();
        var root = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, $"films-{Guid.NewGuid():N}")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "Dune (2021)"));
        File.WriteAllText(Path.Combine(root, "Dune (2021)", "Dune (2021).mkv"), "x");
        var library = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies, Folders = [new LibraryFolder { Path = root }] };
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
        }

        // Path.GetFullPath throws on a null: in the same batch as a good request, it used to stop the server.
        await queue.EnqueueAsync(new LibraryScanRequest($"{root}/Bad\0Path", "Sonarr Download"));
        await queue.EnqueueAsync(new LibraryScanRequest(Path.Combine(root, "Dune (2021)"), "Sonarr Download"));
        using var worker = new LibraryScanWorker(queue, host.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<LibraryScanWorker>.Instance,
            host.Services.GetRequiredService<LibraryWorkGate>());
        await worker.StartAsync(default);

        var scanned = false;
        for (var i = 0; i < 100 && !scanned; i++)
        {
            await Task.Delay(100);
            using var scope = host.Services.CreateScope();
            scanned = await scope.ServiceProvider.GetRequiredService<FlexarrDbContext>().MediaItems.AnyAsync(m => m.LibraryId == library.Id);
        }

        Assert.True(scanned, "the good request in the batch should still be scanned");
        Assert.False(worker.ExecuteTask!.IsCompleted, "the worker should still be running");
        await worker.StopAsync(default);
    }

    [Fact]
    public async Task AWatchedFolder_IsFullyScannedWeekly_OthersEvery12Hours_AndBothAtStartup()
    {
        var watched = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, $"watched-{Guid.NewGuid():N}")).FullName;
        var missing = Path.Combine(factory.MediaDirectory, $"missing-{Guid.NewGuid():N}");
        var dayAgo = DateTime.UtcNow.AddDays(-1);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            db.Libraries.Add(new Library
            {
                Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies,
                Folders = [new LibraryFolder { Path = watched, FullScanAt = dayAgo }, new LibraryFolder { Path = missing, FullScanAt = dayAgo }],
            });
            await db.SaveChangesAsync();
        }

        var options = new Flexarr.Api.Configuration.FlexarrOptions();
        using var watcher = new FolderWatcher(new RecordingScanQueue(), factory.Services.GetRequiredService<IServiceScopeFactory>(), new StaticOptions(options),
            TimeProvider.System, NullLogger<FolderWatcher>.Instance);
        await watcher.SyncAsync(default);
        Assert.True(watcher.KeepsUpWith(watched));
        Assert.False(watcher.KeepsUpWith(missing));

        List<string> Queued(RecordingScanQueue q) => q.Requests.Select(r => r.Path).Where(p => p == watched || p == missing).ToList();
        LibraryScanWorker Worker(RecordingScanQueue q) => new(q, factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<LibraryScanWorker>.Instance,
            factory.Services.GetRequiredService<LibraryWorkGate>(), watcher);

        var scheduled = new RecordingScanQueue();
        using (var worker = Worker(scheduled))
        {
            await worker.EnqueueAllAsync("Scheduled", default);
        }

        Assert.Equal([missing], Queued(scheduled));

        var startup = new RecordingScanQueue();
        using (var worker = Worker(startup))
        {
            await worker.EnqueueAllAsync(LibraryScanWorker.StartupReason, default);
        }

        Assert.Equal(new[] { watched, missing }.Order(), Queued(startup).Order());

        // Switched off, the watched folder is back on 12 hours.
        options.WatchFolders.Enabled = false;
        await watcher.SyncAsync(default);
        var off = new RecordingScanQueue();
        using (var worker = Worker(off))
        {
            await worker.EnqueueAllAsync("Scheduled", default);
        }

        Assert.Contains(watched, Queued(off));
    }

    private sealed class StaticOptions(Flexarr.Api.Configuration.FlexarrOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<Flexarr.Api.Configuration.FlexarrOptions>
    {
        public Flexarr.Api.Configuration.FlexarrOptions CurrentValue => value;
        public Flexarr.Api.Configuration.FlexarrOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<Flexarr.Api.Configuration.FlexarrOptions, string?> listener) => null;
    }

    [Fact]
    public async Task AWatchedFolderOnHold_KeepsThe12HourScan()
    {
        var held = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, $"held-{Guid.NewGuid():N}")).FullName;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            db.Libraries.Add(new Library
            {
                Name = $"Held {Guid.NewGuid():N}", Type = LibraryType.Movies,
                Folders = [new LibraryFolder { Path = held, FullScanAt = DateTime.UtcNow.AddDays(-1), Hold = FolderHold.MostFilesGone, HeldSince = DateTime.UtcNow.AddDays(-1), HeldScans = 1 }],
            });
            await db.SaveChangesAsync();
        }

        using var watcher = new FolderWatcher(new RecordingScanQueue(), factory.Services.GetRequiredService<IServiceScopeFactory>(), new StaticOptions(new Flexarr.Api.Configuration.FlexarrOptions()),
            TimeProvider.System, NullLogger<FolderWatcher>.Instance);
        await watcher.SyncAsync(default);
        Assert.True(watcher.KeepsUpWith(held));

        var queue = new RecordingScanQueue();
        using var worker = new LibraryScanWorker(queue, factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<LibraryScanWorker>.Instance,
            factory.Services.GetRequiredService<LibraryWorkGate>(), watcher);
        await worker.EnqueueAllAsync("Scheduled", default);
        Assert.Contains(queue.Requests, r => r.Path == held);
    }

    [Fact]
    public void AChangeAScanHasAlreadySeen_IsntScannedAgain()
    {
        using var worker = new LibraryScanWorker(new RecordingScanQueue(), factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<LibraryScanWorker>.Instance,
            factory.Services.GetRequiredService<LibraryWorkGate>());
        var started = DateTime.UtcNow;
        var title = new LibraryScanRequest("/films/Dune", FolderWatcher.Reason, LastChange: started.AddSeconds(-1));
        Assert.False(worker.AlreadyScanned(title));

        worker.NoteScanned("/films/Dune", started);
        Assert.True(worker.AlreadyScanned(title));
        Assert.False(worker.AlreadyScanned(title with { LastChange = started.AddSeconds(1) }));
        Assert.False(worker.AlreadyScanned(title with { ForceMetadata = true }));
        Assert.False(worker.AlreadyScanned(title with { LastChange = null }));
        Assert.False(worker.AlreadyScanned(title with { Path = "/films/Heat" }));

        // A scan of the whole folder covers its titles.
        worker.NoteScanned("/films", started.AddSeconds(5));
        Assert.True(worker.AlreadyScanned(title with { Path = "/films/Heat", LastChange = started }));
    }

    [Fact]
    public void Coalesce_KeepsTheLatestChange_AndOnlySubtitlesIfAllAre()
    {
        var now = DateTimeOffset.UtcNow;
        var merged = LibraryScanWorker.Coalesce([
            new LibraryScanRequest("/films/Dune", FolderWatcher.Reason, LastChange: now, OnlySubtitles: true),
            new LibraryScanRequest("/films/Dune", FolderWatcher.Reason, LastChange: now.AddSeconds(5), OnlySubtitles: false),
        ]).Single();
        Assert.Equal(now.AddSeconds(5), merged.LastChange);
        Assert.False(merged.OnlySubtitles);

        // A webhook's request has no such time: it's always scanned.
        Assert.Null(LibraryScanWorker.Coalesce([new LibraryScanRequest("/films/Dune", "Webhook"), new LibraryScanRequest("/films/Dune", FolderWatcher.Reason, LastChange: now)]).Single().LastChange);
    }

    [Fact]
    public async Task TheAddedEvent_SaysNothingOfAnyonesWatching_AndSkipsAFilmThatOnlyMoved()
    {
        var since = DateTime.UtcNow.AddMinutes(-1);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Moved {Guid.NewGuid():N}", Type = LibraryType.Movies };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        MediaItem Film(string title, string tmdb, DateTime seen) =>
            new() { LibraryId = library.Id, Kind = MediaKind.Movie, Title = title, SortTitle = title, TmdbId = tmdb, FirstSeenAt = seen, Path = $"/films/{Guid.NewGuid():N}" };
        var fresh = Film("Heat", "949001", DateTime.UtcNow);
        var old = Film("Dune", "949002", DateTime.UtcNow.AddDays(-30));
        old.MissingSince = DateTime.UtcNow;
        var moved = Film("Dune", "949002", DateTime.UtcNow);
        db.MediaItems.AddRange(fresh, old, moved);
        db.PlaybackStates.Add(new PlaybackState { MediaItem = fresh, UserId = PlaybackState.DefaultUser, Watched = true });
        await db.SaveChangesAsync();

        var events = factory.Services.GetRequiredService<Flexarr.Api.Events.PlaybackEvents>();
        using var listener = events.Listen();
        using var worker = new LibraryScanWorker(new RecordingScanQueue(), factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<LibraryScanWorker>.Instance,
            factory.Services.GetRequiredService<LibraryWorkGate>(), events: events);
        await worker.AnnounceAddedAsync(db, [library.Id], since, default);

        Assert.True(listener.Added.TryRead(out var announced));
        Assert.Equal(fresh.Id, announced!.Item.Id);
        Assert.False(announced.Item.UserData.Watched);
        Assert.False(listener.Added.TryRead(out _));
    }

    [Fact]
    public async Task ANewFilm_IsAnnouncedOnTheEventStream()
    {
        using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IMediaProbe>(new FixedProbe())));
        var root = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, $"films-{Guid.NewGuid():N}")).FullName;
        var library = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies, Folders = [new LibraryFolder { Path = root }] };
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
        }

        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var response = await client.GetAsync(new Uri("/api/v1/events", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync());
        Assert.Equal(": connected", await stream.ReadLineAsync());

        Directory.CreateDirectory(Path.Combine(root, "Dune (2021)"));
        File.WriteAllText(Path.Combine(root, "Dune (2021)", "Dune (2021).mkv"), "x");
        var queue = new LibraryScanQueue();
        await queue.EnqueueAsync(new LibraryScanRequest(Path.Combine(root, "Dune (2021)"), FolderWatcher.Reason));
        using var worker = new LibraryScanWorker(queue, host.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<LibraryScanWorker>.Instance,
            host.Services.GetRequiredService<LibraryWorkGate>(), events: host.Services.GetRequiredService<Flexarr.Api.Events.PlaybackEvents>());
        await worker.StartAsync(default);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            string? line;
            do
            {
                line = await stream.ReadLineAsync(timeout.Token);
            }
            while (line is not null && line != "event: added");

            var data = await stream.ReadLineAsync(timeout.Token);
            Assert.StartsWith("data: ", data, StringComparison.Ordinal);
            var item = System.Text.Json.JsonDocument.Parse(data!["data: ".Length..]).RootElement.GetProperty("item");
            Assert.Equal("Movie", item.GetProperty("kind").GetString());
            Assert.Equal(library.Id, item.GetProperty("libraryId").GetGuid());
            Assert.StartsWith("Dune", item.GetProperty("title").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            await worker.StopAsync(default);
        }
    }

    [Fact]
    public async Task TheMetadataTimer_RefreshesOutOfDateTitles_AFewAtATime()
    {
        var library = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies, Folders = [new LibraryFolder { Path = $"/films-{Guid.NewGuid():N}" }] };
        var month = DateTime.UtcNow.AddDays(-30);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            db.Libraries.Add(library);
            for (var n = 0; n < MetadataRefreshWorker.BatchSize + 3; n++)
            {
                db.MediaItems.Add(new MediaItem
                {
                    LibraryId = library.Id, Kind = MediaKind.Movie, Title = $"Film {n}", SortTitle = $"film {n:D2}", Path = $"{library.Folders[0].Path}/Film {n}",
                    MetadataUpdatedAt = month.AddMinutes(n),
                });
            }

            await db.SaveChangesAsync();
        }

        using var worker = new MetadataRefreshWorker(factory.Services.GetRequiredService<IServiceScopeFactory>(), factory.Services.GetRequiredService<LibraryWorkGate>(),
            NullLogger<MetadataRefreshWorker>.Instance);
        await worker.RefreshDueAsync(default);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var stillDue = await db.MediaItems.Where(i => i.LibraryId == library.Id && i.MetadataUpdatedAt < DateTime.UtcNow.AddDays(-7)).OrderBy(i => i.SortTitle).Select(i => i.Title).ToListAsync();
            Assert.Equal([$"Film {MetadataRefreshWorker.BatchSize}", $"Film {MetadataRefreshWorker.BatchSize + 1}", $"Film {MetadataRefreshWorker.BatchSize + 2}"], stillDue);
        }
    }

    [Fact]
    public async Task TheScheduledScan_SkipsAFolderScannedInFullWithinTwelveHours_AndQueuesItWhenTheyreUp()
    {
        var root = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, $"films-{Guid.NewGuid():N}")).FullName;
        var library = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies, Folders = [new LibraryFolder { Path = root }] };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
        }

        var queue = new LibraryScanQueue();
        LibraryScanWorker Worker() => new(queue, factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<LibraryScanWorker>.Instance,
            factory.Services.GetRequiredService<LibraryWorkGate>());
        async Task<List<string>> QueuedAsync(LibraryScanWorker worker)
        {
            await worker.EnqueueAllAsync("Startup", default);
            var queued = new List<string>();
            while (queue.TryDequeue(out var request))
            {
                if (request.Path == root)
                {
                    queued.Add(request.Path);
                }
            }

            return queued;
        }

        async Task FullScanAtAsync(DateTime? at)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            await db.LibraryFolders.Where(f => f.LibraryId == library.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.FullScanAt, at));
        }

        // Never scanned in full: queued, and not again while that's still to do.
        using (var worker = Worker())
        {
            Assert.Equal([root], await QueuedAsync(worker));
            Assert.Empty(await QueuedAsync(worker));
        }

        // Started again an hour after a full scan: left until the 12 hours are up.
        await FullScanAtAsync(DateTime.UtcNow.AddHours(-1));
        using (var worker = Worker())
        {
            Assert.Empty(await QueuedAsync(worker));
        }

        // Started again 13 hours after: queued.
        await FullScanAtAsync(DateTime.UtcNow.AddHours(-13));
        using (var worker = Worker())
        {
            Assert.Equal([root], await QueuedAsync(worker));
        }

        // A clock put back since: queued rather than waited for.
        await FullScanAtAsync(DateTime.UtcNow.AddHours(5));
        using (var worker = Worker())
        {
            Assert.Equal([root], await QueuedAsync(worker));
        }
    }

    [Fact]
    public async Task AScanOfTheWholeFolder_RecordsWhenItFinished_AndAScanOfPartOfItDoesnt()
    {
        using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IMediaProbe>(new FixedProbe())));
        var root = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, $"films-{Guid.NewGuid():N}")).FullName;
        foreach (var film in new[] { "Dune (2021)", "Heat (1995)" })
        {
            Directory.CreateDirectory(Path.Combine(root, film));
            File.WriteAllText(Path.Combine(root, film, $"{film}.mkv"), "x");
        }

        // Scanned in full an hour ago: the worker's start leaves it alone.
        var hourAgo = DateTime.UtcNow.AddHours(-1);
        var library = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies, Folders = [new LibraryFolder { Path = root, FullScanAt = hourAgo }] };
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
        }

        var queue = new LibraryScanQueue();
        using var worker = new LibraryScanWorker(queue, host.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<LibraryScanWorker>.Instance,
            host.Services.GetRequiredService<LibraryWorkGate>());
        async Task<(int Items, DateTime? FullScanAt)> StateAsync()
        {
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            return (await db.MediaItems.CountAsync(m => m.LibraryId == library.Id && m.Kind == MediaKind.Movie),
                (await db.LibraryFolders.AsNoTracking().SingleAsync(f => f.LibraryId == library.Id)).FullScanAt);
        }

        await queue.EnqueueAsync(new LibraryScanRequest(Path.Combine(root, "Dune (2021)"), "Radarr Download"));
        await worker.StartAsync(default);
        for (var i = 0; i < 100 && (await StateAsync()).Items == 0; i++)
        {
            await Task.Delay(100);
        }

        Assert.Equal((1, (DateTime?)hourAgo), await StateAsync());

        var before = DateTime.UtcNow;
        await queue.EnqueueAsync(new LibraryScanRequest(root, "Library scan"));
        for (var i = 0; i < 100 && (await StateAsync()).FullScanAt == hourAgo; i++)
        {
            await Task.Delay(100);
        }

        var (items, at) = await StateAsync();
        await worker.StopAsync(default);
        Assert.Equal(2, items);
        Assert.NotNull(at);
        Assert.InRange(at.Value, before, DateTime.UtcNow);
    }

    [Fact]
    public void Coalesce_DropsDuplicatesAndNestedPaths()
    {
        var requests = new[]
        {
            new LibraryScanRequest("/tv/Show/Season 01", "Sonarr Download"),
            new LibraryScanRequest("/tv/Show", "Sonarr Download"),
            new LibraryScanRequest("/tv/Show", "Sonarr Download"),
            new LibraryScanRequest("/tv/Other", "Sonarr Download"),
            new LibraryScanRequest("/tv/Showtime", "Sonarr Download"),
        };

        var paths = LibraryScanWorker.Coalesce(requests).Select(r => r.Path);

        Assert.Equal(["/tv/Show", "/tv/Other", "/tv/Showtime"], paths);
    }

    [Fact]
    public void Coalesce_KeepsTheWebhooksTvdbId()
    {
        var requests = new[]
        {
            new LibraryScanRequest("/tv/Show", "Emby-style update (Created)"),
            new LibraryScanRequest("/tv/Show", "Sonarr Download", TvdbId: 371980),
        };

        Assert.Equal(371980, Assert.Single(LibraryScanWorker.Coalesce(requests)).TvdbId);
    }

    private sealed class FixedProbe : IMediaProbe
    {
        public Task<ProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new ProbeResult("mkv", 1800, 5_000_000, VideoRange.Sdr,
                [new MediaStream { Index = 0, Type = StreamType.Video, Codec = "hevc", Width = 1920, Height = 1080 }]));
    }
}
