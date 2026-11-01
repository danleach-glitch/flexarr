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

    private static string Key(string name) => new([.. name.Where(char.IsLetter).Select(char.ToLowerInvariant)]);
}
