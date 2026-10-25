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

/// <param name="Removed">Files removed for good, having been missing for longer than the grace period.</param>
/// <param name="Missing">Files found gone this scan: hidden, but kept (with their items' watch state) for now.</param>
public sealed record ScanResult(int Added, int Updated, int Removed, int Unchanged, int Unmatched, int ProbeFailures, int Missing = 0);

/// <summary>
/// Reconciles the database with what is on disk for a library, or a folder within it:
/// adds new files, re-probes changed ones, hides missing ones and removes those gone for longer than a grace period.
/// </summary>
public sealed partial class LibraryScanner(FlexarrDbContext db, IMediaProbe probe, ScanProgress progress, ILogger<LibraryScanner> logger)
{
    private const int BatchSize = 50;
    private const int ProbeConcurrency = 4;

    /// <summary>A full scan of a folder with this many files or more refuses to hide over half of them at once.</summary>
    private const int MassRemovalMinimum = 10;

    /// <summary>
    /// How long a file stays missing (hidden, its item's watch history and collections kept) before it's removed for
    /// good. Long enough to outlast a drive that mounts late or an *arr upgrade copying the new file across volumes.
    /// </summary>
    public static readonly TimeSpan MissingGracePeriod = TimeSpan.FromHours(24);

    /// <summary>
    /// A folder held because most of its files went (it isn't empty, so it is mounted) is let through once full scans
    /// have found exactly the same this many times, over at least <see cref="SettledAfter"/>: a clear-out, not a hiccup.
    /// An empty folder is never let through this way; it waits for Radarr/Sonarr to say they deleted it, or for
    /// someone to confirm it in Library health.
    /// </summary>
    public const int SettledScans = 3;

    public static readonly TimeSpan SettledAfter = TimeSpan.FromHours(24);

    public Task<ScanResult> ScanAsync(Library library, string? scope, CancellationToken cancellationToken) =>
        ScanAsync(library, scope, retryFailedProbes: false, cancellationToken);

    public Task<ScanResult> ScanAsync(Library library, string? scope, bool retryFailedProbes, CancellationToken cancellationToken) =>
        ScanAsync(library, scope, retryFailedProbes, deleted: false, cancellationToken);

