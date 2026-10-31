namespace Flexarr.Core.Books;

/// <summary>A book's series and its number in it, as one source gives it.</summary>
public sealed record SeriesPart(string Name, string? Sequence);

/// <summary>
/// What one source (a sidecar file, the tags, the path) says about a book. Sources are layered with <see cref="Over"/>:
/// the more trusted one's values win, and the less trusted only fill its gaps.
/// </summary>
public sealed record BookInfo
{
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public IReadOnlyList<string> Authors { get; init; } = [];
    public IReadOnlyList<string> Narrators { get; init; } = [];
    public IReadOnlyList<string> Translators { get; init; } = [];
    public IReadOnlyList<SeriesPart> Series { get; init; } = [];
    public string? Description { get; init; }
    public string? Publisher { get; init; }
    public int? Year { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    public string? Language { get; init; }
    public string? Asin { get; init; }
    public string? Isbn { get; init; }
    public bool? Abridged { get; init; }
    public bool? Explicit { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>A cover image: a file path, or <c>embedded://</c> plus the file whose artwork it is.</summary>
    public string? Cover { get; init; }

    public static BookInfo Empty { get; } = new();

    /// <summary>This source, with <paramref name="lower"/> filling what it doesn't say.</summary>
    public BookInfo Over(BookInfo lower)
    {
        ArgumentNullException.ThrowIfNull(lower);
        return new BookInfo
        {
            Title = Title ?? lower.Title,
            Subtitle = Subtitle ?? lower.Subtitle,
            Authors = Authors.Count > 0 ? Authors : lower.Authors,
            Narrators = Narrators.Count > 0 ? Narrators : lower.Narrators,
            Translators = Translators.Count > 0 ? Translators : lower.Translators,
            Series = Series.Count > 0 ? MergeSeries(Series, lower.Series) : lower.Series,
            Description = Description ?? lower.Description,
            Publisher = Publisher ?? lower.Publisher,
            Year = Year ?? lower.Year,
            ReleaseDate = ReleaseDate ?? lower.ReleaseDate,
            Language = Language ?? lower.Language,
            Asin = Asin ?? lower.Asin,
            Isbn = Isbn ?? lower.Isbn,
            Abridged = Abridged ?? lower.Abridged,
            Explicit = Explicit ?? lower.Explicit,
            Genres = Genres.Count > 0 ? Genres : lower.Genres,
            Cover = Cover ?? lower.Cover,
        };
    }

    /// <summary>The higher source's series, each given the lower's number for it when it has none of its own.</summary>
    private static List<SeriesPart> MergeSeries(IReadOnlyList<SeriesPart> higher, IReadOnlyList<SeriesPart> lower) =>
        [.. higher.Select(h => h.Sequence is not null ? h
            : lower.FirstOrDefault(l => SameSeries(l.Name, h.Name) && l.Sequence is not null) is { } l ? h with { Sequence = l.Sequence } : h)];

    public static bool SameSeries(string a, string b) =>
        string.Equals(Fold(a), Fold(b), StringComparison.Ordinal);

    private static string Fold(string value)
    {
        var folded = value.Normalize(System.Text.NormalizationForm.FormC).Trim().ToLowerInvariant();
        return folded.StartsWith("the ", StringComparison.Ordinal) ? folded[4..] : folded;
    }
}
