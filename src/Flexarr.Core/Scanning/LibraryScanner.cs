using System.Collections.Concurrent;
using Flexarr.Core.Books;
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
/// <param name="Added">Files new to the library.</param>
/// <param name="Updated">Files read again.</param>
/// <param name="Removed">Files gone for good.</param>
/// <param name="Unchanged">Files as they were.</param>
/// <param name="Unmatched">Files that belong to no title.</param>
/// <param name="ProbeFailures">Files ffprobe couldn't read.</param>
/// <param name="Missing">Files newly missing.</param>
/// <param name="Incomplete">Part of what was asked for couldn't be looked at (a folder missing, looking unmounted, or unreadable).</param>
public sealed record ScanResult(int Added, int Updated, int Removed, int Unchanged, int Unmatched, int ProbeFailures, int Missing = 0, bool Incomplete = false);

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
        var incomplete = false;
        var unreadableFolders = new List<string>();

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
                incomplete = true;
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
                    incomplete = true;
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

            var unreadable = new List<string>();
            var onDisk = MediaFileEnumerator.Enumerate(root, target, audio: library.Type == LibraryType.Music, books: library.Type == LibraryType.Audiobooks,
                    everything: library.Type == LibraryType.HomeVideos, unreadable: unreadable)
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
                    if (match.MissingSince is not null)
                    {
                        // Back after going missing, unchanged or not: its book is put together again with it in.
                        state.ChangedBooks.Add(match.MediaItemId);
                        Track(match);
                    }

                    match.MissingSince = null;
                }

                // Files ffprobe couldn't read are tried again when they change (or when asked), not on every scan. HDR
                // files from before HDR10+ was looked for, Dolby Vision files from before their profile was kept, and videos
                // from before closed captions were looked for, are read again once.
                if (match is not null && match.Size == file.Length && match.ModifiedAt == file.LastWriteTimeUtc
                    && (match.ProbeError is null || !retryFailedProbes)
                    && !(match.Hdr10Plus is null && match.ProbeError is null && match.VideoRange is VideoRange.Hdr10 or VideoRange.DolbyVision)
                    && !(match.DolbyVisionProfile is null && match.ProbeError is null && match.VideoRange == VideoRange.DolbyVision)
                    && !(match.ClosedCaptions is null && match.ProbeError is null && FfprobeMediaProbe.CanCarryCaptions(match.VideoCodec)))
                {
                    unchanged++;
                }
                else
                {
                    work.Add((file, match is null ? null : Track(match)));
                }
            }

            if (library.Type == LibraryType.Music && work.Any(w => w.Existing is null))
            {
                // Lidarr upgrading a track (MP3 to FLAC) or renaming it swaps one file for another: the new file goes to
                // the old track, so it keeps its place in playlists and its plays, rather than a new track taking over
                // while the old one is deleted with them a day later.
                var goneIds = existing.Values.Concat(duplicates).Where(f => !seen.Contains(PathComparison.Key(f.Path)) && !Unreadable(f))
                    .Select(f => f.Id).ToList();
                state.OfferReplacedTracks(await db.MediaItems.IgnoreQueryFilters()
                    .Where(i => i.LibraryId == library.Id && i.Kind == MediaKind.Track && i.ParentId != null
                        && i.Files.Any() && i.Files.All(f => f.MissingSince != null || goneIds.Contains(f.Id)))
                    .ToListAsync(cancellationToken).ConfigureAwait(false));
            }

            progress.Begin(library.Id, target, work.Count);

            // A book's files are read once, chapters and all, and what they say is kept with them for BookAssembler.
            var books = library.Type == LibraryType.Audiobooks;
            var music = library.Type == LibraryType.Music;
            foreach (var batch in work.Chunk(BatchSize))
            {
                var probes = new ConcurrentDictionary<string, (ProbeResult? Result, BookFileProbe? Book, string? Error)>(StringComparer.Ordinal);
                await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = ProbeConcurrency, CancellationToken = cancellationToken }, async (w, ct) =>
                {
                    if (MediaFileEnumerator.ProtectedBookExtensions.Contains(w.File.Extension))
                    {
                        probes[w.File.FullName] = (null, null, MediaFileEnumerator.ProtectedBookError);
                        return;
                    }

                    try
                    {
                        if (books)
                        {
                            var book = await probe.ProbeBookFileAsync(w.File.FullName, ct).ConfigureAwait(false);
                            probes[w.File.FullName] = (book.Result ?? await probe.ProbeAsync(w.File.FullName, ct).ConfigureAwait(false), book, null);
                        }
                        else
                        {
                            probes[w.File.FullName] = (await probe.ProbeAsync(w.File.FullName, ct).ConfigureAwait(false), null, null);
                        }
                    }
                    catch (MediaProbeException ex)
                    {
                        probes[w.File.FullName] = (null, null, ex.Message);
                    }
                }).ConfigureAwait(false);

                // One query for the streams being replaced, rather than one per file.
                var changedIds = batch.Where(w => w.Existing is not null).Select(w => w.Existing!.Id).ToList();
                if (changedIds.Count > 0)
                {
                    await db.MediaStreams.Where(s => changedIds.Contains(s.MediaFileId)).LoadAsync(cancellationToken).ConfigureAwait(false);
                }

                if (music)
                {
                    await state.LoadAlbumsOfAsync(library.Id, root, batch.Where(w => w.Existing is null).Select(w => (w.File.FullName, probes[w.File.FullName].Result)), cancellationToken).ConfigureAwait(false);
                }

                foreach (var (file, match) in batch)
                {
                    var (result, bookProbe, error) = probes[file.FullName];
                    if (error is not null && error != MediaFileEnumerator.ProtectedBookError)
                    {
                        failures++;
                        LogProbeFailed(PathComparison.Printable(file.FullName), PathComparison.Printable(error));
                    }

                    if (match is not null)
                    {
                        Apply(match, file, result, error, music);
                        match.BookProbe = bookProbe?.ToJson();
                        state.ChangedBooks.Add(match.MediaItemId);
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

                    if (item.Kind == MediaKind.Audiobook)
                    {
                        state.ChangedBooks.Add(item.Id);
                    }

                    var mediaFile = new MediaFile { Path = file.FullName, MediaItemId = item.Id, BookProbe = bookProbe?.ToJson() };
                    Apply(mediaFile, file, result, error, music);
                    db.MediaFiles.Add(mediaFile);
                    added++;
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                progress.Advance(batch.Length);
            }

            // Files in a folder that couldn't be read weren't seen, but that's no sign they've gone.
            var gone = existing.Values.Where(f => !seen.Contains(PathComparison.Key(f.Path))).Concat(duplicates)
                .Where(f => f.MissingSince is null && !Unreadable(f))
                .ToList();
            if (unreadable.Count > 0)
            {
                LogUnreadable(root, unreadable.Count, PathComparison.Printable(unreadable[0]));
            }

            unreadableFolders.AddRange(unreadable);

            // A root-only lost+found or NAS recycle bin is no sign of trouble, and never was the library's: anything else
            // may be hiding files whose grace has run out, which are no more gone than the rest.
            if (unreadable.Any(d => !MediaFileEnumerator.IsInNotMedia(root, d)))
            {
                held = true;
                incomplete = true;
            }

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
                    Track(file).MissingSince = now;
                    state.ChangedBooks.Add(file.MediaItemId);
                    missing++;
                }
            }

            if (full && !suspicious && folder is not null && (folder.Hold is not null || folder.ReleasedAt is not null))
            {
                Clear(folder, releasedAt: null); // Looks normal again.
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            bool Unreadable(MediaFile file) => unreadable.Any(d => PathComparison.IsUnder(file.Path, d));
        }

        if (!held)
        {
            removed = await RemoveExpiredAsync(library.Id, roots, scope is null ? null : targets.Select(t => t.Scope).ToList(), unreadableFolders, cancellationToken).ConfigureAwait(false);
        }

        await PruneEmptyItemsAsync(db, library.Id, cancellationToken).ConfigureAwait(false);
        await MarkMissingItemsAsync(library.Id, cancellationToken).ConfigureAwait(false);
        if (library.Type == LibraryType.HomeVideos)
        {
            await NumberHomeVideosAsync(library.Id, cancellationToken).ConfigureAwait(false);
        }

        if (library.Type == LibraryType.Audiobooks)
        {
            if (state.ChangedBooks.Count > 0)
            {
                await new BookAssembler(db, probe, logger).AssembleAsync(state.ChangedBooks, cancellationToken).ConfigureAwait(false);
            }

            await BookCarryOver.CarryOverAsync(db, library.Id, cancellationToken).ConfigureAwait(false);
        }

        // Ids from folder names are known now; those from Radarr/Sonarr after the metadata refresh, which does this again.
        await WatchHistoryCarryOver.CarryOverAsync(db, cancellationToken).ConfigureAwait(false);
        await NoticeReplacedArtworkAsync(library.Id, scope is null ? null : targets.Select(t => t.Scope).ToList(), cancellationToken).ConfigureAwait(false);

        library.LastScannedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new ScanResult(added, updated, removed, unchanged, unmatched, failures, missing, incomplete);
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
    /// A known file the scan is about to change, tracked so the change is saved. Known files are read untracked: most
    /// are unchanged, and tracking every one of a big library held them all, twice over, for the whole scan.
    /// </summary>
    private MediaFile Track(MediaFile file)
    {
        if (db.Entry(file).State == EntityState.Detached)
        {
            db.MediaFiles.Attach(file);
        }

        return file;
    }

    /// <summary>
    /// The library's files at or under <paramref name="target"/>, missing ones included, untracked (see
    /// <see cref="Track"/>), except those the context already tracks, which are given as tracked. When the target isn't on disk
    /// (a deleted series folder) it keeps the webhook's spelling, which may not be how its files were stored ("é"
    /// composed, say, where the files have it decomposed): then they're matched by <see cref="PathComparison.Key"/>
    /// among the files of the nearest folder that is there.
    /// </summary>
    private async Task<List<MediaFile>> KnownFilesAsync(Guid libraryId, string root, string target, CancellationToken ct)
    {
        var files = db.MediaFiles.IgnoreQueryFilters().AsNoTracking().Where(f => f.MediaItem!.LibraryId == libraryId);
        if (Directory.Exists(target) || File.Exists(target))
        {
            return Tracked(await files.Where(f => f.Path == target || f.Path.StartsWith(target + "/")).ToListAsync(ct).ConfigureAwait(false));
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
        return ids.Count == 0 ? [] : Tracked(await files.Where(f => ids.Contains(f.Id)).ToListAsync(ct).ConfigureAwait(false));

        // A file an earlier folder of the scan changed (one library folder inside another) is the tracked one.
        List<MediaFile> Tracked(List<MediaFile> loaded)
        {
            if (!db.ChangeTracker.Entries<MediaFile>().Any())
            {
                return loaded;
            }

            var local = db.MediaFiles.Local;
            return loaded.Select(f => local.FindEntry(f.Id)?.Entity ?? f).ToList();
        }
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

    /// <param name="music">A track: its ReplayGain is kept from the tags just read, so playing it needn't read them again.</param>
    private static void Apply(MediaFile target, FileInfo file, ProbeResult? result, string? error, bool music)
    {
        if (target.Size != file.Length || target.ModifiedAt != file.LastWriteTimeUtc)
        {
            target.AudioDelaySeconds = 0; // A new copy of the file needn't be out of sync the way the old one was.
        }

        target.Size = file.Length;
        target.ModifiedAt = file.LastWriteTimeUtc;
        target.ProbeError = error;
        target.Streams.Clear();
        target.Loudness = music && result is not null ? ReplayGain.FromTags(result.Tags).ToJson() : null;

        if (result is null)
        {
            return;
        }

        target.Container = result.Container;
        target.DurationSeconds = result.DurationSeconds;
        target.Bitrate = result.Bitrate;
        target.VideoRange = result.VideoRange;
        target.Hdr10Plus = result.Hdr10Plus;
        target.DolbyVisionProfile = result.DolbyVisionProfile;
        target.ClosedCaptions = result.ClosedCaptions;
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

    /// <summary>
    /// A cover.jpg or poster.jpg replaced beside the files, or a file whose own artwork is read (embedded://) retagged:
    /// each title's local artwork sources are stamped by path, when they last changed and size, and a stamp different
    /// from last time's sets <see cref="MediaItem.ArtworkChangedAt"/>. Artwork addresses carry it, so apps and browsers
    /// fetch the new picture rather than keep the one they cached. A replacement keeping an older date (a Finder copy)
    /// still counts. A first stamp is only recorded, and <see cref="MediaItem.MetadataUpdatedAt"/> is never touched:
    /// that says when the title was last looked up. A scan of part of the library (a webhook's) only looks at artwork
    /// in the folders it scanned, rather than every artwork file in the library.
    /// </summary>
    private async Task NoticeReplacedArtworkAsync(Guid libraryId, IReadOnlyList<string>? within, CancellationToken ct)
    {
        var local = await db.MediaItems
            .Where(i => i.LibraryId == libraryId
                && ((i.PosterUrl != null && (i.PosterUrl.StartsWith("file://") || i.PosterUrl.StartsWith("embedded://")))
                    || (i.BackdropUrl != null && (i.BackdropUrl.StartsWith("file://") || i.BackdropUrl.StartsWith("embedded://")))
                    || (i.LogoUrl != null && (i.LogoUrl.StartsWith("file://") || i.LogoUrl.StartsWith("embedded://")))
                    || (i.ThumbUrl != null && (i.ThumbUrl.StartsWith("file://") || i.ThumbUrl.StartsWith("embedded://")))))
            .Select(i => new { i.Id, i.ArtworkStamp, Urls = new[] { i.PosterUrl, i.BackdropUrl, i.LogoUrl, i.ThumbUrl } })
            .ToListAsync(ct).ConfigureAwait(false);
        var changed = local.Where(i => within is null || i.Urls.Any(u => LocalPath(u) is { } path && within.Any(w => PathComparison.IsUnder(path, w))))
            .Select(i => (i.Id, Old: i.ArtworkStamp, New: ArtworkStamp(i.Urls)))
            .Where(i => i.New != i.Old)
            .ToDictionary(i => i.Id, i => (i.Old, i.New));
        if (changed.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var chunk in changed.Keys.Chunk(500))
        {
            var items = await db.MediaItems.Where(i => chunk.Contains(i.Id)).ToListAsync(ct).ConfigureAwait(false);
            foreach (var item in items)
            {
                var (old, stamp) = changed[item.Id];
                if (old is not null)
                {
                    item.ArtworkChangedAt = now;
                }

                item.ArtworkStamp = stamp;
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The <see cref="MediaItem.ArtworkStamp"/> of these artwork addresses: their local files' paths, dates and sizes.</summary>
    internal static string ArtworkStamp(IEnumerable<string?> urls)
    {
        var text = new System.Text.StringBuilder();
        foreach (var url in urls)
        {
            var path = LocalPath(url);
            if (path is null)
            {
                continue;
            }

            var file = new FileInfo(path);
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            text.Append(url).Append('\n')
                .Append(file.Exists ? file.LastWriteTimeUtc.Ticks.ToString(invariant) : "-").Append('\n')
                .Append(file.Exists ? file.Length.ToString(invariant) : "-").Append('\n');
        }

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    /// <summary>The file a local artwork address (file:// or embedded://) reads; null for any other.</summary>
    private static string? LocalPath(string? url) =>
        url is null ? null
        : url.StartsWith("file://", StringComparison.Ordinal) ? url["file://".Length..]
        : url.StartsWith("embedded://", StringComparison.Ordinal) ? url["embedded://".Length..]
        : null;

    /// <summary>
    /// Removes files missing for longer than <see cref="MissingGracePeriod"/>, if this scan could have seen them come
    /// back: not in a folder it couldn't read, or in a part of the library it didn't look at (that part may be unreadable,
    /// or unmounted, for all it knows; its own scan will see to them). A file under none of the library's folders has no
    /// scan of its own.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="roots">The library's folders.</param>
    /// <param name="scanned">What this scan was limited to; null for all of it.</param>
    /// <param name="unreadable">Folders it couldn't read.</param>
    /// <param name="ct">Stops it.</param>
    private async Task<int> RemoveExpiredAsync(Guid libraryId, List<string> roots, List<string>? scanned, List<string> unreadable, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - MissingGracePeriod;
        var expired = db.MediaFiles.IgnoreQueryFilters().Where(f => f.MissingSince != null && f.MissingSince < cutoff && f.MediaItem!.LibraryId == libraryId);
        if (scanned is null && unreadable.Count == 0)
        {
            return await expired.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        var ids = (await expired.Select(f => new { f.Id, f.Path }).ToListAsync(ct).ConfigureAwait(false))
            .Where(f => !unreadable.Any(d => PathComparison.IsUnder(f.Path, d))
                && (scanned is null || scanned.Any(s => PathComparison.IsUnder(f.Path, s)) || !roots.Any(r => PathComparison.IsUnder(f.Path, r))))
            .Select(f => f.Id).ToList();
        var removed = 0;
        foreach (var chunk in ids.Chunk(500))
        {
            removed += await db.MediaFiles.IgnoreQueryFilters().Where(f => chunk.Contains(f.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        return removed;
    }

    /// <summary>
    /// Hides playable items whose files are all missing, then the seasons/albums and series/artists with nothing left
    /// to show; and brings back any whose files have reappeared.
    /// </summary>
    private async Task MarkMissingItemsAsync(Guid libraryId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var items = db.MediaItems.IgnoreQueryFilters().Where(i => i.LibraryId == libraryId);

        var playable = items.Where(i => i.Kind == MediaKind.Movie || i.Kind == MediaKind.Episode || i.Kind == MediaKind.Track || i.Kind == MediaKind.Audiobook);
        await playable.Where(i => i.MissingSince == null && i.Files.All(f => f.MissingSince != null))
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.MissingSince, now), ct).ConfigureAwait(false);
        await playable.Where(i => i.MissingSince != null && i.Files.Any(f => f.MissingSince == null))
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.MissingSince, (DateTime?)null), ct).ConfigureAwait(false);

        await MarkContainersAsync(items.Where(i => i.Kind == MediaKind.Season || i.Kind == MediaKind.Album)).ConfigureAwait(false);
        // A followed artist waiting for their first album has no children to go by: it stays as it is.
        await MarkContainersAsync(items.Where(i => i.Kind == MediaKind.Series || (i.Kind == MediaKind.Artist && !i.Placeholder))).ConfigureAwait(false);

        async Task MarkContainersAsync(IQueryable<MediaItem> containers)
        {
            await containers.Where(i => i.MissingSince == null && i.Children.All(c => c.MissingSince != null))
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.MissingSince, now), ct).ConfigureAwait(false);
            await containers.Where(i => i.MissingSince != null && i.Children.Any(c => c.MissingSince == null))
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.MissingSince, (DateTime?)null), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Removes playable items without files, then the albums/seasons and artists/series left empty.</summary>
    /// <summary>
    /// Home videos are kept by their folders' and files' paths; their season and episode numbers only put them in
    /// order, so they're given again after every scan: folders and videos by name, with numbers in names counted as
    /// numbers ("Part 2" before "Part 10"). Videos straight in a show's folder come first.
    /// </summary>
    private async Task NumberHomeVideosAsync(Guid libraryId, CancellationToken ct)
    {
        var order = StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture, System.Globalization.CompareOptions.NumericOrdering | System.Globalization.CompareOptions.IgnoreCase);
        var items = await db.MediaItems.IgnoreQueryFilters()
            .Where(i => i.LibraryId == libraryId && (i.Kind == MediaKind.Series || i.Kind == MediaKind.Season || i.Kind == MediaKind.Episode))
            .ToListAsync(ct).ConfigureAwait(false);
        var children = items.Where(i => i.ParentId is not null).ToLookup(i => i.ParentId!.Value);
        foreach (var series in items.Where(i => i.Kind == MediaKind.Series))
        {
            var seasons = children[series.Id].OrderBy(s => s.Path == series.Path ? 0 : 1).ThenBy(s => s.Title, order).ToList();
            for (var s = 0; s < seasons.Count; s++)
            {
                var season = seasons[s];
                season.SeasonNumber = s + 1;
                season.SortTitle = SortTitle.ForNumber(s + 1);
                var videos = children[season.Id].OrderBy(e => Path.GetFileName(e.Path) ?? e.Title, order).ToList();
                for (var e = 0; e < videos.Count; e++)
                {
                    videos[e].SeasonNumber = s + 1;
                    videos[e].EpisodeNumber = e + 1;
                    videos[e].SortTitle = SortTitle.ForNumber(e + 1);
                }
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a library's playable items left without files, then the seasons/albums and series/artists left empty
    /// (their history kept a while, by id, as a scan does).
    /// </summary>
    public static async Task PruneEmptyItemsAsync(FlexarrDbContext db, Guid libraryId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        // Missing files still count: their items wait out the grace period with them.
        // Their history and collections are kept a while, by id, in case the title comes back under a new path.
        var items = db.MediaItems.IgnoreQueryFilters().Where(i => i.LibraryId == libraryId);
        var playable = items.Where(i => (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Episode || i.Kind == MediaKind.Track || i.Kind == MediaKind.Audiobook) && !i.Files.Any());
        await WatchHistoryCarryOver.BuryAsync(db, playable, ct).ConfigureAwait(false);
        await playable.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await items.Where(i => (i.Kind == MediaKind.Season || i.Kind == MediaKind.Album) && !i.Children.Any()).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        // A placeholder whose artist turned up as a separate artist after all (by MusicBrainz id) is no longer needed.
        var placeholders = items.Where(i => i.Kind == MediaKind.Artist && i.Placeholder && !i.Children.Any() && i.MusicBrainzId != null);
        await placeholders.Where(p => items.Any(o => o.Kind == MediaKind.Artist && !o.Placeholder && o.MusicBrainzId == p.MusicBrainzId))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        // An artist's first album arriving makes a followed artist's placeholder an ordinary artist.
        await items.Where(i => i.Kind == MediaKind.Artist && i.Placeholder && i.Children.Any())
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.Placeholder, false), ct).ConfigureAwait(false);
        var containers = items.Where(i => (i.Kind == MediaKind.Series || (i.Kind == MediaKind.Artist && !i.Placeholder)) && !i.Children.Any());
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} folders in {Root} couldn't be read (first: {Folder}); keeping their files as they were")]
    private partial void LogUnreadable(string root, int count, string folder);

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
            var folders = scoped?.Select(t => SeriesFolder(t.Root, t.Scope)).ToList();
            var aliases = library.Type == LibraryType.Music
                ? await db.MusicAliases.AsNoTracking().Where(a => a.LibraryId == library.Id).ToListAsync(ct).ConfigureAwait(false)
                : [];

            // A scan of one title only needs that title's parents: not every film, or every album, tracked for it. Told
            // apart here in C#, by the paths alone, since paths compare by PathComparison's rules, not SQLite's.
            var parentItems = items.Where(i => i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series || i.Kind == MediaKind.Artist || i.Kind == MediaKind.Album || i.Kind == MediaKind.Audiobook);
            if (scoped is not null && folders is not null && folders.All(f => f is not null))
            {
                var roots = scoped.Select(t => PathComparison.Key(t.Root)).ToHashSet(StringComparer.Ordinal);
                var merged = aliases.Select(a => a.ItemId).ToHashSet();
                var wanted = (await parentItems.AsNoTracking().Select(i => new { i.Id, i.Kind, i.Path }).ToListAsync(ct).ConfigureAwait(false))
                    // Any artist: the tags of a file here can name one from anywhere (see LoadAlbumsOfAsync for their albums).
                    .Where(i => i.Kind == MediaKind.Artist || merged.Contains(i.Id)
                        || (i.Path is not null && (roots.Contains(PathComparison.Key(i.Path)) || folders.Any(f => PathComparison.IsUnder(i.Path, f!)))))
                    .Select(i => i.Id).ToList();
                parentItems = parentItems.Where(i => wanted.Contains(i.Id));
                index._albumsLoadedFor = [];
            }

            var parents = await parentItems.ToListAsync(ct).ConfigureAwait(false);
            parents.ForEach(index.Track);
            if (library.Type == LibraryType.Music)
            {
                index._genres = await db.Genres.ToDictionaryAsync(g => g.Name, StringComparer.OrdinalIgnoreCase, ct).ConfigureAwait(false);

                // Artists and albums merged into others (in Library health): tracks the merged one would have had go to
                // the one kept. Real names and folders come first.
                var byId = parents.ToDictionary(p => p.Id);
                foreach (var alias in aliases)
                {
                    if (byId.TryGetValue(alias.ItemId, out var target))
                    {
                        if (alias.Key.StartsWith(MusicKeys.PathPrefix, StringComparison.Ordinal))
                        {
                            index._byPath.TryAdd(alias.Key[MusicKeys.PathPrefix.Length..], target);
                        }
                        else
                        {
                            index._byKey.TryAdd(alias.Key, target);
                        }
                    }
                }
            }

            if (library.Type is not (LibraryType.TvShows or LibraryType.HomeVideos))
            {
                return index;
            }

            // Untracked: a scan only finds parents among them and never changes them, and tracking every season and
            // episode of a big library held them all, twice over, for the whole scan.
            var seasons = items.AsNoTracking().Where(i => i.Kind == MediaKind.Season);
            var episodes = items.AsNoTracking().Where(i => i.Kind == MediaKind.Episode);
            if (folders is not null && folders.All(f => f is not null))
            {
                // A home video straight in the library folder has that folder for its show, whatever the scan's scope.
                var homes = scoped!.Select(t => PathComparison.Key(t.Root)).ToHashSet(StringComparer.Ordinal);
                var seriesIds = parents.Where(i => i.Kind == MediaKind.Series && i.Path is not null && (folders.Contains(PathComparison.Key(i.Path)) || homes.Contains(PathComparison.Key(i.Path))))
                    .Select(i => i.Id).ToList();
                seasons = seasons.Where(i => i.ParentId != null && seriesIds.Contains(i.ParentId.Value));
                episodes = episodes.Where(i => i.Parent!.ParentId != null && seriesIds.Contains(i.Parent.ParentId.Value));
            }

            (await seasons.ToListAsync(ct).ConfigureAwait(false)).ForEach(index.Track);
            (await episodes.ToListAsync(ct).ConfigureAwait(false)).ForEach(index.Track);
            return index;
        }

        /// <summary>Artists whose albums have been loaded, in a scan of part of the library; null when they all were.</summary>
        private HashSet<Guid>? _albumsLoadedFor;

        /// <summary>
        /// In a scan of part of a music library, the albums of the artists these new files' tags name: an album can be in
        /// a folder elsewhere, and must not be made again.
        /// </summary>
        public async Task LoadAlbumsOfAsync(Guid libraryId, string root, IEnumerable<(string Path, ProbeResult? Probe)> files, CancellationToken ct)
        {
            if (_albumsLoadedFor is null)
            {
                return;
            }

            var artists = new List<Guid>();
            foreach (var (path, probe) in files)
            {
                var artist = MusicTags.Read(probe?.Tags ?? new Dictionary<string, string>(), root, path).Artist;
                if (_byKey.TryGetValue($"artist:{Fold(artist)}", out var known) && _albumsLoadedFor.Add(known.Id))
                {
                    artists.Add(known.Id);
                }
            }

            if (artists.Count > 0)
            {
                (await db.MediaItems.IgnoreQueryFilters()
                    .Where(i => i.LibraryId == libraryId && i.Kind == MediaKind.Album && i.ParentId != null && artists.Contains(i.ParentId.Value))
                    .ToListAsync(ct).ConfigureAwait(false)).ForEach(Track);
            }
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
                LibraryType.HomeVideos => ResolveHomeVideo(library, root, filePath, segments),
                LibraryType.Audiobooks => ResolveBook(library, root, filePath),
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
            if (artist is null && !_byKey.TryGetValue(artistKey, out artist) && Placeholder(library.Id, info.ArtistMusicBrainzId, info.Artist) is { } waiting)
            {
                // A followed artist's first album: theirs, whatever spelling the tags use ("Beyonce" for "Beyoncé").
                artist = waiting;
                _byKey[artistKey] = waiting;
            }

            if (artist is null)
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

                // A new album takes its first track's genre tags: its Genres view then works without Lidarr (whose
                // genres replace these when it describes the album).
                foreach (var name in info.Genres)
                {
                    if (!_genres.TryGetValue(name, out var genre))
                    {
                        genre = new Genre { Name = name };
                        db.Genres.Add(genre);
                        _genres[name] = genre;
                    }

                    album.Genres.Add(genre);
                }
            }
            else if (album.PosterUrl is null && probe?.HasCoverArt == true)
            {
                album.PosterUrl = $"embedded://{filePath}";
            }

            artist.PosterUrl ??= album.PosterUrl; // No artist photo: show their first album's cover.

            var trackArtist = info.TrackArtist is { } tagged && !IsNamed(artist, tagged) ? tagged : null;
            if (TakeReplaced(album.Id, info.Disc, info.Track, info.Title) is { } replaced)
            {
                replaced.Title = info.Title;
                replaced.SortTitle = SortTitle.ForNumber(((info.Disc ?? 1) * 1000) + (info.Track ?? 0));
                replaced.Year = info.Year;
                replaced.DiscNumber = info.Disc;
                replaced.TrackNumber = info.Track;
                replaced.TrackArtist = trackArtist;
                replaced.Path = filePath;
                return replaced;
            }

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
                TrackArtist = trackArtist,
                Path = filePath,
            });
        }

        private readonly Dictionary<Guid, List<MediaItem>> _replaced = [];

        /// <summary>
        /// Tracks whose files have all gone (this scan or before): a new file of the same album, disc and track
        /// number (or, untracked, the same title) is taken to replace theirs.
        /// </summary>
        public void OfferReplacedTracks(IEnumerable<MediaItem> tracks)
        {
            foreach (var track in tracks)
            {
                var pool = _replaced.TryGetValue(track.ParentId!.Value, out var list) ? list : _replaced[track.ParentId.Value] = [];
                if (!pool.Any(t => t.Id == track.Id))
                {
                    pool.Add(track);
                }
            }
        }

        private MediaItem? TakeReplaced(Guid albumId, int? disc, int? number, string title)
        {
            if (!_replaced.TryGetValue(albumId, out var pool))
            {
                return null;
            }

            var match = number is { } n
                ? pool.FirstOrDefault(t => t.TrackNumber == n && (t.DiscNumber ?? 1) == (disc ?? 1))
                : pool.FirstOrDefault(t => t.TrackNumber is null && Fold(t.Title) == Fold(title));
            if (match is not null)
            {
                pool.Remove(match);
            }

            return match;
        }

        /// <summary>Books whose files were added, changed or went missing in this scan: <see cref="BookAssembler"/> puts them together after.</summary>
        public HashSet<Guid> ChangedBooks { get; } = [];

        /// <summary>
        /// The book <paramref name="filePath"/> is part of, by its folder (see <see cref="BookLayout"/>). Only its path is
        /// read here; <see cref="BookAssembler"/> reads its tags, sidecars and chapters once all its files are in.
        /// </summary>
        private MediaItem ResolveBook(Library library, string root, string filePath)
        {
            var place = BookLayout.Locate(root, filePath, AudioFilesIn);
            if (_byPath.TryGetValue(PathComparison.Key(place.Key), out var book))
            {
                return book;
            }

            return Add(new MediaItem
            {
                LibraryId = library.Id,
                Kind = MediaKind.Audiobook,
                Title = place.Title,
                SortTitle = SortTitle.For(place.Title),
                Year = place.Year,
                Path = place.Key,
                Book = new BookDetails { Asin = place.Asin },
            });
        }

        private readonly Dictionary<string, IReadOnlyCollection<string>> _audioFiles = new(StringComparer.Ordinal);

        private IReadOnlyCollection<string> AudioFilesIn(string directory)
        {
            if (!_audioFiles.TryGetValue(directory, out var files))
            {
                files = MediaFileEnumerator.Enumerate(directory, directory, everything: true, books: true)
                    .Where(f => Path.GetDirectoryName(f.FullName) == directory).Select(f => f.Name).ToList();
                _audioFiles[directory] = files;
            }

            return files;
        }

        private static bool IsNamed(MediaItem artist, string name) =>
            MusicTags.SameArtist(artist.Title, name) || (artist.OriginalTitle is { } original && MusicTags.SameArtist(original, name));

        private static string Fold(string value) => MusicKeys.Fold(value);

        /// <summary>
        /// A followed artist's placeholder in this library, by MusicBrainz id, else by a loose name (accents, case,
        /// punctuation, "&amp;"/"and" and a leading "The" ignored); null when none matches.
        /// </summary>
        private MediaItem? Placeholder(Guid libraryId, string? musicBrainzId, string name)
        {
            var candidates = _artistsById.Values.Where(a => a.Placeholder && a.LibraryId == libraryId).ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            var loose = Loose(name);
            var hasId = musicBrainzId is { Length: > 0 };
            // Known ids that differ are different artists, whatever their names (two bands called Nirvana).
            return (hasId ? candidates.FirstOrDefault(a => a.MusicBrainzId == musicBrainzId) : null)
                ?? candidates.FirstOrDefault(a => loose.Length > 0 && Loose(a.Title) == loose && !(hasId && a.MusicBrainzId is { Length: > 0 } && a.MusicBrainzId != musicBrainzId));
        }

        private static string Loose(string value)
        {
            // A leading "The" as a word (not "Them" or "Theo"), before spaces and punctuation go.
            var trimmed = value.Trim();
            if (trimmed.Length > 4 && trimmed.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[4..];
            }

            var decomposed = trimmed.Normalize(System.Text.NormalizationForm.FormD);
            var letters = new System.Text.StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (char.IsLetterOrDigit(c))
                {
                    letters.Append(char.ToLowerInvariant(c));
                }
                else if (c == '&' || c == '+')
                {
                    letters.Append("and");
                }
            }

            return letters.ToString();
        }

        private readonly Dictionary<string, MediaItem> _byKey = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, MediaItem> _artistsById = [];
        private Dictionary<string, Genre> _genres = new(StringComparer.OrdinalIgnoreCase);

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

        /// <summary>
        /// A home video: the folder it's in under the library's folder is its "show" (or the library's folder itself,
        /// for videos straight in it), the folder under that its "season" (with any deeper folders in it; the show's own
        /// folder for videos straight in it), and the video an "episode" named after its file. Numbers are only
        /// provisional: <see cref="NumberHomeVideosAsync"/> puts them in order once the scan is done.
        /// </summary>
        private MediaItem ResolveHomeVideo(Library library, string root, string filePath, string[] segments)
        {
            var seriesPath = segments.Length > 1 ? Path.Combine(root, segments[0]) : root.TrimEnd(Path.DirectorySeparatorChar);
            if (!_byPath.TryGetValue(PathComparison.Key(seriesPath), out var series))
            {
                var name = Path.GetFileName(seriesPath) is { Length: > 0 } folder ? folder : library.Name;
                series = Add(new MediaItem
                {
                    LibraryId = library.Id,
                    Kind = MediaKind.Series,
                    Title = name,
                    SortTitle = SortTitle.For(name),
                    Path = seriesPath,
                });
            }

            var seasonPath = segments.Length > 2 ? Path.Combine(seriesPath, segments[1]) : seriesPath;
            if (!_byKey.TryGetValue($"season:{PathComparison.Key(seasonPath)}", out var season))
            {
                var number = NextNumber(series.Id);
                season = Add(new MediaItem
                {
                    LibraryId = library.Id,
                    ParentId = series.Id,
                    Kind = MediaKind.Season,
                    Title = seasonPath == seriesPath ? series.Title : Path.GetFileName(seasonPath),
                    SortTitle = SortTitle.ForNumber(number),
                    SeasonNumber = number,
                    Path = seasonPath,
                });
            }

            if (_byKey.TryGetValue($"video:{PathComparison.Key(filePath)}", out var video))
            {
                return video;
            }

            var episode = NextNumber(season.Id);
            var title = Path.GetFileNameWithoutExtension(filePath).Trim();
            return Add(new MediaItem
            {
                LibraryId = library.Id,
                ParentId = season.Id,
                Kind = MediaKind.Episode,
                Title = title.Length > 0 ? title : Path.GetFileName(filePath),
                SortTitle = SortTitle.ForNumber(episode),
                SeasonNumber = season.SeasonNumber,
                EpisodeNumber = episode,
                Path = filePath,
            });
        }

        private readonly Dictionary<Guid, int> _highest = [];

        private int NextNumber(Guid parent) => _highest.GetValueOrDefault(parent) + 1;

        private MediaItem Add(MediaItem item)
        {
            item.AddedAt = _addedAt;
            item.FirstSeenAt = DateTime.UtcNow;
            db.MediaItems.Add(item);
            Track(item);
            return item;
        }

        private void Number(Guid parent, int number) => _highest[parent] = Math.Max(_highest.GetValueOrDefault(parent), number);

        private void Track(MediaItem item)
        {
            switch (item.Kind)
            {
                case MediaKind.Movie or MediaKind.Series or MediaKind.Audiobook when item.Path is not null:
                    _byPath[PathComparison.Key(item.Path)] = item;
                    break;
                case MediaKind.Season when item.ParentId is { } p && item.SeasonNumber is { } s:
                    _byNumber[(p, s)] = item;
                    Number(p, s);
                    if (item.Path is not null)
                    {
                        _byKey[$"season:{PathComparison.Key(item.Path)}"] = item; // a home video folder
                    }

                    break;
                case MediaKind.Episode when item.ParentId is { } p && EpisodeKey(item.EpisodeNumber, item.AirDate) is { } key:
                    _byNumber[(p, key)] = item;
                    Number(p, key);
                    if (item.Path is not null)
                    {
                        _byKey[$"video:{PathComparison.Key(item.Path)}"] = item; // a home video
                    }

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
