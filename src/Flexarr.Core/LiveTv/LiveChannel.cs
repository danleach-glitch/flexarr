namespace Flexarr.Core.LiveTv;

/// <summary>
/// A TV channel from the Live TV source (Dispatcharr, through its Xtream Codes API), as its channel list last gave it.
/// The list is the source's to curate: channels it drops are removed here at the next refresh.
/// </summary>
public sealed class LiveChannel
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The source's id for it: the stream id in its channel list, which its stream address ends with.</summary>
    public int SourceId { get; set; }

    /// <summary>The channel number viewers know it by; the source may use decimals (101.1).</summary>
    public double Number { get; set; }

    public required string Name { get; set; }

    /// <summary>The source's group for it ("UK News"), for filtering the guide.</summary>
    public string? Group { get; set; }

    public string? LogoUrl { get; set; }

    /// <summary>Which channel it is in the guide (XMLTV's channel id); null when the source matched it to none.</summary>
    public string? GuideId { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A programme in the guide, on one channel.</summary>
public sealed class LiveProgramme
{
    public long Id { get; set; }
    public Guid ChannelId { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public required string Title { get; set; }

    /// <summary>The episode's own title, when the guide gives one.</summary>
    public string? Subtitle { get; set; }

    public string? Description { get; set; }

    /// <summary>The first of the guide's categories ("News", "Film").</summary>
    public string? Category { get; set; }

    public int? Season { get; set; }
    public int? Episode { get; set; }
    public string? ImageUrl { get; set; }

    /// <summary>A first showing rather than a repeat, when the guide says so.</summary>
    public bool IsNew { get; set; }
}

/// <summary>A channel a profile starred, shown first and on its own in the guide.</summary>
public sealed class LiveFavourite
{
    public required string ProfileId { get; set; }
    public Guid ChannelId { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
