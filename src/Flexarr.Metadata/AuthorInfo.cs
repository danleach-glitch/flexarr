using Flexarr.Core.Books;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Flexarr.Metadata.Books;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Flexarr.Metadata;

/// <summary>A photo or biography an author could have, and where it's from (one of <see cref="PersonSources"/>).</summary>
public sealed record AuthorCandidate(string Source, string? Image, string? Biography);

/// <summary>
/// Authors' photos and biographies. A photo in the author's own folder (author.jpg, folder.jpg) wins over any found
/// online; what's still missing comes from Readarr, then Audible (by ASIN, then by name), then Open Library, and only
/// fills gaps. An author is looked up online at most once a month, and one an admin fixed by hand
/// (<see cref="Person.InfoLocked"/>) never is.
/// </summary>
public sealed partial class AuthorInfo(FlexarrDbContext db, ArrClient arr, IMetadataSettings settings, BookProviders? providers, ILogger logger)
{
    /// <summary>How long an author's online lookup stands before they're looked up again.</summary>
    public static readonly TimeSpan LookAgainAfter = TimeSpan.FromDays(30);

    /// <summary>The source of a candidate that's what the person has now, from before sources were kept.</summary>
    public const string Current = "current";

    private static readonly string[] PhotoNames = ["author", "folder"];
    private static readonly string[] PhotoExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    /// <summary>The books an author is credited on, as finding their photo and biography needs them.</summary>
    private sealed record AuthorBook(string Title, string? Path, Guid LibraryId);

