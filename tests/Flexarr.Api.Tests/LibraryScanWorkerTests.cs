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
    public async Task TheScheduledScan_IsFullOnceADay_AndOtherwiseOnlyWhatChanged()
    {
        var root = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, $"films-{Guid.NewGuid():N}")).FullName;
        foreach (var film in new[] { "Dune (2021)", "Heat (1995)" })
        {
            Directory.CreateDirectory(Path.Combine(root, film));
            File.WriteAllText(Path.Combine(root, film, $"{film}.mkv"), "x");
        }

        var library = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies, Folders = [new LibraryFolder { Path = root }] };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
        }

        var queue = new LibraryScanQueue();
        using var worker = new LibraryScanWorker(queue, factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<LibraryScanWorker>.Instance,
            factory.Services.GetRequiredService<LibraryWorkGate>());
        async Task<List<string>> QueuedAsync()
        {
            await worker.EnqueueAllAsync("Scheduled", default);
            var queued = new List<string>();
            while (queue.TryDequeue(out var request))
            {
                if (request.Path.StartsWith(root, StringComparison.Ordinal))
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

        // Never scanned in full: in full.
        Assert.Equal([root], await QueuedAsync());

        // Scanned in full an hour ago, nothing changed since: nothing.
        foreach (var folder in Directory.EnumerateDirectories(root).Append(root))
        {
            Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow.AddHours(-2));
        }

        await FullScanAtAsync(DateTime.UtcNow.AddHours(-1));
        Assert.Empty(await QueuedAsync());

        // A film's folder changed: just that one.
        File.WriteAllText(Path.Combine(root, "Heat (1995)", "Heat (1995).srt"), "x");
        Assert.Equal([Path.Combine(root, "Heat (1995)")], await QueuedAsync());

        // A day on: in full again.
        await FullScanAtAsync(DateTime.UtcNow - LibraryScanWorker.FullScanAfter - TimeSpan.FromMinutes(1));
        Assert.Equal([root], await QueuedAsync());
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
