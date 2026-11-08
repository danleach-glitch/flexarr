using System.Globalization;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Search;

/// <summary>
/// The episodes, albums, tracks and books a search finds by name. Their names are indexed by kind (only these four
/// kinds, see <see cref="Searched"/>), so a search reads those names rather than every item. When the words are
/// particular enough to match few names, one pass finds all four kinds and the best of each are picked here, in the
/// order the queries give them; otherwise search goes back to a query per kind.
/// </summary>
internal static class NamedItems
{
    /// <summary>The most names one pass brings back. Past it the words are common ones, and reading every match in
    /// would cost more than the four queries, which stop at the best few.</summary>
    internal const int AtOnce = 2000;

    /// <summary>How many of each kind a search shows.</summary>
    private const int Shown = 8;

    /// <summary>The best of each kind, by id, best first.</summary>
    internal sealed record Picks(List<Guid> Episodes, List<Guid> Albums, List<Guid> Tracks, List<Guid> Books);

    /// <param name="Starts">Its title starts with the words.</param>
    /// <param name="SortStarts">Its sort title does ("The Hobbit" for "hobbit"), which counts for books.</param>
    internal sealed record Named(Guid Id, MediaKind Kind, bool Starts, bool SortStarts, int? Year, string SortTitle);

    /// <summary>
    /// Items of the four kinds search finds by name, in the words the index of their names is filtered by (SQLite uses
    /// that index only for a query that says so word for word): every query for them by name starts here.
    /// </summary>
    internal static IQueryable<MediaItem> Searched(FlexarrDbContext db) =>
        db.MediaItems.Where(i => new[] { MediaKind.Episode, MediaKind.Album, MediaKind.Track, MediaKind.Audiobook }.Contains(i.Kind));

    /// <summary>
    /// The best matches of each kind for these LIKE patterns, among what the profile can see, found in one pass; null
    /// when more than <see cref="AtOnce"/> names match (the caller then asks kind by kind).
    /// </summary>
    internal static async Task<Picks?> FindAsync(FlexarrDbContext db, string contains, string prefix, CancellationToken ct)
    {
        // Books match on their subtitle too, looked up only for books (a join would look it up for every item). The
        // first condition follows from the second, and needs only what the index holds, so only the names that match
        // (and books) are read from the items.
        var named = await Searched(db)
            .Where(i => EF.Functions.Like(i.Title, contains) || i.Kind == MediaKind.Audiobook)
            .Where(i => ((i.Kind == MediaKind.Episode || i.Kind == MediaKind.Album || i.Kind == MediaKind.Track) && EF.Functions.Like(i.Title, contains))
                || (i.Kind == MediaKind.Audiobook && i.MissingSince == null && (EF.Functions.Like(i.Title, contains)
                    || db.BookDetails.Any(b => b.MediaItemId == i.Id && b.Subtitle != null && EF.Functions.Like(b.Subtitle, contains)))))
            .Select(i => new Named(i.Id, i.Kind, EF.Functions.Like(i.Title, prefix), EF.Functions.Like(i.SortTitle, prefix), i.Year, i.SortTitle))
            .Take(AtOnce + 1).ToListAsync(ct);
        return named.Count > AtOnce ? null : Pick(named);
    }

    /// <summary>
    /// The best of each kind, as its own query orders them: names that start with the words first, then albums newest
    /// first and tracks and books by sort title, and names that still tie by id, so the same search always gives the
    /// same answer whatever order the database hands them over in.
    /// </summary>
    internal static Picks Pick(IReadOnlyList<Named> named)
    {
        IEnumerable<Named> Of(MediaKind kind) => named.Where(n => n.Kind == kind);
        static List<Guid> Best(IOrderedEnumerable<Named> ordered) => [.. ordered.ThenBy(n => n.Id, IdOrder.Instance).Take(Shown).Select(n => n.Id)];
        return new Picks(
            Best(Of(MediaKind.Episode).OrderByDescending(n => n.Starts)),
            Best(Of(MediaKind.Album).OrderByDescending(n => n.Starts).ThenByDescending(n => n.Year)),
            Best(Of(MediaKind.Track).OrderByDescending(n => n.Starts).ThenBy(n => n.SortTitle, DatabaseOrder.Instance)),
            Best(Of(MediaKind.Audiobook).OrderByDescending(n => n.Starts || n.SortStarts).ThenBy(n => n.SortTitle, DatabaseOrder.Instance)));
    }

    /// <summary>
    /// Ids in the order SQLite sorts them: it keeps a Guid as its text (upper case), so they compare as that text,
    /// hex digit by hex digit (digits before letters in either case).
    /// </summary>
    internal sealed class IdOrder : IComparer<Guid>
    {
        public static readonly IdOrder Instance = new();

        public int Compare(Guid x, Guid y) => string.CompareOrdinal(x.ToString("D", CultureInfo.InvariantCulture), y.ToString("D", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Text in the order SQLite sorts it: by its UTF-8 bytes, which is by code point. .NET's ordinal order goes by
    /// UTF-16 units, which puts characters beyond U+FFFF (emoji) before U+E000 to U+FFFF instead of after them.
    /// </summary>
    internal sealed class DatabaseOrder : IComparer<string>
    {
        public static readonly DatabaseOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            if (x is null || y is null)
            {
                return x is null ? (y is null ? 0 : -1) : 1;
            }

            var shared = Math.Min(x.Length, y.Length);
            for (var i = 0; i < shared; i++)
            {
                if (x[i] != y[i])
                {
                    return Rank(x[i]).CompareTo(Rank(y[i]));
                }
            }

            return x.Length.CompareTo(y.Length);
        }

        /// <summary>A UTF-16 unit's place by code point: surrogates (halves of characters beyond U+FFFF) go after U+E000 to U+FFFF.</summary>
        private static int Rank(char unit) => unit >= 0xE000 ? unit - 0x800 : unit >= 0xD800 ? unit + 0x2000 : unit;
    }
}
