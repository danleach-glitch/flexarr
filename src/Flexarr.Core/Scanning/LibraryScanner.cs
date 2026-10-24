using System.Collections.Concurrent;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Parsing;
using Flexarr.Core.Paths;
using Flexarr.Core.Probing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Flexarr.Core.Scanning;

public sealed record ScanResult(int Added, int Updated, int Removed, int Unchanged, int Unmatched, int ProbeFailures);

/// <summary>
/// Reconciles the database with what is on disk for a library, or a folder within it:
/// adds new files, re-probes changed ones, removes missing ones and prunes empty items.
/// </summary>
public sealed partial class LibraryScanner(FlexarrDbContext db, IMediaProbe probe, ScanProgress progress, ILogger<LibraryScanner> logger)
{
    private const int BatchSize = 50;
    private const int ProbeConcurrency = 4;

    public async Task<ScanResult> ScanAsync(Library library, string? scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(library);

        var roots = library.Folders.Select(f => f.Path).ToList();
        var targets = scope is null
            ? roots.Select(r => (Root: r, Scope: r)).ToList()
            : roots.Where(r => PathComparison.IsUnder(scope, r)).Select(r => (Root: r, Scope: scope)).ToList();

        var state = await ItemIndex.LoadAsync(db, library.Id, cancellationToken).ConfigureAwait(false);
        int added = 0, updated = 0, removed = 0, unchanged = 0, unmatched = 0, failures = 0;

        foreach (var (root, target) in targets)
        {
            if (!Directory.Exists(root))
            {
                // An unmounted drive must not look like "everything was deleted".
                LogRootMissing(root);
                continue;
            }

            var existing = await db.MediaFiles
                .Where(f => f.MediaItem!.LibraryId == library.Id && (f.Path == target || f.Path.StartsWith(target + "/")))
                .ToDictionaryAsync(f => f.Path, StringComparer.Ordinal, cancellationToken)
                .ConfigureAwait(false);

            var onDisk = MediaFileEnumerator.Enumerate(root, target).ToList();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var work = new List<(FileInfo File, MediaFile? Existing)>();

            foreach (var file in onDisk)
            {
                seen.Add(file.FullName);
                if (existing.TryGetValue(file.FullName, out var known)
                    && known.Size == file.Length && known.ModifiedAt == file.LastWriteTimeUtc && known.ProbeError is null)
                {
                    unchanged++;
                }
                else
                {
                    work.Add((file, known));
                }
            }

            progress.Begin(library.Id, target, work.Count);

            foreach (var batch in work.Chunk(BatchSize))
            {
                var probes = new ConcurrentDictionary<string, (ProbeResult? Result, string? Error)>(StringComparer.Ordinal);
                await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = ProbeConcurrency, CancellationToken = cancellationToken }, async (w, ct) =>
                {
                    try
                    {
                        probes[w.File.FullName] = (await probe.ProbeAsync(w.File.FullName, ct).ConfigureAwait(false), null);
                    }
                    catch (MediaProbeException ex)
                    {
                        probes[w.File.FullName] = (null, ex.Message);
                    }
                }).ConfigureAwait(false);

                foreach (var (file, known) in batch)
                {
                    var (result, error) = probes[file.FullName];
                    if (error is not null)
                    {
                        failures++;
                        LogProbeFailed(file.FullName, error);
                    }

                    if (known is not null)
                    {
                        await db.Entry(known).Collection(f => f.Streams).LoadAsync(cancellationToken).ConfigureAwait(false);
                        Apply(known, file, result, error);
                        updated++;
                        continue;
                    }

                    var item = state.Resolve(library, root, file.FullName);
                    if (item is null)
                    {
                        unmatched++;
                        LogUnmatched(file.FullName);
                        continue;
                    }

                    var mediaFile = new MediaFile { Path = file.FullName, MediaItemId = item.Id };
                    Apply(mediaFile, file, result, error);
                    db.MediaFiles.Add(mediaFile);
                    added++;
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                progress.Advance(batch.Length);
            }

            foreach (var gone in existing.Values.Where(f => !seen.Contains(f.Path)))
            {
                db.MediaFiles.Remove(gone);
                removed++;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await PruneEmptyItemsAsync(library.Id, cancellationToken).ConfigureAwait(false);

        library.LastScannedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        progress.End();

        return new ScanResult(added, updated, removed, unchanged, unmatched, failures);
    }

    private static void Apply(MediaFile target, FileInfo file, ProbeResult? result, string? error)
    {
        target.Size = file.Length;
        target.ModifiedAt = file.LastWriteTimeUtc;
        target.ProbeError = error;
        target.Streams.Clear();

        if (result is null)
        {
            return;
        }

        target.Container = result.Container;
        target.DurationSeconds = result.DurationSeconds;
        target.Bitrate = result.Bitrate;
        target.VideoRange = result.VideoRange;
        target.Streams.AddRange(result.Streams);

        var video = result.Streams.FirstOrDefault(s => s.Type == StreamType.Video);
        target.VideoCodec = video?.Codec;
        target.Width = video?.Width;
        target.Height = video?.Height;

        var audio = result.Streams.FirstOrDefault(s => s.Type == StreamType.Audio && s.IsDefault)
            ?? result.Streams.FirstOrDefault(s => s.Type == StreamType.Audio);
        target.AudioCodec = audio?.Codec;
        target.AudioChannels = audio?.Channels;
    }

    /// <summary>Removes movies/episodes without files, then seasons and series without children.</summary>
    private async Task PruneEmptyItemsAsync(Guid libraryId, CancellationToken ct)
    {
        var items = db.MediaItems.Where(i => i.LibraryId == libraryId);
        await items.Where(i => (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Episode) && !i.Files.Any()).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await items.Where(i => i.Kind == MediaKind.Season && !i.Children.Any()).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await items.Where(i => i.Kind == MediaKind.Series && !i.Children.Any()).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Library folder {Root} is missing; skipping so its items are not removed")]
    private partial void LogRootMissing(string root);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not probe {Path}: {Error}")]
    private partial void LogProbeFailed(string path, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Could not work out which episode {Path} is; skipping")]
    private partial void LogUnmatched(string path);

    /// <summary>In-memory index of a library's items so a scan resolves parents without a query per file.</summary>
    private sealed class ItemIndex(FlexarrDbContext db)
    {
        private readonly Dictionary<string, MediaItem> _byPath = new(StringComparer.Ordinal);
        private readonly Dictionary<(Guid Parent, int Number), MediaItem> _byNumber = [];

        public static async Task<ItemIndex> LoadAsync(FlexarrDbContext db, Guid libraryId, CancellationToken ct)
        {
            var index = new ItemIndex(db);
            foreach (var item in await db.MediaItems.Where(i => i.LibraryId == libraryId).ToListAsync(ct).ConfigureAwait(false))
            {
                index.Track(item);
            }

            return index;
        }

        public MediaItem? Resolve(Library library, string root, string filePath)
        {
            var segments = Path.GetRelativePath(root, filePath).Split(Path.DirectorySeparatorChar);
            return library.Type switch
            {
                LibraryType.Movies => ResolveMovie(library, root, segments),
                LibraryType.TvShows => ResolveEpisode(library, root, segments),
                _ => null,
            };
        }

        private MediaItem ResolveMovie(Library library, string root, string[] segments)
        {
            // root/Movie (Year)/file.mkv → the folder is the movie; root/Movie (Year).mkv → the file is.
            var path = Path.Combine(root, segments[0]);
            if (_byPath.TryGetValue(path, out var movie))
            {
                return movie;
            }

            var parsed = MovieNameParser.Parse(segments[0]);
            movie = Add(new MediaItem
            {
                LibraryId = library.Id,
                Kind = MediaKind.Movie,
                Title = parsed.Title,
                SortTitle = SortTitle.For(parsed.Title),
                Year = parsed.Year,
                Path = path,
                TmdbId = parsed.Ids.TmdbId,
                ImdbId = parsed.Ids.ImdbId,
            });
            return movie;
        }

        private MediaItem? ResolveEpisode(Library library, string root, string[] segments)
        {
            if (segments.Length < 2)
            {
                return null; // A loose file in the TV root has no series.
            }

            var seriesPath = Path.Combine(root, segments[0]);
            var parsedSeries = TvNameParser.ParseSeriesFolder(segments[0]);
            var seasonFromFolder = segments.Length > 2 ? TvNameParser.ParseSeasonFolder(segments[1]) : null;
            var parsed = TvNameParser.ParseEpisode(segments[^1], seasonFromFolder, parsedSeries.Title);
            if (parsed is null)
            {
                return null;
            }

            if (!_byPath.TryGetValue(seriesPath, out var series))
            {
                series = Add(new MediaItem
                {
                    LibraryId = library.Id,
                    Kind = MediaKind.Series,
                    Title = parsedSeries.Title,
                    SortTitle = SortTitle.For(parsedSeries.Title),
                    Year = parsedSeries.Year,
                    Path = seriesPath,
                    TvdbId = parsedSeries.Ids.TvdbId,
                    TmdbId = parsedSeries.Ids.TmdbId,
                    ImdbId = parsedSeries.Ids.ImdbId,
                });
            }

            if (!_byNumber.TryGetValue((series.Id, parsed.Season), out var season))
            {
                season = Add(new MediaItem
                {
                    LibraryId = library.Id,
                    ParentId = series.Id,
                    Kind = MediaKind.Season,
                    Title = parsed.Season == 0 ? "Specials" : $"Season {parsed.Season}",
                    SortTitle = SortTitle.ForNumber(parsed.Season),
                    SeasonNumber = parsed.Season,
                });
            }

            var episodeKey = EpisodeKey(parsed.AirDate is null ? parsed.Episode : null, parsed.AirDate)!.Value;
            if (!_byNumber.TryGetValue((season.Id, episodeKey), out var episode))
            {
                var title = parsed.Title
                    ?? (parsed.AirDate is { } d ? d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : $"Episode {parsed.Episode}");
                episode = Add(new MediaItem
                {
                    LibraryId = library.Id,
                    ParentId = season.Id,
                    Kind = MediaKind.Episode,
                    Title = title,
                    SortTitle = SortTitle.ForNumber(episodeKey),
                    SeasonNumber = parsed.Season,
                    EpisodeNumber = parsed.AirDate is null ? parsed.Episode : null,
                    EpisodeNumberEnd = parsed.EpisodeEnd,
                    AirDate = parsed.AirDate,
                });
            }

            return episode;
        }

        private static int? EpisodeKey(int? number, DateOnly? airDate) => number ?? airDate?.DayNumber;

        private MediaItem Add(MediaItem item)
        {
            db.MediaItems.Add(item);
            Track(item);
            return item;
        }

        private void Track(MediaItem item)
        {
            switch (item.Kind)
            {
                case MediaKind.Movie or MediaKind.Series when item.Path is not null:
                    _byPath[item.Path] = item;
                    break;
                case MediaKind.Season when item.ParentId is { } p && item.SeasonNumber is { } s:
                    _byNumber[(p, s)] = item;
                    break;
                case MediaKind.Episode when item.ParentId is { } p && EpisodeKey(item.EpisodeNumber, item.AirDate) is { } key:
                    _byNumber[(p, key)] = item;
                    break;
            }
        }
    }
}