    /// <summary>
    /// Every credited author of a book just refreshed. <paramref name="readarr"/> are the authors the Readarr-compatible
    /// apps know (one of the same name is the author's), <paramref name="matched"/> the author of the app's book it was
    /// matched to (taken for a book's only author when the names don't agree: "Sir Terry Pratchett" for "Terry
    /// Pratchett"; with several, it's no telling whose it is), and <paramref name="knownAsins"/> the authors'
    /// ASINs an online match listed.
    /// </summary>
    public async Task FillBookAsync(MediaItem book, IReadOnlyList<ReadarrAuthor> readarr, ReadarrAuthor? matched, IReadOnlyCollection<string> knownAsins, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(readarr);
        var authors = book.Credits.Where(c => c.Role == CreditRole.Author && c.Person is not null).OrderBy(c => c.Order).Select(c => c.Person!).Distinct().ToList();
        for (var i = 0; i < authors.Count; i++)
        {
            var person = authors[i];
            var fromArr = readarr.FirstOrDefault(a => a.AuthorName is { } name && Fold(name) == Fold(person.Name))
                ?? (authors.Count == 1 ? matched : null);
            await FillAsync(person, fromArr, knownAsins, new AuthorBook(book.Title, book.Path, book.LibraryId), force: false, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One person, now (an admin unlocked them): their folder, then online, whenever they were last looked up. Readarr's
    /// authors are asked for; an app that doesn't answer is skipped.
    /// </summary>
    public async Task RefreshPersonAsync(Person person, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(person);
        var readarr = await ReadarrAuthorsAsync(ct).ConfigureAwait(false);
        var fromArr = readarr.FirstOrDefault(a => a.AuthorName is { } name && Fold(name) == Fold(person.Name));
        await FillAsync(person, fromArr, [], null, force: true, ct).ConfigureAwait(false);
    }

    private async Task FillAsync(Person person, ReadarrAuthor? fromArr, IReadOnlyCollection<string> knownAsins, AuthorBook? current, bool force, CancellationToken ct)
    {
        if (person.InfoLocked)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var books = await BooksAsync(person, current, ct).ConfigureAwait(false);

        // Their own folder's photo, looked for every time (it's only a look on disk).
        var (local, sure) = await FolderPhotoAsync(person.Name, books, ct).ConfigureAwait(false);
        if (local is not null && (PersonSources.PhotoReplaceable(person) || person.PhotoSource == PersonSources.Folder))
        {
            var stamp = FileStamp(local);
            if (person.ImageUrl != local)
            {
                PersonSources.SetPhoto(person, local, PersonSources.Folder, now);
            }
            else if (stamp != person.ImageStamp)
            {
                person.ImageChangedAt = now; // the same file, changed (or never stamped): a new ?v=
            }

            person.ImageStamp = stamp;
        }
        else if (local is null && sure && person.PhotoSource == PersonSources.Folder)
        {
            // Taken away (not just out of reach, a share that's offline): online fills it again, now.
            PersonSources.SetPhoto(person, null, null, now);
            person.InfoCheckedAt = null;
        }

        if ((person.ImageUrl is not null && person.Biography is not null) || (!force && person.InfoCheckedAt > now - LookAgainAfter))
        {
            return;
        }

        if (fromArr is not null)
        {
            Take(person, Plain(fromArr.Overview), ArrPhoto(fromArr.Images), PersonSources.Readarr, now);
        }

        var failed = false;
        if (providers is not null && Missing(person))
        {
            try
            {
                if (await AudibleAsync(person, knownAsins, ct).ConfigureAwait(false) is { } audible)
                {
                    Take(person, audible.Biography, audible.Image, PersonSources.Audible, now);
                }
            }
            catch (Exception ex) when (IsFailure(ex, ct))
            {
                LogLookupFailed(person.Name, "Audible", ex.Message);
                failed = true;
            }
        }

        if (providers is not null && Missing(person))
        {
            try
            {
                if (await providers.OpenLibraryAuthorAsync(person.Name, [.. books.Select(b => b.Title)], ct).ConfigureAwait(false) is { } openLibrary)
                {
                    Take(person, openLibrary.Biography, openLibrary.Image, PersonSources.OpenLibrary, now);
                }
            }
            catch (Exception ex) when (IsFailure(ex, ct))
            {
                LogLookupFailed(person.Name, "Open Library", ex.Message);
                failed = true;
            }
        }

        // A provider that couldn't be asked is asked again at the next refresh, not a month on.
        if (!failed)
        {
            person.InfoCheckedAt = now;
        }
    }

    /// <summary>
    /// Every photo and biography an admin could choose from for a person: what they have now (under its own source),
    /// their folder's photo, Readarr's, Audible's and Open Library's, at most one of each from each source. A source
    /// that can't be asked is left out.
    /// </summary>
    public async Task<(List<AuthorCandidate> Photos, List<AuthorCandidate> Biographies)> CandidatesAsync(Person person, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(person);
        var photos = new List<AuthorCandidate>();
        var biographies = new List<AuthorCandidate>();
        void Add(string source, string? image, string? biography)
        {
            if (image is not null && (ArtworkUrl.IsHttps(image) || ArtworkUrl.IsLocal(image)) && photos.All(p => p.Source != source))
            {
                photos.Add(new AuthorCandidate(source, image, null));
            }

            if (!string.IsNullOrWhiteSpace(biography) && biographies.All(b => b.Source != source))
            {
                biographies.Add(new AuthorCandidate(source, null, biography.Trim()));
            }
        }

        Add(person.PhotoSource ?? GuessSource(person.ImageUrl), person.ImageUrl, null);
        Add(person.BiographySource ?? Current, null, person.Biography);

        var books = await BooksAsync(person, null, ct).ConfigureAwait(false);
        Add(PersonSources.Folder, (await FolderPhotoAsync(person.Name, books, ct).ConfigureAwait(false)).Photo, null);
        if ((await ReadarrAuthorsAsync(ct).ConfigureAwait(false)).FirstOrDefault(a => a.AuthorName is { } name && Fold(name) == Fold(person.Name)) is { } fromArr)
        {
            Add(PersonSources.Readarr, ArrPhoto(fromArr.Images), Plain(fromArr.Overview));
        }

        if (providers is not null)
        {
            try
            {
                if (await AudibleAsync(person, [], ct).ConfigureAwait(false) is { } audible)
                {
                    Add(PersonSources.Audible, audible.Image, audible.Biography);
                }
            }
            catch (Exception ex) when (IsFailure(ex, ct))
            {
                LogLookupFailed(person.Name, "Audible", ex.Message);
            }

            try
            {
                if (await providers.OpenLibraryAuthorAsync(person.Name, [.. books.Select(b => b.Title)], ct).ConfigureAwait(false) is { } openLibrary)
                {
                    Add(PersonSources.OpenLibrary, openLibrary.Image, openLibrary.Biography);
                }
            }
            catch (Exception ex) when (IsFailure(ex, ct))
            {
                LogLookupFailed(person.Name, "Open Library", ex.Message);
            }
        }

        return (photos, biographies);
    }

    /// <summary>Where a photo from before sources were kept came from, by its address.</summary>
    private static string GuessSource(string? url) =>
        url is null ? Current
        : url.StartsWith("file://", StringComparison.Ordinal) ? PersonSources.Folder
        : Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host == "image.tmdb.org" ? PersonSources.Tmdb
        : uri?.Host == "static.tvmaze.com" ? PersonSources.TvMaze
        : uri?.Host == "covers.openlibrary.org" ? PersonSources.OpenLibrary
        : Current;

    /// <summary>Audible's photo and biography: by the ASIN the person has, else (or when that's unknown there) by name.</summary>
    private async Task<BookAuthorInfo?> AudibleAsync(Person person, IReadOnlyCollection<string> knownAsins, CancellationToken ct)
    {
        if (person.Asin is { Length: > 0 } asin && await providers!.AuthorAsync(asin, ct).ConfigureAwait(false) is { } byAsin)
        {
            return byAsin;
        }

        if (await providers!.FindAudibleAuthorAsync(person.Name, knownAsins, ct).ConfigureAwait(false) is not { } found
            || string.Equals(found, person.Asin, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var info = await providers.AuthorAsync(found, ct).ConfigureAwait(false);
        if (info is not null)
        {
            person.Asin ??= found;
        }

        return info;
    }

    /// <summary>Fills only what's missing, noting where it came from.</summary>
    private static void Take(Person person, string? biography, string? image, string source, DateTime now)
    {
        if (person.Biography is null && !string.IsNullOrWhiteSpace(biography))
        {
            person.Biography = biography.Trim();
            person.BiographySource = source;
        }

        if (person.ImageUrl is null && ArtworkUrl.IsHttps(image))
        {
            PersonSources.SetPhoto(person, image, source, now);
        }
    }

    private static bool Missing(Person person) => person.ImageUrl is null || person.Biography is null;

    /// <summary>The books a person wrote: saved ones and, for someone new, the one being refreshed.</summary>
    private async Task<List<AuthorBook>> BooksAsync(Person person, AuthorBook? current, CancellationToken ct)
    {
        var books = await db.Credits.AsNoTracking().Where(c => c.PersonId == person.Id && c.Role == CreditRole.Author)
            .Select(c => new AuthorBook(c.MediaItem!.Title, c.MediaItem.Path, c.MediaItem.LibraryId)).ToListAsync(ct).ConfigureAwait(false);
        if (current is not null && !books.Contains(current))
        {
            books.Add(current);
        }

        return books;
    }

    /// <summary>
    /// A photo in the author's own folder: author.jpg (or .jpeg, .png, .webp), else folder.jpg, in the top folder of a
    /// book of theirs that's under an author folder (Author/Title, Author/Series/Title) named for them alone.
    /// <c>Sure</c> is false when a folder that could hold one couldn't be read (a share that's offline), so no photo
    /// doesn't mean it's gone.
    /// </summary>
    private async Task<(string? Photo, bool Sure)> FolderPhotoAsync(string name, List<AuthorBook> books, CancellationToken ct)
    {
        var libraries = books.Where(b => b.Path is not null).Select(b => b.LibraryId).Distinct().ToList();
        if (libraries.Count == 0)
        {
            return (null, true);
        }

        var roots = await db.LibraryFolders.AsNoTracking().Where(f => libraries.Contains(f.LibraryId)).Select(f => new { f.LibraryId, f.Path }).ToListAsync(ct).ConfigureAwait(false);
        var sure = true;
        foreach (var book in books.Where(b => b.Path is not null))
        {
            foreach (var root in roots.Where(r => r.LibraryId == book.LibraryId).Select(r => r.Path.TrimEnd('/')))
            {
                var (photo, listed) = FolderPhoto(name, root, book.Path!, ListFolder);
                if (photo is not null)
                {
                    return (photo, true);
                }

                sure &= listed;
            }
        }

        return (null, sure);
    }

    /// <summary>
    /// The photo in <paramref name="bookPath"/>'s author folder under <paramref name="root"/>, as a file:// URL.
    /// <c>Listed</c> is false when the folder couldn't be read.
    /// </summary>
    /// <param name="list">The files in a folder; null when it can't be read (see <see cref="ListFolder"/>).</param>
    internal static (string? Photo, bool Listed) FolderPhoto(string name, string root, string bookPath, Func<string, string[]?> list)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (root.Length == 0 || !PathComparison.IsUnder(bookPath, root))
        {
            return (null, true);
        }

        var relative = Path.GetRelativePath(root, bookPath).Split(Path.DirectorySeparatorChar);
        if (relative.Length < 2 || relative[0] is "." or ".." || BookLayout.SplitAuthors(relative[0]) is not [var only] || Fold(only) != Fold(name))
        {
            return (null, true);
        }

        if (list(Path.Combine(root, relative[0])) is not { } files)
        {
            return (null, false);
        }

        foreach (var wanted in PhotoNames)
        {
            foreach (var extension in PhotoExtensions)
            {
                if (files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), wanted + extension, StringComparison.OrdinalIgnoreCase)) is { } file)
                {
                    return ($"file://{file}", true);
                }
            }
        }

        return (null, true);
    }

    /// <summary>
    /// The files in an author folder: none when it's gone from a library folder that's there, null when it can't be
    /// read or its library folder is missing or empty (a share that isn't mounted). Links are left out: an author.jpg
    /// linked to a picture elsewhere would show something from outside the library.
    /// </summary>
    internal static string[]? ListFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                return [.. Directory.GetFiles(folder).Where(f => new FileInfo(f).LinkTarget is null)];
            }

