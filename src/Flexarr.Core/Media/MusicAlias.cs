namespace Flexarr.Core.Media;

/// <summary>
/// What the scanner would have filed tracks under for an artist or album that was merged into another (in Library
/// health): its <c>artist:</c> or <c>album:</c> key, or its folder as <c>path:</c>. The scanner reads these with its
/// index, so new and rescanned tracks land in the one kept, whatever their tags say.
/// </summary>
public sealed class MusicAlias
{
    public int Id { get; set; }
    public Guid LibraryId { get; set; }

    /// <summary>The scanner's key: <c>artist:&lt;folded name&gt;</c>, <c>album:&lt;artist id&gt;:&lt;folded title&gt;</c> or <c>path:&lt;folder key&gt;</c>.</summary>
    public required string Key { get; set; }

    /// <summary>The artist or album it now means.</summary>
    public Guid ItemId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
