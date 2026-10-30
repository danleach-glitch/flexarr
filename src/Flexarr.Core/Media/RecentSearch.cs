namespace Flexarr.Core.Media;

/// <summary>
/// Something a profile searched for and what they picked from the results, shown back as recent searches on every
/// device the profile uses. A row with no <see cref="Query"/> is an item reopened from the recent list itself; one with
/// no <see cref="ItemId"/> is a search that led to a person or genre (or the item has since gone).
/// </summary>
public sealed class RecentSearch
{
    public const int MaxQueryLength = 200;

    public long Id { get; set; }
    public required string ProfileId { get; set; }
    public string? Query { get; set; }
    public Guid? ItemId { get; set; }
    public DateTime SearchedAt { get; set; } = DateTime.UtcNow;
}
