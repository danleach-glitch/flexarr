using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Core.Books;

/// <summary>
/// What a book's genres may be. Tags written by rippers and download tools often name the format ("Audiobook",
/// "Spoken Word") as the genre; those say nothing about the book, and two spellings split the Genres view in two.
/// </summary>
public static class BookGenres
{
    /// <summary>Format words, compared on letters only and in lower case ("Audio Book", "audiobooks").</summary>
    private static readonly HashSet<string> FormatWords =
    [
        "audiobook", "audiobooks", "audiobookunabridged", "audiobooksunabridged", "audio", "audible", "audibleaudio",
        "spokenword", "speech", "unabridged", "abridged", "book", "books",
    ];

    /// <summary>Whether a genre only names the format.</summary>
    public static bool IsFormat(string name) => FormatWords.Contains(Key(name));

    /// <summary>The genres worth keeping: trimmed, no format words, each once.</summary>
    public static List<string> Clean(IEnumerable<string> names) =>
        [.. names.Select(n => n.Trim()).Where(n => n.Length > 0 && !IsFormat(n)).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The genre called <paramref name="name"/>: written just so, else in any case ("fantasy" is "Fantasy", so the
    /// Genres view isn't split in two), else one added this run, else a new one.
    /// </summary>
    public static async Task<Genre> FindAsync(FlexarrDbContext db, string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.Genres.FirstOrDefaultAsync(g => g.Name == name, ct).ConfigureAwait(false)
            ?? await db.Genres.FirstOrDefaultAsync(g => EF.Functions.Collate(g.Name, "NOCASE") == name, ct).ConfigureAwait(false)
            ?? db.Genres.Local.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? db.Genres.Add(new Genre { Name = name }).Entity;
    }

    private static string Key(string name) => new([.. name.Where(char.IsLetter).Select(char.ToLowerInvariant)]);
}