    /// <param name="library">The library to scan.</param>
    /// <param name="scope">A folder or file in the library to limit the scan to; null for all of it.</param>
    /// <param name="retryFailedProbes">Probe files ffprobe couldn't read again even if they haven't changed.</param>
    /// <param name="deleted">
    /// Sonarr, Radarr or Lidarr deleted <paramref name="scope"/>: its files going is expected, even if that empties
    /// the library folder.
    /// </param>
    /// <param name="cancellationToken">Stops the scan.</param>
    public async Task<ScanResult> ScanAsync(Library library, string? scope, bool retryFailedProbes, bool deleted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (library.Type == LibraryType.Podcasts)
        {
            return new ScanResult(0, 0, 0, 0, 0, 0); // its episodes come from their feeds, not from the folder
        }

        try
        {
            return await ScanCoreAsync(library, scope, retryFailedProbes, deleted, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            progress.End();
        }
    }

    private async Task<ScanResult> ScanCoreAsync(Library library, string? scope, bool retryFailedProbes, bool deleted, CancellationToken cancellationToken)
    {
        var roots = library.Folders.Select(f => f.Path).ToList();
        var targets = scope is null
            ? roots.Select(r => (Root: r, Scope: r)).ToList()
            : roots.Where(r => PathComparison.IsUnder(scope, r)).Select(r => (Root: r, Scope: OnDiskSpelling(r, scope))).ToList();

        var state = await ItemIndex.LoadAsync(db, library, scope is null ? null : targets, cancellationToken).ConfigureAwait(false);
        int added = 0, updated = 0, removed = 0, unchanged = 0, unmatched = 0, failures = 0, missing = 0;
        var held = false; // A folder looked unmounted: remove nothing this time.

        foreach (var (root, target) in targets)
        {
            // Only a scan of the whole folder records or ends a hold: a webhook's counts are of its own corner.
            var full = PathComparison.IsUnder(root, target);
            var folder = await db.LibraryFolders.FirstOrDefaultAsync(f => f.LibraryId == library.Id && f.Path == root, cancellationToken).ConfigureAwait(false);
            if (!Directory.Exists(root))
            {
                // An unmounted drive must not look like "everything was deleted".
                LogRootMissing(root);
                held = true;
                if (full && folder is not null && folder.Hold != FolderHold.FolderMissing)
                {
                    Hold(folder, FolderHold.FolderMissing, 0, 0, DateTime.UtcNow);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            // Missing files too, so one that comes back picks up where it left off.
            var existing = new Dictionary<string, MediaFile>(StringComparer.Ordinal);
            var duplicates = new List<MediaFile>();
            var known = await KnownFilesAsync(library.Id, root, target, cancellationToken).ConfigureAwait(false);
            foreach (var file in known)
            {
                if (!existing.TryAdd(PathComparison.Key(file.Path), file))
                {
                    duplicates.Add(file); // The same file in another Unicode spelling, added before paths were compared composed.
                }
            }

            var suspicious = false; // The folder looked unmounted or cleared out; normal again clears the record of that.
            var released = false;
            if (existing.Count > 0 && IsEmpty(root))
            {
                // A network share or external drive that hasn't mounted yet often leaves an empty folder behind. Not when
                // Radarr or Sonarr have just said they deleted these files themselves, though.
                suspicious = true;
                var hiding = existing.Values.Count(f => f.MissingSince is null) + duplicates.Count(f => f.MissingSince is null);
                if (!(deleted && !full) && KeepHolding(folder, FolderHold.FolderEmpty, hiding, existing.Count, full))
                {
                    LogRootEmpty(root, existing.Count);
                    held = true;
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (deleted && !full && await ShowsOtherFilesAsync(library.Id, root, existing.Values.Concat(duplicates), cancellationToken).ConfigureAwait(false))
                {
                    // Radarr/Sonarr deleted one title while the rest of the folder looks unmounted: only that title's
                    // files go. The folder stays held, and nothing is purged while it looks like this.
                    held = true;
                }
                else if (deleted && folder is not null)
                {
                    folder.ReleasedAt ??= DateTime.UtcNow; // Emptied by Radarr/Sonarr: later scans let what it hid go too.
                }

                released = true;
            }

            var onDisk = MediaFileEnumerator.Enumerate(root, target, audio: library.Type == LibraryType.Music)
                .Where(f => PathComparison.IsUnder(f.FullName, root))
                .ToList();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var work = new List<(FileInfo File, MediaFile? Existing)>();

            foreach (var file in onDisk)
            {
                var key = PathComparison.Key(file.FullName);
                if (!seen.Add(key))
                {
                    continue;
                }

                existing.TryGetValue(key, out var match);
                if (match is not null)
                {
                    match.MissingSince = null;
                }

                // Files ffprobe couldn't read are tried again when they change (or when asked), not on every scan.
                if (match is not null && match.Size == file.Length && match.ModifiedAt == file.LastWriteTimeUtc
                    && (match.ProbeError is null || !retryFailedProbes))
                {
                    unchanged++;
                }
                else
                {
                    work.Add((file, match));
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

                // One query for the streams being replaced, rather than one per file.
                var changedIds = batch.Where(w => w.Existing is not null).Select(w => w.Existing!.Id).ToList();
                if (changedIds.Count > 0)
                {
                    await db.MediaStreams.Where(s => changedIds.Contains(s.MediaFileId)).LoadAsync(cancellationToken).ConfigureAwait(false);
                }

                foreach (var (file, match) in batch)
                {
                    var (result, error) = probes[file.FullName];
                    if (error is not null)
                    {
                        failures++;
                        LogProbeFailed(PathComparison.Printable(file.FullName), PathComparison.Printable(error));
                    }

                    if (match is not null)
                    {
                        Apply(match, file, result, error);
                        updated++;
                        continue;
                    }

                    var item = state.Resolve(library, root, file.FullName, file.LastWriteTimeUtc, result);
                    if (item is null)
                    {
                        unmatched++;
                        LogUnmatched(PathComparison.Printable(file.FullName));
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

            var gone = existing.Values.Where(f => !seen.Contains(PathComparison.Key(f.Path))).Concat(duplicates)
                .Where(f => f.MissingSince is null)
                .ToList();
            var holding = false;
            if (!released && full && existing.Count >= MassRemovalMinimum && gone.Count * 2 > existing.Count)
            {
                // Most of a library vanishing at once is far likelier a mount or permissions problem than a clear-out.
                suspicious = true;
                holding = KeepHolding(folder, FolderHold.MostFilesGone, gone.Count, existing.Count, full);
                if (holding)
                {
                    LogMassRemoval(root, gone.Count, existing.Count);
                    held = true;
                }
            }

            if (!holding)
            {
                var now = DateTime.UtcNow;
                foreach (var file in gone)
                {
                    file.MissingSince = now;
                    missing++;
                }
            }

            if (full && !suspicious && folder is not null && (folder.Hold is not null || folder.ReleasedAt is not null))
            {
                Clear(folder, releasedAt: null); // Looks normal again.
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!held)
        {
            removed = await RemoveExpiredAsync(library.Id, cancellationToken).ConfigureAwait(false);
        }

        await PruneEmptyItemsAsync(library.Id, cancellationToken).ConfigureAwait(false);
        await MarkMissingItemsAsync(library.Id, cancellationToken).ConfigureAwait(false);
        // Ids from folder names are known now; those from Radarr/Sonarr after the metadata refresh, which does this again.
        await WatchHistoryCarryOver.CarryOverAsync(db, cancellationToken).ConfigureAwait(false);

        library.LastScannedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new ScanResult(added, updated, removed, unchanged, unmatched, failures, missing);
    }

    /// <summary>
    /// <paramref name="scope"/> as the library's own scans spell it: the configured root, then each name as it is on
    /// disk. A webhook may send "é" composed where the folder was created decomposed (or the reverse); matching the
    /// stored paths keeps the scan from taking the folder for a new series.
    /// </summary>
    private static string OnDiskSpelling(string root, string scope)
    {
        var rootKey = PathComparison.Key(root);
        var relative = PathComparison.Key(scope)[rootKey.Length..];
        var current = root.Length > 1 ? root.TrimEnd('/') : root;
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] names;
            try
            {
                names = Directory.Exists(current) ? Directory.EnumerateFileSystemEntries(current).Select(n => Path.GetFileName(n)).ToArray() : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                names = [];
            }

            var composed = segment.Normalize(System.Text.NormalizationForm.FormC);
            var name = names.FirstOrDefault(n => n == segment)
                ?? names.FirstOrDefault(n => n.Normalize(System.Text.NormalizationForm.FormC) == composed)
                ?? segment;
            current = Path.Combine(current, name);
        }

        return current;
    }

    /// <summary>
    /// The library's files at or under <paramref name="target"/>, missing ones included. When the target isn't on disk
    /// (a deleted series folder) it keeps the webhook's spelling, which may not be how its files were stored ("é"
    /// composed, say, where the files have it decomposed): then they're matched by <see cref="PathComparison.Key"/>
    /// among the files of the nearest folder that is there.
    /// </summary>
    private async Task<List<MediaFile>> KnownFilesAsync(Guid libraryId, string root, string target, CancellationToken ct)
    {
        var files = db.MediaFiles.IgnoreQueryFilters().Where(f => f.MediaItem!.LibraryId == libraryId);
        if (Directory.Exists(target) || File.Exists(target))
        {
            return await files.Where(f => f.Path == target || f.Path.StartsWith(target + "/")).ToListAsync(ct).ConfigureAwait(false);
        }

        var prefix = Path.GetDirectoryName(target);
        while (prefix is not null && PathComparison.IsUnder(prefix, root) && !Directory.Exists(prefix))
        {
            prefix = Path.GetDirectoryName(prefix);
        }

        prefix = prefix is not null && PathComparison.IsUnder(prefix, root) ? prefix.TrimEnd('/') : root.TrimEnd('/');
        var candidates = await files.AsNoTracking().Where(f => f.Path.StartsWith(prefix + "/")).Select(f => new { f.Id, f.Path })
            .ToListAsync(ct).ConfigureAwait(false);
        var ids = candidates.Where(f => PathComparison.IsUnder(f.Path, target)).Select(f => f.Id).ToList();
        return ids.Count == 0 ? [] : await files.Where(f => ids.Contains(f.Id)).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Whether the library shows any file in <paramref name="root"/> besides <paramref name="these"/>.</summary>
    private async Task<bool> ShowsOtherFilesAsync(Guid libraryId, string root, IEnumerable<MediaFile> these, CancellationToken ct)
    {
        var ids = these.Select(f => f.Id).ToList();
        var prefix = root.TrimEnd('/') + "/";
        return await db.MediaFiles.IgnoreQueryFilters()
            .AnyAsync(f => f.MediaItem!.LibraryId == libraryId && f.MissingSince == null && f.Path.StartsWith(prefix) && !ids.Contains(f.Id), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a suspicious-looking folder's files stay as they are this scan, recording the hold for Library health.
    /// It's let through when someone confirmed the removal there, or (for <see cref="FolderHold.MostFilesGone"/>) when
    /// full scans have found exactly the same <see cref="SettledScans"/> times over <see cref="SettledAfter"/>; and once
    /// let through, what it hid isn't held again while the folder still looks the same.
    /// </summary>
    /// <param name="folder">The library folder; null if it isn't one (never held, then).</param>
    /// <param name="reason">What looked wrong.</param>
    /// <param name="hiding">The files this scan would hide.</param>
    /// <param name="known">The files the library knows in the folder.</param>
    /// <param name="full">A scan of the whole folder: only these record, or end, a hold.</param>
    private bool KeepHolding(LibraryFolder? folder, FolderHold reason, int hiding, int known, bool full)
    {
        if (folder is null)
        {
            return true;
        }

        if (hiding == 0 && folder.ReleasedAt is not null)
        {
            return false; // Already let through: what's left is waiting out the grace period.
        }

        if (!full)
        {
            return true;
        }

        var now = DateTime.UtcNow;
        if (folder.Hold != reason || folder.HeldMissing != hiding || folder.HeldKnown != known || folder.HeldSince is null)
        {
            Hold(folder, reason, hiding, known, now); // Something new: start counting again (and ask again).
            return true;
        }

        folder.HeldScans++;
        var confirmed = folder.RemovalConfirmed;
        var settled = reason == FolderHold.MostFilesGone && folder.HeldScans >= SettledScans && now - folder.HeldSince >= SettledAfter;
        if (!confirmed && !settled)
        {
            return true;
        }

        LogHoldReleased(folder.Path, hiding, known, confirmed ? "the removal was confirmed" : $"{folder.HeldScans} scans over {(now - folder.HeldSince.Value).TotalHours:F0} hours found the same");
        Clear(folder, releasedAt: now);
        return false;
    }

    private void Hold(LibraryFolder folder, FolderHold reason, int hiding, int known, DateTime now)
    {
        folder.Hold = reason;
        folder.HeldSince = now;
        folder.HeldScans = 1;
        folder.HeldMissing = hiding;
        folder.HeldKnown = known;
        Unconfirm(folder);
        folder.ReleasedAt = null;
    }

    private void Clear(LibraryFolder folder, DateTime? releasedAt)
    {
        folder.Hold = null;
        folder.HeldSince = null;
        folder.HeldScans = 0;
        folder.HeldMissing = 0;
        folder.HeldKnown = 0;
        Unconfirm(folder);
        folder.ReleasedAt = releasedAt;
    }

    /// <summary>
    /// Written even if it was false when this scan loaded the folder: a "Confirm removal" saved while it ran was for
    /// the numbers shown then, not the ones this scan has just recorded.
    /// </summary>
    private void Unconfirm(LibraryFolder folder)
    {
        folder.RemovalConfirmed = false;
        db.Entry(folder).Property(f => f.RemovalConfirmed).IsModified = true;
    }

    private static bool IsEmpty(string directory)
    {
        try
        {
            return !Directory.EnumerateFileSystemEntries(directory).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
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

    /// <summary>Removes files missing for longer than <see cref="MissingGracePeriod"/>.</summary>
    private async Task<int> RemoveExpiredAsync(Guid libraryId, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - MissingGracePeriod;
        return await db.MediaFiles.IgnoreQueryFilters()
            .Where(f => f.MissingSince != null && f.MissingSince < cutoff && f.MediaItem!.LibraryId == libraryId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Hides playable items whose files are all missing, then the seasons/albums and series/artists with nothing left
    /// to show; and brings back any whose files have reappeared.
    /// </summary>
    private async Task MarkMissingItemsAsync(Guid libraryId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var items = db.MediaItems.IgnoreQueryFilters().Where(i => i.LibraryId == libraryId);

        var playable = items.Where(i => i.Kind == MediaKind.Movie || i.Kind == MediaKind.Episode || i.Kind == MediaKind.Track);
        await playable.Where(i => i.MissingSince == null && i.Files.All(f => f.MissingSince != null))
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.MissingSince, now), ct).ConfigureAwait(false);
        await playable.Where(i => i.MissingSince != null && i.Files.Any(f => f.MissingSince == null))
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.MissingSince, (DateTime?)null), ct).ConfigureAwait(false);

        await MarkContainersAsync(items.Where(i => i.Kind == MediaKind.Season || i.Kind == MediaKind.Album)).ConfigureAwait(false);
        await MarkContainersAsync(items.Where(i => i.Kind == MediaKind.Series || i.Kind == MediaKind.Artist)).ConfigureAwait(false);

        async Task MarkContainersAsync(IQueryable<MediaItem> containers)
        {
            await containers.Where(i => i.MissingSince == null && i.Children.All(c => c.MissingSince != null))
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.MissingSince, now), ct).ConfigureAwait(false);
            await containers.Where(i => i.MissingSince != null && i.Children.Any(c => c.MissingSince == null))
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.MissingSince, (DateTime?)null), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Removes playable items without files, then the albums/seasons and artists/series left empty.</summary>
    private async Task PruneEmptyItemsAsync(Guid libraryId, CancellationToken ct)
    {
        // Missing files still count: their items wait out the grace period with them.
        // Their history and collections are kept a while, by id, in case the title comes back under a new path.
        var items = db.MediaItems.IgnoreQueryFilters().Where(i => i.LibraryId == libraryId);
        var playable = items.Where(i => (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Episode || i.Kind == MediaKind.Track) && !i.Files.Any());
        await WatchHistoryCarryOver.BuryAsync(db, playable, ct).ConfigureAwait(false);
        await playable.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await items.Where(i => (i.Kind == MediaKind.Season || i.Kind == MediaKind.Album) && !i.Children.Any()).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var containers = items.Where(i => (i.Kind == MediaKind.Series || i.Kind == MediaKind.Artist) && !i.Children.Any());
        await WatchHistoryCarryOver.BuryAsync(db, containers, ct).ConfigureAwait(false);
        await containers.ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Library folder {Root} is missing; skipping so its items are not removed")]
    private partial void LogRootMissing(string root);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Library folder {Root} is empty but held {Known} files; skipping so its items are not removed (is the drive or share mounted?)")]
    private partial void LogRootEmpty(string root, int known);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Missing} of the {Known} files in {Root} have gone at once; keeping them all in case the drive or share is having trouble")]
    private partial void LogMassRemoval(string root, int missing, int known);

    [LoggerMessage(Level = LogLevel.Information, Message = "No longer holding {Root}: hiding {Missing} of its {Known} files, as {Why}")]
    private partial void LogHoldReleased(string root, int missing, int known, string why);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not probe {Path}: {Error}")]
    private partial void LogProbeFailed(string path, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Could not work out which episode {Path} is; skipping")]
    private partial void LogUnmatched(string path);

    /// <summary>In-memory index of a library's items so a scan resolves parents without a query per file.</summary>
    private sealed class ItemIndex(FlexarrDbContext db)
    {
        private readonly Dictionary<string, MediaItem> _byPath = new(StringComparer.Ordinal);
        private readonly Dictionary<(Guid Parent, int Number), MediaItem> _byNumber = [];

        /// <param name="db">The database.</param>
        /// <param name="library">The library being scanned.</param>
        /// <param name="scoped">The folders a scoped scan covers, or null for the whole library.</param>
        /// <param name="ct">Stops the load.</param>
        public static async Task<ItemIndex> LoadAsync(FlexarrDbContext db, Library library, IReadOnlyList<(string Root, string Scope)>? scoped, CancellationToken ct)
        {
            var index = new ItemIndex(db);

            // Missing items too: a file that reappears goes back to its old item, with its watch history.
            // Tracks are never looked up, and a scan of one series only needs that series' seasons and episodes.
            var items = db.MediaItems.IgnoreQueryFilters().Where(i => i.LibraryId == library.Id);
            var parents = await items
                .Where(i => i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series || i.Kind == MediaKind.Artist || i.Kind == MediaKind.Album)
                .ToListAsync(ct).ConfigureAwait(false);
            parents.ForEach(index.Track);
            if (library.Type != LibraryType.TvShows)
            {
                return index;
            }

            var seasons = items.Where(i => i.Kind == MediaKind.Season);
            var episodes = items.Where(i => i.Kind == MediaKind.Episode);
            var folders = scoped?.Select(t => SeriesFolder(t.Root, t.Scope)).ToList();
            if (folders is not null && folders.All(f => f is not null))
            {
                var seriesIds = parents.Where(i => i.Kind == MediaKind.Series && i.Path is not null && folders.Contains(PathComparison.Key(i.Path)))
                    .Select(i => i.Id).ToList();
                seasons = seasons.Where(i => i.ParentId != null && seriesIds.Contains(i.ParentId.Value));
                episodes = episodes.Where(i => i.Parent!.ParentId != null && seriesIds.Contains(i.Parent.ParentId.Value));
            }

            (await seasons.ToListAsync(ct).ConfigureAwait(false)).ForEach(index.Track);
            (await episodes.ToListAsync(ct).ConfigureAwait(false)).ForEach(index.Track);
            return index;
        }

        /// <summary>The series folder (first level under the root) a scope is in, or null when it's the whole root.</summary>
        private static string? SeriesFolder(string root, string scope)
        {
            var rootKey = PathComparison.Key(root);
            var first = PathComparison.Key(scope)[rootKey.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return first is null ? null : PathComparison.Key(Path.Combine(rootKey, first));
        }

        /// <param name="library">The library being scanned.</param>
        /// <param name="root">The library folder containing the file.</param>
        /// <param name="filePath">The video file.</param>
        /// <param name="fileTime">
        /// The file's modified time, used as "date added": it's when Radarr/Sonarr imported it, which is what
        /// "recently added" should mean, rather than when Flexarr first saw it.
        /// </param>
        /// <param name="probe">What ffprobe found; music is organised by its tags.</param>
        public MediaItem? Resolve(Library library, string root, string filePath, DateTime fileTime, ProbeResult? probe)
        {
            var segments = Path.GetRelativePath(root, filePath).Split(Path.DirectorySeparatorChar);
            _addedAt = fileTime < DateTime.UtcNow ? fileTime : DateTime.UtcNow;
            return library.Type switch
            {
                LibraryType.Movies => ResolveMovie(library, root, segments),
                LibraryType.TvShows => ResolveEpisode(library, root, segments),
                LibraryType.Music => ResolveTrack(library, root, filePath, probe),
                _ => null,
            };
        }

        private static readonly string[] CoverFiles = ["cover.jpg", "folder.jpg", "front.jpg", "album.jpg", "cover.png", "folder.png"];
        private static readonly string[] ArtistImageFiles = ["artist.jpg", "folder.jpg", "artist.png"];

        private MediaItem ResolveTrack(Library library, string root, string filePath, ProbeResult? probe)
        {
            var info = MusicTags.Read(probe?.Tags ?? new Dictionary<string, string>(), root, filePath);
            var albumDirectory = Path.GetDirectoryName(filePath)!;

            // An album folder is one album whatever its tags say, so a title Lidarr corrected (or a stray tag on one
            // track) doesn't split it in two; and the album's artist is its artist, under whichever name.
            MediaItem? artist = null;
            if (_byPath.TryGetValue(PathComparison.Key(albumDirectory), out var album) && album.ParentId is { } artistId)
            {
                _artistsById.TryGetValue(artistId, out artist);
            }

            var artistKey = $"artist:{Fold(info.Artist)}";
            if (artist is null && !_byKey.TryGetValue(artistKey, out artist))
            {
                // Artist/Album/Track layout: the artist folder may hold an artist photo.
                var artistDirectory = Path.GetDirectoryName(albumDirectory);
                var photo = artistDirectory is not null && artistDirectory.Length > root.TrimEnd('/').Length
                    ? ArtistImageFiles.Select(f => Path.Combine(artistDirectory, f)).FirstOrDefault(File.Exists)
                    : null;
                artist = Add(new MediaItem
                {
                    LibraryId = library.Id,
                    Kind = MediaKind.Artist,
                    Title = info.Artist,
                    SortTitle = SortTitle.For(info.Artist),
                    MusicBrainzId = info.ArtistMusicBrainzId,
                    PosterUrl = photo is null ? null : $"file://{photo}",
                });
            }

            var albumKey = $"album:{artist.Id}:{Fold(info.Album)}";
            if (album is null && !_byKey.TryGetValue(albumKey, out album))
            {
                var cover = CoverFiles.Select(f => Path.Combine(albumDirectory, f)).FirstOrDefault(File.Exists);
                album = Add(new MediaItem
                {
                    LibraryId = library.Id,
                    ParentId = artist.Id,
                    Kind = MediaKind.Album,
                    Title = info.Album,
                    SortTitle = SortTitle.For(info.Album),
                    Year = info.Year,
                    Path = albumDirectory,
                    MusicBrainzId = info.AlbumMusicBrainzId,
                    PosterUrl = cover is not null ? $"file://{cover}" : probe?.HasCoverArt == true ? $"embedded://{filePath}" : null,
                });
            }
            else if (album.PosterUrl is null && probe?.HasCoverArt == true)
            {
                album.PosterUrl = $"embedded://{filePath}";
            }

            artist.PosterUrl ??= album.PosterUrl; // No artist photo: show their first album's cover.

            return Add(new MediaItem
            {
                LibraryId = library.Id,
                ParentId = album.Id,
                Kind = MediaKind.Track,
                Title = info.Title,
                SortTitle = SortTitle.ForNumber(((info.Disc ?? 1) * 1000) + (info.Track ?? 0)),
                Year = info.Year,
                DiscNumber = info.Disc,
                TrackNumber = info.Track,
                // The track's own artist only when it isn't the album's, under either of the artist's names (the
                // tags' or Lidarr's): a compilation's tracks, a featured artist.
                TrackArtist = info.TrackArtist is { } tagged && !IsNamed(artist, tagged) ? tagged : null,
                Path = filePath,
            });
        }

        private static bool IsNamed(MediaItem artist, string name) =>
            MusicTags.SameArtist(artist.Title, name) || (artist.OriginalTitle is { } original && MusicTags.SameArtist(original, name));

        private static string Fold(string value) => value.Trim().Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();

        private readonly Dictionary<string, MediaItem> _byKey = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, MediaItem> _artistsById = [];

        private DateTime _addedAt = DateTime.UtcNow;

        private MediaItem ResolveMovie(Library library, string root, string[] segments)
        {
            // root/Movie (Year)/file.mkv → the folder is the movie; root/Movie (Year).mkv → the file is.
            var path = Path.Combine(root, segments[0]);
            if (_byPath.TryGetValue(PathComparison.Key(path), out var movie))
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

            if (!_byPath.TryGetValue(PathComparison.Key(seriesPath), out var series))
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
            item.AddedAt = _addedAt;
            db.MediaItems.Add(item);
            Track(item);
            return item;
        }

        private void Track(MediaItem item)
        {
            switch (item.Kind)
            {
                case MediaKind.Movie or MediaKind.Series when item.Path is not null:
                    _byPath[PathComparison.Key(item.Path)] = item;
                    break;
                case MediaKind.Season when item.ParentId is { } p && item.SeasonNumber is { } s:
                    _byNumber[(p, s)] = item;
                    break;
                case MediaKind.Episode when item.ParentId is { } p && EpisodeKey(item.EpisodeNumber, item.AirDate) is { } key:
                    _byNumber[(p, key)] = item;
                    break;
                // Under the tags' name too (kept as the original title when Lidarr renamed it), since the files still
                // carry the tags.
                case MediaKind.Artist:
                    _artistsById[item.Id] = item;
                    _byKey[$"artist:{Fold(item.Title)}"] = item;
                    if (item.OriginalTitle is { } taggedArtist)
                    {
                        _byKey.TryAdd($"artist:{Fold(taggedArtist)}", item);
                    }

                    break;
                case MediaKind.Album when item.ParentId is { } artistId:
                    _byKey[$"album:{artistId}:{Fold(item.Title)}"] = item;
                    if (item.OriginalTitle is { } taggedAlbum)
                    {
                        _byKey.TryAdd($"album:{artistId}:{Fold(taggedAlbum)}", item);
                    }

                    if (item.Path is not null)
                    {
                        _byPath[PathComparison.Key(item.Path)] = item;
                    }

                    break;
            }
        }
    }
}
