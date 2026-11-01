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

    public List<Credit> Credits { get; set; } = [];
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
