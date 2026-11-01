using Flexarr.Api.Endpoints;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Books;

/// <summary>One of a book's files as a player gets it: as it is, or converted to AAC for a player that can't decode it.</summary>
public sealed record BookPart(int Index, Guid FileId, string Path, double OffsetSeconds, double DurationSeconds, string? Container, string? AudioCodec, long Size, bool Convert);

/// <summary>Playing a book: its files on one timeline, where each profile is in it, and when it's finished.</summary>
public static class BookPlayback
{
    /// <summary>A book counts as finished within its last two minutes, or past 98%, whichever comes first.</summary>
    public const double FinishedWithinSeconds = 120;

    public const double FinishedFraction = 0.98;

    /// <summary>The sound a player that says nothing of its own can play: every browser and phone plays these.</summary>
    public static readonly IReadOnlyList<string> BasicCodecs = ["aac", "mp3"];

    /// <summary>Near the end: past 98%, or (for a book long enough that it isn't most of it) in its last two minutes.</summary>
    public static bool IsFinished(double position, double duration, bool ended) =>
        ended || (duration > 0 && (position >= duration * FinishedFraction
            || (duration >= FinishedWithinSeconds * 10 && position >= duration - FinishedWithinSeconds)));

    /// <summary>
    /// A book's playable files in order, each converted when <paramref name="codecs"/> doesn't include its sound. AAC and
    /// MP3 always count, whatever a player says: every player decodes them, and a player that claimed none would
    /// otherwise set the server converting every file of the book.
    /// </summary>
    public static async Task<List<BookPart>> PartsAsync(FlexarrDbContext db, Guid bookId, IReadOnlyCollection<string> codecs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(codecs);
        codecs = [.. codecs.Concat(BasicCodecs)];
        var files = await db.MediaFiles.AsNoTracking()
            .Where(f => f.MediaItemId == bookId && f.BookOrder != null && f.ProbeError == null && f.MissingSince == null)
            .OrderBy(f => f.BookOrder).ToListAsync(ct);
        var parts = new List<BookPart>(files.Count);
        var offset = 0.0;
        foreach (var file in files)
        {
            var duration = file.DurationSeconds ?? 0;
            parts.Add(new BookPart(parts.Count, file.Id, file.Path, offset, duration, file.Container, file.AudioCodec, file.Size,
                Convert: file.AudioCodec is null || !codecs.Contains(file.AudioCodec, StringComparer.OrdinalIgnoreCase)));
            offset += duration;
        }

        return parts;
    }

    public static async Task<BookListeningDto> ListeningAsync(FlexarrDbContext db, string profileId, CancellationToken ct) =>
        await db.Profiles.Where(p => p.Id == profileId)
            .Select(p => new BookListeningDto(p.AudiobookSpeed, p.AudiobookSkipBackSeconds, p.AudiobookSkipForwardSeconds, p.AudiobookSmartRewind))
            .FirstOrDefaultAsync(ct) is { } listening
            ? listening with
            {
                Speed = SpeedOrDefault(listening.Speed),
                SkipBackSeconds = SkipOrDefault(listening.SkipBackSeconds),
                SkipForwardSeconds = SkipOrDefault(listening.SkipForwardSeconds),
            }
            : new BookListeningDto(1, 30, 30, true);

    /// <summary>
    /// A profile's book speed as it's handed out: a speed no one can choose (0, left by a profile made before
    /// audiobooks, or anything that isn't a number) is 1×.
    /// </summary>
    public static double SpeedOrDefault(double speed) => double.IsFinite(speed) && speed > 0 ? speed : 1;

    /// <summary>The skip steps a profile can choose, for books and podcasts alike.</summary>
    public static readonly int[] SkipSteps = [10, 15, 30, 45, 60];

    /// <summary>A skip step as it's handed out: none chosen yet (0, a profile made before audiobooks) is 30 seconds.</summary>
    public static int SkipOrDefault(int seconds) => seconds > 0 ? seconds : 30;