            var root = Path.GetDirectoryName(folder);
            return root is not null && Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() ? [] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// A local photo's size and modified time, as the image cache tells copies apart: a replacement copied with its old
    /// date (cp -p, rsync -a) still differs.
    /// </summary>
    private static string? FileStamp(string url)
    {
        var file = new FileInfo(ArtworkUrl.LocalPath(url)!);
        return file.Exists ? $"{file.LastWriteTimeUtc.Ticks:x}-{file.Length:x}" : null;
    }

    /// <summary>The authors every Readarr-compatible app knows; one that doesn't answer is skipped.</summary>
    private async Task<List<ReadarrAuthor>> ReadarrAuthorsAsync(CancellationToken ct)
    {
        var authors = new List<ReadarrAuthor>();
        foreach (var readarr in settings.ArrInstances.Where(a => a.Kind == ArrKind.Readarr))
        {
            try
            {
                authors.AddRange(await arr.GetAuthorsAsync(readarr, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (IsFailure(ex, ct))
            {
                LogLookupFailed(readarr.Name, readarr.Name, ex.Message);
            }
        }

        return authors;
    }

    /// <summary>The app's photo of an author, when it points at one on the web (its own /MediaCover paths need its key).</summary>
    private static string? ArrPhoto(IReadOnlyList<ArrImage> images) =>
        images.Where(i => i.CoverType is "poster" or "headshot" or "cover").Select(i => i.RemoteUrl).FirstOrDefault(ArtworkUrl.IsHttps);

    private static string? Plain(string? html) => string.IsNullOrWhiteSpace(html) ? null : BookSidecars.PlainText(html);

    /// <summary>A provider that couldn't be asked, or answered nonsense; not the refresh being stopped.</summary>
    private static bool IsFailure(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or IOException or System.Text.Json.JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    /// <summary>A name to compare: "J.R.R. Tolkien" and "J. R. R. Tolkien" are the same.</summary>
    internal static string Fold(string value) => new([.. value.Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    [LoggerMessage(Level = LogLevel.Information, Message = "Looking up {Author} on {Source} failed: {Error}")]
    private partial void LogLookupFailed(string author, string source, string error);
}
