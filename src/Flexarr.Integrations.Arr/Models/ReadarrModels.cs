using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flexarr.Integrations.Arr.Models;

/// <summary>
/// Reads an id as text whether it's a number or a string. Readarr's are numbers; Chaptarr (beta) prefixes its foreign
/// ids with their provider ("hc:428812", "gr:…") and may change more, so ids are never parsed as numbers.
/// </summary>
public sealed class FlexibleIdConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString(),
        JsonTokenType.Number => reader.TryGetInt64(out var n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
        JsonTokenType.Null => null,
        _ => Skip(ref reader),
    };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value);
    }

    private static string? Skip(ref Utf8JsonReader reader)
    {
        reader.Skip();
        return null;
    }
}

/// <summary>Which of the Readarr family an instance is: its API is the same, its quirks aren't.</summary>
public enum ReadarrFlavour
{
    Readarr,
    Bookshelf,
    Chaptarr,
}

public static class ReadarrFlavours
{
    /// <summary>
    /// Bookshelf calls itself Readarr in <c>appName</c>; its <c>packageAuthor</c> (pennydreadful) gives it away.
    /// Chaptarr names itself.
    /// </summary>
    public static ReadarrFlavour Of(ArrSystemStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.AppName.Contains("chaptarr", StringComparison.OrdinalIgnoreCase))
        {
            return ReadarrFlavour.Chaptarr;
        }

        return status.PackageAuthor?.Contains("pennydreadful", StringComparison.OrdinalIgnoreCase) == true
            || status.AppName.Contains("bookshelf", StringComparison.OrdinalIgnoreCase)
            ? ReadarrFlavour.Bookshelf
            : ReadarrFlavour.Readarr;
    }
}

/// <summary>An author as a Readarr-compatible app knows them. Unknown fields are ignored.</summary>
public sealed record ReadarrAuthor
{
    [JsonConverter(typeof(FlexibleIdConverter))]
    public string? Id { get; init; }

    public string? AuthorName { get; init; }

    /// <summary>Goodreads', Hardcover's or (Chaptarr) a prefixed id.</summary>
    [JsonConverter(typeof(FlexibleIdConverter))]
    public string? ForeignAuthorId { get; init; }

    public string? Path { get; init; }
    public string? Overview { get; init; }
    public string? SortName { get; init; }
    public bool Monitored { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<ArrImage> Images { get; init; } = [];
}

/// <summary>One edition of a book: the audiobook or an ebook, with its ASIN and publisher.</summary>
public sealed record ReadarrEdition
{
    [JsonConverter(typeof(FlexibleIdConverter))]
    public string? ForeignEditionId { get; init; }

    public string? Title { get; init; }
    public string? Asin { get; init; }
    public string? Isbn13 { get; init; }
    public string? Language { get; init; }
    public string? Overview { get; init; }

    /// <summary>"Audiobook", "Audio CD", "Kindle Edition", "Paperback"…</summary>
    public string? Format { get; init; }

    public bool IsEbook { get; init; }
    public string? Publisher { get; init; }
    public DateTime? ReleaseDate { get; init; }
    public bool Monitored { get; init; }
    public IReadOnlyList<ArrImage> Images { get; init; } = [];
}

/// <summary>A book as a Readarr-compatible app knows it, owned or not.</summary>
public sealed record ReadarrBook
{
    [JsonConverter(typeof(FlexibleIdConverter))]
    public string? Id { get; init; }

    [JsonConverter(typeof(FlexibleIdConverter))]
    public string? AuthorId { get; init; }

    public string? Title { get; init; }
    public string? AuthorTitle { get; init; }
    public string? SeriesTitle { get; init; }

    [JsonConverter(typeof(FlexibleIdConverter))]
    public string? ForeignBookId { get; init; }

    public string? Overview { get; init; }
    public DateTime? ReleaseDate { get; init; }
    public bool Monitored { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<ArrImage> Images { get; init; } = [];
    public IReadOnlyList<ReadarrEdition> Editions { get; init; } = [];
    public ReadarrAuthor? Author { get; init; }

    /// <summary>The edition it's after: the monitored one, else the first.</summary>
    public ReadarrEdition? Edition => Editions.FirstOrDefault(e => e.Monitored) ?? (Editions.Count > 0 ? Editions[0] : null);
}

/// <summary>A file the app has imported for a book.</summary>
public sealed record ReadarrBookFile
{
    [JsonConverter(typeof(FlexibleIdConverter))]
    public string? Id { get; init; }

    [JsonConverter(typeof(FlexibleIdConverter))]
    public string? AuthorId { get; init; }

    [JsonConverter(typeof(FlexibleIdConverter))]
    public string? BookId { get; init; }

    public string? Path { get; init; }
    public long Size { get; init; }
}

/// <summary>A series and where each of its books sits in it.</summary>
public sealed record ReadarrSeries
{
    [JsonConverter(typeof(FlexibleIdConverter))]
    public string? Id { get; init; }

    public string? Title { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<ReadarrSeriesLink> Links { get; init; } = [];
}

/// <param name="Position">The book's number as the series writes it ("1", "1.5", "0").</param>
public sealed record ReadarrSeriesLink(string? Position, [property: JsonConverter(typeof(FlexibleIdConverter))] string? BookId);
