using Flexarr.Api.Configuration;
using Flexarr.Api.Playback;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Tests;

public sealed class CacheJanitorTests : IDisposable
{
    private readonly FlexarrApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task OldAndOrphanedCacheEntries_AreDeleted_AndTheRestKept()
    {
        var cache = _factory.Services.GetRequiredService<IOptionsMonitor<FlexarrOptions>>().CurrentValue.ResolvedCacheDirectory;
        Guid kept;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var library = new Library { Name = "Films", Type = LibraryType.Movies };
            var movie = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Movie, Title = "Film", SortTitle = "film" };
            var file = new MediaFile { MediaItemId = movie.Id, Path = "/films/film.mkv" };
            db.AddRange(library, movie, file);
            await db.SaveChangesAsync();
            kept = file.Id;
        }

        var gone = Guid.NewGuid();
        var keptPreviews = Directory.CreateDirectory(Path.Combine(cache, "trickplay", kept.ToString("N"))).FullName;
        var gonePreviews = Directory.CreateDirectory(Path.Combine(cache, "trickplay", gone.ToString("N"))).FullName;
        var goneFailure = Write(Path.Combine(cache, "trickplay", gone.ToString("N") + ".failed"));
        var crashedLive = Directory.CreateDirectory(Path.Combine(cache, "live", "0123456789abcdef")).FullName;
        Write(Path.Combine(crashedLive, "seg1.ts"));
        var fresh = Write(Path.Combine(cache, "subtitles", "fresh.vtt"));
        var stale = Write(Path.Combine(cache, "subtitles", "stale.vtt"));
        var staleIndex = Write(Path.Combine(cache, "remux-index", "stale.json"));
        var staleLogo = Write(Path.Combine(cache, "livetv-logos", "stale"));
        var longAgo = DateTime.UtcNow - CacheJanitor.KeptFor - TimeSpan.FromDays(1);
        foreach (var old in new[] { stale, staleIndex, staleLogo })
        {
            File.SetLastWriteTimeUtc(old, longAgo);
        }

        await _factory.Services.GetRequiredService<CacheJanitor>().RunOnceAsync(CancellationToken.None);

        Assert.True(Directory.Exists(keptPreviews));
        Assert.True(File.Exists(fresh));
        Assert.False(Directory.Exists(gonePreviews));
        Assert.False(File.Exists(goneFailure));
        Assert.False(Directory.Exists(crashedLive));
        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(staleIndex));
        Assert.False(File.Exists(staleLogo));
    }

    [Fact]
    public async Task PreparedDownloads_NoDownloadPointsAt_AreDeleted_OnceTheyAreAnHourOld()
    {
        var cache = _factory.Services.GetRequiredService<IOptionsMonitor<FlexarrOptions>>().CurrentValue.ResolvedCacheDirectory;
        var folder = Path.Combine(cache, "downloads");
        Guid ready, preparing;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var library = new Library { Name = "Films", Type = LibraryType.Movies };
            var movie = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Movie, Title = "Film", SortTitle = "film" };
            var readyRow = new Download
            {
                ProfileId = PlaybackState.DefaultUser, MediaItemId = movie.Id, Quality = DownloadQuality.Medium, Status = DownloadStatus.Ready,
            };
            readyRow.FilePath = Path.Combine(folder, $"{readyRow.Id:N}.mp4");
            var preparingRow = new Download
            {
                ProfileId = PlaybackState.DefaultUser, MediaItemId = movie.Id, Quality = DownloadQuality.High, Status = DownloadStatus.Preparing,
            };
            db.AddRange(library, movie, readyRow, preparingRow);
            await db.SaveChangesAsync();
            (ready, preparing) = (readyRow.Id, preparingRow.Id);
        }

        var readyFile = Write(Path.Combine(folder, $"{ready:N}.mp4"));
        var partFile = Write(Path.Combine(folder, $"{preparing:N}.part.mp4"));
        var orphan = Write(Path.Combine(folder, $"{Guid.NewGuid():N}.mp4")); // its profile was deleted
        var orphanPart = Write(Path.Combine(folder, $"{Guid.NewGuid():N}.part.m4a"));
        var justMade = Write(Path.Combine(folder, $"{Guid.NewGuid():N}.mp4")); // a row about to be saved
        foreach (var old in new[] { readyFile, partFile, orphan, orphanPart })
        {
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow - CacheJanitor.DownloadGrace - TimeSpan.FromMinutes(5));
        }

        await _factory.Services.GetRequiredService<CacheJanitor>().RunOnceAsync(CancellationToken.None);

        Assert.True(File.Exists(readyFile));
        Assert.True(File.Exists(partFile));
        Assert.True(File.Exists(justMade));
        Assert.False(File.Exists(orphan));
        Assert.False(File.Exists(orphanPart));
    }

    private static string Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }
}
