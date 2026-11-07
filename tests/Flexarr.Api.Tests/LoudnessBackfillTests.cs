using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Endpoints;
using Flexarr.Api.Playback;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Parsing;
using Flexarr.Core.Probing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>Tracks scanned before ReplayGain was kept have it read in the background, so their first play doesn't wait for ffprobe.</summary>
public class LoudnessBackfillTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task TracksScannedBefore_HaveTheirReplayGainKept_OnlyWhenIdle_AndOnlyAsTheScanFoundThem()
    {
        var probe = new TagProbe();
        using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IMediaProbe>(probe)));
        var folder = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, $"music-{Guid.NewGuid():N}")).FullName;
        var library = new Library { Name = "Music", Type = LibraryType.Music };
        var album = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Album, Title = "Album", SortTitle = "album" };
        var movie = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Movie, Title = "Film", SortTitle = "film" };
        var rows = new List<(MediaItem Track, MediaFile File)>();

        // More than a batch of old tracks, so the sweep goes on past the first.
        var old = Enumerable.Range(1, LoudnessBackfill.BatchSize + 5).Select(n => Track($"{n:D2} - Old.flac")).ToList();
        var kept = Track("50 - Kept.flac", f => f.Loudness = new Loudness(-1, -2, 0.5, 0.6).ToJson());
        var changed = Track("51 - Retagged.flac", f => f.ModifiedAt = f.ModifiedAt.AddMinutes(-5));
        var unreadable = Track("52 - Unreadable.flac", f => f.ProbeError = "Invalid data");
        var gone = Track("53 - Gone.flac");
        File.Delete(gone.Path);
        var broken = Track("54 - Broken.flac");
        probe.Fails.Add(broken.Path);
        var film = Track("film.mkv", f => f.MediaItemId = movie.Id);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            db.AddRange(library, album, movie);
            db.AddRange(rows.Select(r => r.Track));
            db.AddRange(rows.Select(r => r.File));
            await db.SaveChangesAsync();
        }

        var sweep = new LoudnessBackfill.Sweep();
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();

            // Someone is watching: nothing is read.
            Assert.True(await LoudnessBackfill.BackfillAsync(db, probe, sweep, () => true, default));
            Assert.Equal(0, probe.Calls);

            var batches = 0;
            while (await LoudnessBackfill.BackfillAsync(db, probe, sweep, () => false, default))
            {
                batches++;
            }

            Assert.Equal(2, batches);
            // Each old track once, and the one ffprobe can't read; none of the rest.
            Assert.Equal(old.Count + 1, probe.Calls);
            Assert.Equal(old.Select(f => f.Path).Append(broken.Path).Order(), probe.Probed.Order());

            // A sweep after that has nothing to read, the file ffprobe failed on included.
            Assert.False(await LoudnessBackfill.BackfillAsync(db, probe, sweep, () => false, default));
            Assert.Equal(old.Count + 1, probe.Calls);
        }

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var loudness = await db.MediaFiles.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.Loudness);
            Assert.All(old, f => Assert.Equal(TagProbe.Loudness.ToJson(), loudness[f.Id]));
            Assert.Equal(new Loudness(-1, -2, 0.5, 0.6).ToJson(), loudness[kept.Id]);
            Assert.Null(loudness[changed.Id]);
            Assert.Null(loudness[unreadable.Id]);
            Assert.Null(loudness[gone.Id]);
            Assert.Null(loudness[broken.Id]);
            Assert.Null(loudness[film.Id]);
        }

        // An old track's first play is answered without ffprobe now.
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var answer = await client.GetFromJsonAsync<LoudnessDto>(new Uri($"/api/v1/items/{old[0].MediaItemId}/loudness", UriKind.Relative), Json);
        Assert.Equal(new LoudnessDto(-7.5, -8.25, 0.9, 1.0), answer);
        Assert.Equal(old.Count + 1, probe.Calls);

        MediaFile Track(string name, Action<MediaFile>? change = null)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllBytes(path, [0x66, 0x4C, 0x61, 0x43]);
            var track = new MediaItem { LibraryId = library.Id, ParentId = album.Id, Kind = MediaKind.Track, Title = name, SortTitle = name };
            var file = new MediaFile { MediaItemId = track.Id, Path = path, Size = 4, ModifiedAt = File.GetLastWriteTimeUtc(path), Container = "flac", AudioCodec = "flac" };
            change?.Invoke(file);
            rows.Add((track, file));
            return file;
        }
    }

    [Fact]
    public async Task ASweep_PassesOverTracksSkippedBefore_HoweverMany_WithoutAskingTheDatabaseToLeaveThemOut()
    {
        var probe = new TagProbe();
        using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IMediaProbe>(probe)));
        var folder = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, $"music-{Guid.NewGuid():N}")).FullName;
        var library = new Library { Name = "Music", Type = LibraryType.Music };
        var album = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Album, Title = "Album", SortTitle = "album" };
        var files = Enumerable.Range(1, (LoudnessBackfill.BatchSize * 2) + 5).Select(n =>
        {
            var path = Path.Combine(folder, $"{n:D2} - Track.flac");
            File.WriteAllBytes(path, [0x66, 0x4C, 0x61, 0x43]);
            var track = new MediaItem { LibraryId = library.Id, ParentId = album.Id, Kind = MediaKind.Track, Title = $"{n}", SortTitle = $"{n:D2}" };
            return (Track: track, File: new MediaFile { MediaItemId = track.Id, Path = path, Size = 4, ModifiedAt = File.GetLastWriteTimeUtc(path), Container = "flac", AudioCodec = "flac" });
        }).ToList();

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        db.AddRange(library, album);
        db.AddRange(files.Select(f => f.Track));
        db.AddRange(files.Select(f => f.File));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // A sweep before skipped more than two pages of these (a drive that wasn't there), among far more ids than
        // SQLite takes as parameters.
        var sweep = new LoudnessBackfill.Sweep();
        var skipped = files.Take((LoudnessBackfill.BatchSize * 2) + 1).Select(f => f.File.Id).ToList();
        sweep.Skipped.UnionWith(skipped);
        sweep.Skipped.UnionWith(Enumerable.Range(0, 40_000).Select(_ => Guid.NewGuid()));

        try
        {
            Assert.True(await LoudnessBackfill.BackfillAsync(db, probe, sweep, () => false, default));
            // (Only this test's tracks: the class's other sweep may have left some of its own behind.)
            Assert.Equal(files.Skip(skipped.Count).Select(f => f.File.Path), probe.Probed.Where(p => p.StartsWith(folder, StringComparison.Ordinal)));
            Assert.False(await LoudnessBackfill.BackfillAsync(db, probe, sweep, () => false, default));
            Assert.Null(sweep.After);

            // The next sweep starts afresh and reads none of them again.
            Assert.False(await LoudnessBackfill.BackfillAsync(db, probe, sweep, () => false, default));
            Assert.Equal(4, probe.Probed.Count(p => p.StartsWith(folder, StringComparison.Ordinal)));
            var kept = await db.MediaFiles.IgnoreQueryFilters().AsNoTracking().Where(f => f.Path.StartsWith(folder)).ToDictionaryAsync(f => f.Id, f => f.Loudness);
            Assert.All(skipped, id => Assert.Null(kept[id]));
            Assert.All(files.Skip(skipped.Count), f => Assert.Equal(TagProbe.Loudness.ToJson(), kept[f.File.Id]));
        }
        finally
        {
            // The class's other sweep shares this database: leave it no unfilled tracks of this one's.
            await db.MediaFiles.IgnoreQueryFilters().Where(f => f.Path.StartsWith(folder)).ExecuteDeleteAsync();
        }
    }

    /// <summary>Stands in for ffprobe: every file carries the same ReplayGain tags, except those it fails on.</summary>
    private sealed class TagProbe : IMediaProbe
    {
        public static readonly Loudness Loudness = new(-7.5, -8.25, 0.9, 1.0);

        public HashSet<string> Fails { get; } = [];

        public List<string> Probed { get; } = [];

        public int Calls => Probed.Count;

        public Task<ProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
        {
            Probed.Add(path);
            if (Fails.Contains(path))
            {
                throw new MediaProbeException("Invalid data found when processing input");
            }

            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["REPLAYGAIN_TRACK_GAIN"] = "-7.50 dB",
                ["REPLAYGAIN_ALBUM_GAIN"] = "-8.25 dB",
                ["REPLAYGAIN_TRACK_PEAK"] = "0.9",
                ["REPLAYGAIN_ALBUM_PEAK"] = "1.0",
            };
            return Task.FromResult(new ProbeResult("flac", 0.2, 465080, VideoRange.Sdr, [new MediaStream { Index = 0, Type = StreamType.Audio, Codec = "flac", Channels = 1 }]) { Tags = tags });
        }
    }
}