    /// <summary>
    /// Podcasts and books share one speed. Saved from either side, the book speed takes it to the nearest 0.05×, at most
    /// 3.5×, and the podcast speed takes it as it is.
    /// </summary>
    public static void SetSharedSpeed(Profile profile, double speed)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.PodcastSpeed = speed;
        profile.AudiobookSpeed = Math.Clamp(Math.Round(speed * 20) / 20, 0.5, 3.5);
    }

    public static BookProgressDto Progress(PlaybackState? state) => state is null
        ? new BookProgressDto(0, false, null, null, null)
        : new BookProgressDto(state.PositionSeconds, state.Watched, state.FinishedAt, state.LastPlayedAt, state.Device);

    /// <param name="url">The address of part n: the session's for a player, the item's for a download.</param>
    public static async Task<BookManifestDto> ManifestAsync(FlexarrDbContext db, MediaItem book, string profileId, IReadOnlyList<BookPart> parts,
        Func<BookPart, string> url, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(url);
        var state = await db.PlaybackStates.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == profileId && p.MediaItemId == book.Id, ct);
        var listening = await ListeningAsync(db, profileId, ct);
        var speed = await db.ShowPlaybackSpeeds.Where(s => s.ProfileId == profileId && s.SeriesId == book.Id).Select(s => (double?)s.Speed).FirstOrDefaultAsync(ct);
        return new BookManifestDto(
            book.Id,
            parts.Sum(p => p.DurationSeconds),
            [.. parts.Select(p => new BookFileDto(p.Index, p.FileId, p.OffsetSeconds, p.DurationSeconds, p.Container, p.AudioCodec, p.Size, url(p), p.Convert))],
            await BookQueries.Chapters(db, book.Id).ToListAsync(ct),
            await BookQueries.Bookmarks(db, book.Id, profileId).ToListAsync(ct),
            Progress(state),
            speed is > 0 and var kept ? kept : listening.Speed,
            listening,
            await BookQueries.NextInSeriesAfterAsync(db, book, profileId, ct));
    }

    /// <summary>
    /// Records a place a device reached in a book, unless something newer is recorded already: a phone that listened
    /// offline sends its listening later with the time it happened, and listening elsewhere since wins.
    /// </summary>
    /// <returns>The profile's place in the book afterwards; null if there's no such book.</returns>
    public static async Task<BookProgressDto?> RecordAsync(FlexarrDbContext db, Guid bookId, string profileId, BookProgressRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var duration = await db.BookDetails.Where(d => d.MediaItemId == bookId && d.MediaItem!.Kind == MediaKind.Audiobook)
            .Select(d => (double?)d.DurationSeconds).FirstOrDefaultAsync(ct);
        if (duration is not { } length)
        {
            return null;
        }

        var at = (request.RecordedAt ?? DateTime.UtcNow).ToUniversalTime();
        if (at > DateTime.UtcNow.AddMinutes(5))
        {
            at = DateTime.UtcNow; // A device whose clock runs fast mustn't win against everything that follows.
        }

        for (var attempt = 0; ; attempt++)
        {
            var state = await db.PlaybackStates.FirstOrDefaultAsync(p => p.UserId == profileId && p.MediaItemId == bookId, ct);
            if (state is not null && state.LastPlayedAt > at)
            {
                return Progress(state);
            }

            var added = state is null;
            if (state is null)
            {
                state = new PlaybackState { UserId = profileId, MediaItemId = bookId };
                db.PlaybackStates.Add(state);
            }

            Apply(state, Math.Clamp(request.PositionSeconds, 0, length), length, request.Ended, at, request.Device);
            try
            {
                await db.SaveChangesAsync(ct);
                return Progress(state);
            }
            catch (DbUpdateException) when (added && attempt == 0)
            {
                // Two first reports for the same book raced and the other inserted the row: apply this one to it.
                db.Entry(state).State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// A place in a book onto its state: finished near the end (counting a play, unless it was finished already), or
    /// the place, kept even near the start, since books are listened to in short sittings.
    /// </summary>
    public static void Apply(PlaybackState state, double position, double duration, bool ended, DateTime at, string? device)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.LastPlayedAt = at;
        state.DurationSeconds = duration;
        state.Device = device is { Length: > 0 } d ? (d.Length > 100 ? d[..100] : d) : state.Device;
        if (IsFinished(position, duration, ended))
        {
            if (!state.Watched)
            {
                state.PlayCount++;
                state.FinishedAt = at;
            }

            state.Watched = true;
            state.PositionSeconds = 0;
        }
        else
        {
            state.Watched = false;
            state.PositionSeconds = position;
        }
    }

    /// <summary>Marks a book finished (now) or not; not finished forgets the place too, and when it was finished.</summary>
    public static async Task<bool> SetFinishedAsync(FlexarrDbContext db, Guid bookId, string profileId, bool finished, CancellationToken ct)
    {
        if (!await db.MediaItems.AnyAsync(i => i.Id == bookId && i.Kind == MediaKind.Audiobook, ct))
        {
            return false;
        }

        var state = await db.PlaybackStates.FirstOrDefaultAsync(p => p.UserId == profileId && p.MediaItemId == bookId, ct);
        if (state is null)
        {
            if (!finished)
            {
                return true;
            }

            state = new PlaybackState { UserId = profileId, MediaItemId = bookId };
            db.PlaybackStates.Add(state);
        }

        if (finished && !state.Watched)
        {
            state.PlayCount = Math.Max(1, state.PlayCount + 1);
            state.FinishedAt = DateTime.UtcNow;
        }
        else if (!finished)
        {
            state.FinishedAt = null;
        }

        state.Watched = finished;
        state.PositionSeconds = 0;
        state.LastPlayedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>"Listen again": back to the start, no longer finished, but when it was finished is kept.</summary>
    public static async Task<bool> ListenAgainAsync(FlexarrDbContext db, Guid bookId, string profileId, CancellationToken ct) =>
        await db.MediaItems.AnyAsync(i => i.Id == bookId && i.Kind == MediaKind.Audiobook, ct)
        && (await db.PlaybackStates.Where(p => p.UserId == profileId && p.MediaItemId == bookId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Watched, false).SetProperty(p => p.PositionSeconds, 0).SetProperty(p => p.LastPlayedAt, DateTime.UtcNow), ct) >= 0);
}
