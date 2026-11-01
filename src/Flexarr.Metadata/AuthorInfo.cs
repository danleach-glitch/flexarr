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
/// <param name="readarrAuthors">Readarr's authors kept a few minutes between requests (the editor's); none in a refresh.</param>
public sealed partial class AuthorInfo(FlexarrDbContext db, ArrClient arr, IMetadataSettings settings, BookProviders? providers, ILogger logger,
    ReadarrAuthorCache? readarrAuthors = null)
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
    public Task FillBookAsync(MediaItem book, IReadOnlyList<ReadarrAuthor> readarr, ReadarrAuthor? matched, IReadOnlyCollection<string> knownAsins, CancellationToken ct) =>
        FillBookAsync(book, new ReadarrAuthorIndex(readarr), matched, knownAsins, ct);

    /// <inheritdoc cref="FillBookAsync(MediaItem, IReadOnlyList{ReadarrAuthor}, ReadarrAuthor?, IReadOnlyCollection{string}, CancellationToken)"/>
    /// <remarks>The index is made once a refresh, not once a book.</remarks>
    public async Task FillBookAsync(MediaItem book, ReadarrAuthorIndex readarr, ReadarrAuthor? matched, IReadOnlyCollection<string> knownAsins, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(readarr);
        var authors = book.Credits.Where(c => c.Role == CreditRole.Author && c.Person is not null).OrderBy(c => c.Order).Select(c => c.Person!).Distinct().ToList();
        for (var i = 0; i < authors.Count; i++)
        {
            var person = authors[i];
            var fromArr = readarr.Find(person.Name) ?? (authors.Count == 1 ? matched : null);
            var standing = await StandingAsync(person, new AuthorBook(book.Title, book.Path, book.LibraryId), ct).ConfigureAwait(false);
            await FillAsync(person, fromArr, knownAsins, standing, force: false, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One person, now (an admin unlocked them): their folder, then online, whenever they were last looked up. Readarr's
    /// authors are asked for; an app that doesn't answer is skipped. Only for someone credited on a book (an author,
    /// narrator or translator), and never for someone from films or TV (see <see cref="Standing"/>).
    /// </summary>
    public async Task RefreshPersonAsync(Person person, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(person);
        var standing = await StandingAsync(person, null, ct).ConfigureAwait(false);
        if (!standing.Writes || standing.Screen)
        {
            return;
        }

        var readarr = await ReadarrAuthorsAsync(ct).ConfigureAwait(false);
        var fromArr = new ReadarrAuthorIndex(readarr).Find(person.Name);
        await FillAsync(person, fromArr, [], standing, force: true, ct).ConfigureAwait(false);
    }

    private async Task FillAsync(Person person, ReadarrAuthor? fromArr, IReadOnlyCollection<string> knownAsins, Standing standing, bool force, CancellationToken ct)
    {
        // Someone from films or TV keeps their photo from there (TMDB can't take a book's back), and no namesake's biography.
        if (person.InfoLocked || standing.Screen)
        {
            return;
        }

        var now = DateTime.UtcNow;

        // A photo that can't be served (an old http:// one) is no photo: something else can fill its place.
        if (person.ImageUrl is { } old && !ArtworkUrl.IsHttps(old) && !ArtworkUrl.IsLocal(old) && PersonSources.PhotoReplaceable(person))
        {
            PersonSources.SetPhoto(person, null, null, now);
            person.InfoCheckedAt = null;
        }

        var books = standing.Books;

        // Their own folder's photo, looked for every time (it's only a look on disk).
        await ApplyFolderPhotoAsync(person, books, now, ct).ConfigureAwait(false);

        if ((person.ImageUrl is not null && person.Biography is not null) || (!force && person.InfoCheckedAt > now - LookAgainAfter))
        {
            return;
        }

        if (fromArr is not null)
        {
            Take(person, Plain(fromArr.Overview), ArrPhoto(fromArr.Images), PersonSources.Readarr, now);
        }

        // A provider that failed (down, slow, or asking to be left alone) isn't asked again for the rest of the refresh:
        // its authors wait for the next one.
        var failed = false;
        if (providers is not null && Missing(person))
        {
            if (_down.Contains(PersonSources.Audible))
            {
                failed = true;
            }
            else
            {
                try
                {
                    if (await AudibleAsync(person, knownAsins, remember: true, ct).ConfigureAwait(false) is { } audible)
                    {
                        Take(person, audible.Biography, audible.Image, PersonSources.Audible, now);
                    }
                }
                catch (Exception ex) when (IsFailure(ex, ct))
                {
                    LogLookupFailed(person.Name, "Audible", ex.Message);
                    _down.Add(PersonSources.Audible);
                    failed = true;
                }
            }
        }

        if (providers is not null && Missing(person))
        {
            if (_down.Contains(PersonSources.OpenLibrary))
            {
                failed = true;
            }
            else
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
                    _down.Add(PersonSources.OpenLibrary);
                    failed = true;
                }
            }
        }

        // A provider that couldn't be asked is asked again at the next refresh, not a month on.
        if (!failed)
        {
            person.InfoCheckedAt = now;
        }
    }

    /// <summary>
    /// A person's photo from their own folder: taken (over anything but an upload or a photo chosen by hand), its ?v=
    /// moved on when the file changes, and dropped when it's gone (not merely out of reach).
    /// </summary>
    private async Task ApplyFolderPhotoAsync(Person person, List<AuthorBook> books, DateTime now, CancellationToken ct)
    {
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
    }

    /// <summary>
    /// After a scan: every unlocked author of a book in the library (or just in <paramref name="within"/>, a webhook's
    /// folders) gets their folder's photo, new, changed or gone, without waiting for one of their books to be due a
    /// refresh. Only the disk is looked at; the caller saves.
    /// </summary>
    public async Task FolderPhotosAsync(Guid libraryId, IReadOnlyCollection<string> within, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(within);
        var credited = await db.Credits.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Role == CreditRole.Author && c.MediaItem!.LibraryId == libraryId && c.MediaItem.MissingSince == null && c.MediaItem.Path != null
                && !c.Person!.InfoLocked)
            .Select(c => new { c.PersonId, c.MediaItem!.Path })
            .ToListAsync(ct).ConfigureAwait(false);
        var ids = credited.Where(c => within.Count == 0 || within.Any(w => PathComparison.IsUnder(c.Path!, w))).Select(c => c.PersonId).Distinct().ToList();
        var now = DateTime.UtcNow;
        foreach (var chunk in ids.Chunk(200))
        {
            foreach (var person in await db.People.Where(p => chunk.Contains(p.Id)).ToListAsync(ct).ConfigureAwait(false))
            {
                var standing = await StandingAsync(person, null, ct).ConfigureAwait(false);
                if (!standing.Screen)
                {
                    await ApplyFolderPhotoAsync(person, standing.Books, now, ct).ConfigureAwait(false);
                }
            }
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

            if (PersonSources.Biography(biography) is { } text && biographies.All(b => b.Source != source))
            {
                biographies.Add(new AuthorCandidate(source, null, text));
            }
        }

        Add(person.PhotoSource ?? GuessSource(person.ImageUrl), person.ImageUrl, null);
        Add(person.BiographySource ?? Current, null, person.Biography);

        // Only someone credited on a book is looked for as an author, and never someone from films or TV.
        var standing = await StandingAsync(person, null, ct).ConfigureAwait(false);
        if (!standing.Writes || standing.Screen)
        {
            return (photos, biographies);
        }

        var books = standing.Books;
        Add(PersonSources.Folder, (await FolderPhotoAsync(person.Name, books, ct).ConfigureAwait(false)).Photo, null);
        if (new ReadarrAuthorIndex(await ReadarrAuthorsAsync(ct).ConfigureAwait(false)).Find(person.Name) is { } fromArr)
        {
            Add(PersonSources.Readarr, OnlinePhoto(ArrPhoto(fromArr.Images), PersonSources.Readarr), Plain(fromArr.Overview));
        }

        if (providers is not null)
        {
            try
            {
                if (await AudibleAsync(person, [], remember: false, ct).ConfigureAwait(false) is { } audible)
                {
                    Add(PersonSources.Audible, OnlinePhoto(audible.Image, PersonSources.Audible), audible.Biography);
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
                    Add(PersonSources.OpenLibrary, OnlinePhoto(openLibrary.Image, PersonSources.OpenLibrary), openLibrary.Biography);
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

    /// <summary>
    /// Audible's photo and biography: by the ASIN the person has, else (or when that's unknown there) by name. An ASIN
    /// found by name is kept on the person only when <paramref name="remember"/> (a refresh, not the editor's list of
    /// candidates, which may be saved with whatever else an admin changes).
    /// </summary>
    private async Task<BookAuthorInfo?> AudibleAsync(Person person, IReadOnlyCollection<string> knownAsins, bool remember, CancellationToken ct)
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
        if (info is not null && remember)
        {
            person.Asin ??= found;
        }

        return info;
    }

    /// <summary>Fills only what's missing, noting where it came from.</summary>
    private static void Take(Person person, string? biography, string? image, string source, DateTime now)
    {
        if (person.Biography is null && PersonSources.Biography(biography) is { } text)
        {
            person.Biography = text;
            person.BiographySource = source;
        }

        if (person.ImageUrl is null && OnlinePhoto(image, source) is { } photo)
        {
            PersonSources.SetPhoto(person, photo, source, now);
        }
    }

    /// <summary>Where Audible's author photos live: Amazon's image hosts.</summary>
    private static readonly string[] AmazonImageSites = ["media-amazon.com", "ssl-images-amazon.com", "images-amazon.com"];

    /// <summary>
    /// A photo a provider gave, if it's one the server may fetch: Audible's only from Amazon's image hosts, Open
    /// Library's only from its covers host, and anyone's only over https from a name on the internet (not an address, a
    /// port, or a name that only means something at home), so a bad answer can't point the server at the home network.
    /// </summary>
    internal static string? OnlinePhoto(string? url, string source)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.HostNameType != UriHostNameType.Dns
            || !uri.IsDefaultPort || !uri.Host.Contains('.', StringComparison.Ordinal) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        if (host.EndsWith(".local", StringComparison.Ordinal) || host.EndsWith(".lan", StringComparison.Ordinal) || host.EndsWith(".localhost", StringComparison.Ordinal)
            || host.EndsWith(".internal", StringComparison.Ordinal) || host.EndsWith(".home.arpa", StringComparison.Ordinal) || host.EndsWith(".ts.net", StringComparison.Ordinal))
        {
            return null;
        }

        return source switch
        {
            PersonSources.Audible => AmazonImageSites.Any(site => host == site || host.EndsWith("." + site, StringComparison.Ordinal)) ? url : null,
            PersonSources.OpenLibrary => host == "covers.openlibrary.org" ? url : null,
            _ => url,
        };
    }

    private static bool Missing(Person person) => person.ImageUrl is null || person.Biography is null;

    /// <summary>
    /// What a person is credited with, as finding their photo and biography needs it: the books they wrote, whether
    /// they're credited on a book at all (an author, narrator or translator), and whether they're someone from films
    /// or TV (a TMDB or TVmaze person with a credit there), whose photo is TMDB's or TVmaze's.
    /// </summary>
    private sealed record Standing(List<AuthorBook> Books, bool Writes, bool Screen);

    /// <summary>
    /// A person's <see cref="Standing"/>: saved credits, in every library (an admin whose profile hides one still
    /// sees all of an author's books), and, for someone new, the book being refreshed.
    /// </summary>
    private async Task<Standing> StandingAsync(Person person, AuthorBook? current, CancellationToken ct)
    {
        var credits = await db.Credits.IgnoreQueryFilters().AsNoTracking().Where(c => c.PersonId == person.Id)
            .Select(c => new { c.Role, c.MediaItem!.Title, c.MediaItem.Path, c.MediaItem.LibraryId, Missing = c.MediaItem.MissingSince != null })
            .ToListAsync(ct).ConfigureAwait(false);
        var books = credits.Where(c => c.Role == CreditRole.Author && !c.Missing).Select(c => new AuthorBook(c.Title, c.Path, c.LibraryId)).Distinct().ToList();
        if (current is not null && !books.Contains(current))
        {
            books.Add(current);
        }

        var writes = current is not null || credits.Any(c => BookRoles.Contains(c.Role));
        var screen = (person.TmdbId is not null || person.TvMazeId is not null) && credits.Any(c => !BookRoles.Contains(c.Role));
        return new Standing(books, writes, screen);
    }

    /// <summary>The credits of books.</summary>
    internal static readonly CreditRole[] BookRoles = [CreditRole.Author, CreditRole.Narrator, CreditRole.Translator];

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

        var sure = true;
        foreach (var book in books.Where(b => b.Path is not null))
        {
            foreach (var root in await RootsAsync(book.LibraryId, ct).ConfigureAwait(false))
            {
                var (photo, listed) = FolderPhoto(name, root, book.Path!, Listed);
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

    /// <summary>Providers that failed while this lives (a refresh): not asked again until the next.</summary>
    private readonly HashSet<string> _down = new(StringComparer.Ordinal);

    // Remembered for as long as this lives (a refresh, a request): an author of 80 books has one folder, listed once.
    private readonly Dictionary<Guid, List<string>> _roots = [];
    private readonly Dictionary<string, string[]?> _listings = new(StringComparer.Ordinal);

    /// <summary>A library's folders.</summary>
    private async Task<List<string>> RootsAsync(Guid libraryId, CancellationToken ct)
    {
        if (!_roots.TryGetValue(libraryId, out var roots))
        {
            roots = (await db.LibraryFolders.AsNoTracking().Where(f => f.LibraryId == libraryId).Select(f => f.Path).ToListAsync(ct).ConfigureAwait(false))
                .Select(p => p.TrimEnd('/')).ToList();
            _roots[libraryId] = roots;
        }

        return roots;
    }

    /// <summary>An author folder's files, listed once (see <see cref="ListFolder"/>).</summary>
    private string[]? Listed(string folder)
    {
        if (!_listings.TryGetValue(folder, out var files))
        {
            files = ListFolder(folder);
            _listings[folder] = files;
        }

        return files;
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
                authors.AddRange(readarrAuthors is null
                    ? await arr.GetAuthorsAsync(readarr, ct).ConfigureAwait(false)
                    : await readarrAuthors.GetAsync(readarr, () => arr.GetAuthorsAsync(readarr, ct)).ConfigureAwait(false));
            }
            catch (Exception ex) when (IsFailure(ex, ct))
            {
                LogLookupFailed(readarr.Name, readarr.Name, ex.Message);
            }
        }

        return authors;
    }

    /// <summary>The app's photo of an author, when it points at one on the web (its own /MediaCover paths need its key).</summary>
    /// <remarks>Goodreads' "nophoto" placeholder isn't a photo, and would keep a real one out for good.</remarks>
    internal static string? ArrPhoto(IReadOnlyList<ArrImage> images) =>
        images.Where(i => i.CoverType is "poster" or "headshot" or "cover").Select(i => i.RemoteUrl)
            .FirstOrDefault(u => ArtworkUrl.IsHttps(u) && !u!.Contains("nophoto", StringComparison.OrdinalIgnoreCase));

    private static string? Plain(string? html) => string.IsNullOrWhiteSpace(html) ? null : BookSidecars.PlainText(html);

    /// <summary>A provider that couldn't be asked, or answered nonsense; not the refresh being stopped.</summary>
    private static bool IsFailure(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or IOException or System.Text.Json.JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    /// <summary>A name to compare: "J.R.R. Tolkien" and "J. R. R. Tolkien" are the same.</summary>
    internal static string Fold(string value) => new([.. value.Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    [LoggerMessage(Level = LogLevel.Information, Message = "Looking up {Author} on {Source} failed: {Error}")]
    private partial void LogLookupFailed(string author, string source, string error);
}

/// <summary>
/// Every author each Readarr-compatible app knows, kept for a few minutes: opening the author editor (and looking again)
/// would otherwise fetch the whole list from each app every time. One for the whole server.
/// </summary>
public sealed class ReadarrAuthorCache(TimeProvider? time = null)
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<ReadarrAuthor> Authors)> _kept = new(StringComparer.Ordinal);

    /// <summary>The app's authors, as fetched in the last few minutes, else fetched now (a failure isn't kept).</summary>
    public async Task<IReadOnlyList<ReadarrAuthor>> GetAsync(ArrInstance instance, Func<Task<IReadOnlyList<ReadarrAuthor>>> fetch)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(fetch);
        var key = $"{instance.Name}\n{instance.BaseUrl}\n{instance.ApiKey}";
        var now = _time.GetUtcNow();
        if (_kept.TryGetValue(key, out var kept) && now - kept.At < KeepFor)
        {
            return kept.Authors;
        }

        var authors = await fetch().ConfigureAwait(false);
        _kept[key] = (now, authors);
        return authors;
    }
}

/// <summary>Readarr's authors by name, folded as <see cref="AuthorInfo.Fold"/> folds them; the first of a name wins.</summary>
public sealed class ReadarrAuthorIndex
{
    public static ReadarrAuthorIndex Empty { get; } = new([]);

    private readonly Dictionary<string, ReadarrAuthor> _byName = new(StringComparer.Ordinal);

    public ReadarrAuthorIndex(IEnumerable<ReadarrAuthor> authors)
    {
        ArgumentNullException.ThrowIfNull(authors);
        foreach (var author in authors)
        {
            if (author.AuthorName is { } name)
            {
                _byName.TryAdd(AuthorInfo.Fold(name), author);
            }
        }
    }

    public ReadarrAuthor? Find(string name) => _byName.GetValueOrDefault(AuthorInfo.Fold(name));
}
