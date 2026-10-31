namespace Flexarr.Core.Media;

public enum CollectionKind
{
    /// <summary>Titles chosen by hand, in the order given.</summary>
    Manual,

    /// <summary>Titles matching saved rules, worked out whenever it's opened.</summary>
    Smart,

    /// <summary>A film series from Radarr (TMDB collections), kept in step automatically.</summary>
    Franchise,

    /// <summary>One of a profile's Trakt lists (its own or one it liked), kept in step with Trakt; always personal.</summary>
    TraktList,
}

/// <summary>
/// A group of films and shows. Household collections (no owner) are for everyone and made by the main profile;
/// personal ones belong to the profile that made them.
/// </summary>
public sealed class Collection
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public CollectionKind Kind { get; set; }

    /// <summary>The profile it belongs to; null for a household collection.</summary>
    public string? OwnerProfileId { get; set; }

    /// <summary>For smart collections: their rules, as JSON (the API owns the shape).</summary>
    public string? RulesJson { get; set; }

    /// <summary>For Trakt lists: "{user slug}/{list id}", as <see cref="TraktConnection.ListIds"/> holds it.</summary>
    public string? TraktListId { get; set; }

    /// <summary>For franchises: TMDB's collection id, as Radarr reports it.</summary>
    public int? TmdbCollectionId { get; set; }

    /// <summary>Franchise artwork (remote URLs); other collections show their titles' posters.</summary>
    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }

    /// <summary>For franchises: the films not in the library yet, as JSON, so they can be shown and requested.</summary>
    public string? MissingJson { get; set; }

    /// <summary>A franchise someone doesn't want; it isn't recreated by the next sync.</summary>
    public bool Hidden { get; set; }

    /// <summary>Shown as a row on Home (placed and reordered like any other row).</summary>
    public bool ShowOnHome { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<CollectionItem> Items { get; set; } = [];
}

public sealed class CollectionItem
{
    public Guid CollectionId { get; set; }
    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    /// <summary>Order within the collection (by hand for manual ones; release order for franchises).</summary>
    public int Position { get; set; }
}
