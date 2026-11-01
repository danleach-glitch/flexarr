using Flexarr.Core.Books;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Flexarr.Metadata.Books;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Flexarr.Metadata;

/// <summary>
/// Authors' photos and biographies. What's missing comes from Readarr, then Audible (by ASIN, then by name), then Open Library, and only
/// fills gaps. An author is looked up online at most once a month, and one an admin fixed by hand
/// (<see cref="Person.InfoLocked"/>) never is.
/// </summary>
public sealed partial class AuthorInfo(FlexarrDbContext db, ArrClient arr, IMetadataSettings settings, BookProviders? providers, ILogger logger)
{
    /// <summary>How long an author's online lookup stands before they're looked up again.</summary>
    public static readonly TimeSpan LookAgainAfter = TimeSpan.FromDays(30);

    /// <summary>The books an author is credited on, as finding their photo and biography needs them.</summary>
    private sealed record AuthorBook(string Title, string? Path, Guid LibraryId);

    /// <summary>
    /// Every credited author of a book just refreshed. <paramref name="readarr"/> are the authors the Readarr-compatible
    /// apps know (one of the same name is the author's), <paramref name="matched"/> the author of the app's book it was
    /// matched to (taken for the first author when no name agrees), and <paramref name="knownAsins"/> the authors'
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
                ?? (i == 0 && matched is not null && (matched.AuthorName is null || authors.All(p => Fold(p.Name) != Fold(matched.AuthorName))) ? matched : null);
            await FillAsync(person, fromArr, knownAsins, new AuthorBook(book.Title, book.Path, book.LibraryId), force: false, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One person, now (an admin unlocked them): online, whenever they were last looked up. Readarr's
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
