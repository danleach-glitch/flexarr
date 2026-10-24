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
}

public sealed class Person
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string? TmdbId { get; set; }
    public string? TvMazeId { get; set; }
    public string? ImageUrl { get; set; }
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
