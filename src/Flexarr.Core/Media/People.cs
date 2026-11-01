namespace Flexarr.Core.Media;

public sealed class Genre
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public List<MediaItem> Items { get; set; } = [];
}

public enum CreditRole
{
    Actor,
    Director,
    Writer,
    Producer,
    Creator,

    /// <summary>Wrote a book.</summary>
    Author,

    /// <summary>Reads an audiobook aloud.</summary>
    Narrator,

    /// <summary>Translated a book.</summary>
    Translator,
}

public sealed class Person
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string? TmdbId { get; set; }
    public string? TvMazeId { get; set; }
    public string? ImageUrl { get; set; }

    /// <summary>A short biography, for authors and narrators (from a sidecar, the *arr or Audible).</summary>
    public string? Biography { get; set; }

    /// <summary>Audible's id for an author, for looking up their photo and biography.</summary>
    public string? Asin { get; set; }

    /// <summary>
    /// Where <see cref="ImageUrl"/> came from, one of <see cref="PersonSources"/>; null for a photo from before
    /// sources were kept.
    /// </summary>
    public string? PhotoSource { get; set; }

    /// <summary>Where <see cref="Biography"/> came from, as <see cref="PhotoSource"/>.</summary>
    public string? BiographySource { get; set; }

    /// <summary>An admin fixed this person's photo or biography by hand: refreshes leave both alone.</summary>
    public bool InfoLocked { get; set; }

    /// <summary>When an author's photo and biography were last looked up online; looked up again a month on.</summary>
    public DateTime? InfoCheckedAt { get; set; }

    /// <summary>When the photo last changed, for its URL's ?v=; null for one from before this was kept.</summary>
    public DateTime? ImageChangedAt { get; set; }

    /// <summary>
    /// A photo from the author's folder, as last seen: its size and modified time. Any change (even to an older time, a
    /// copy that kept its date) is a new ?v=.
    /// </summary>
    public string? ImageStamp { get; set; }

    public List<Credit> Credits { get; set; } = [];
}

/// <summary>Where a person's photo or biography came from (<see cref="Person.PhotoSource"/>).</summary>
public static class PersonSources
{
    /// <summary>The longest biography kept, an admin's or a provider's.</summary>
    public const int MaxBiography = 20_000;

    /// <summary>A biography trimmed, and cut short (at a word, with an ellipsis) past <see cref="MaxBiography"/>.</summary>
    public static string? Biography(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        if (text.Length <= MaxBiography)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', MaxBiography - 1);
        return text[..(cut > MaxBiography / 2 ? cut : MaxBiography - 1)].TrimEnd() + "…";
    }

    public const string Folder = "folder", Readarr = "readarr", Audible = "audible", OpenLibrary = "openlibrary",
        Tmdb = "tmdb", TvMaze = "tvmaze", Upload = "upload", Manual = "manual";

    /// <summary>
    /// Whether a refresh may change this person's photo: not when an admin locked it, and never over a photo from the
    /// author's own folder or one uploaded or chosen by hand.
    /// </summary>
    public static bool PhotoReplaceable(Person person)
    {
        ArgumentNullException.ThrowIfNull(person);
        return !person.InfoLocked && person.PhotoSource is not (Folder or Upload or Manual);
    }

    /// <summary>Sets a person's photo and where it came from, noting when it changed (its URL's ?v=).</summary>
    public static void SetPhoto(Person person, string? url, string? source, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(person);
        if (!string.Equals(person.ImageUrl, url, StringComparison.Ordinal))
        {
            person.ImageUrl = url;
            person.ImageChangedAt = now;
            person.ImageStamp = null;
        }

        person.PhotoSource = url is null ? null : source;
    }
}

public sealed class Credit
{
    public int Id { get; set; }
    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }
    public Guid PersonId { get; set; }
    public Person? Person { get; set; }
    public CreditRole Role { get; set; }
    public string? Character { get; set; }
    public int Order { get; set; }
}
