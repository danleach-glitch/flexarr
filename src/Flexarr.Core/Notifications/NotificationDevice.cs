using Flexarr.Core.Media;

namespace Flexarr.Core.Notifications;

/// <summary>
/// A phone that wants notifications, for one profile (the one it was on when they were turned on), and what about.
/// Its pushes go through a relay to Apple, sealed with <see cref="Key"/> so only the phone can read them.
/// </summary>
public sealed class NotificationDevice
{
    /// <summary>Its sign-in's id; for a phone signed in with the server's API key, <c>token:</c> and its push token.</summary>
    public required string Id { get; set; }

    /// <summary>The sign-in it belongs to: when that's revoked, its notifications stop. Null with the API key.</summary>
    public string? SignInId { get; set; }

    public required string ProfileId { get; set; }

    /// <summary>Apple's push token for the phone, in hex.</summary>
    public required string Token { get; set; }

    /// <summary>"production", or "sandbox" for a development build.</summary>
    public required string Environment { get; set; }

    /// <summary>The AES-256 key the phone made, to seal each notification with.</summary>
    public required byte[] Key { get; set; }

    /// <summary>The phone's time zone, for the morning's Upcoming at its hour.</summary>
    public string TimeZone { get; set; } = "UTC";

    public bool Films { get; set; } = true;
    public bool Shows { get; set; } = true;
    public bool Podcasts { get; set; } = true;
    public bool Recordings { get; set; } = true;
    public bool Requests { get; set; } = true;
    public bool Upcoming { get; set; }

    /// <summary>The hour (0–23, the phone's time) Upcoming is sent at.</summary>
    public int UpcomingHour { get; set; } = 9;

    /// <summary>The phone's date Upcoming was last sent on, so it goes once a day.</summary>
    public DateOnly? UpcomingSentOn { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A film or show a profile asked for from Flexarr, so it can be told when it's in.</summary>
public sealed class MediaRequest
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string ProfileId { get; set; }

    /// <summary><see cref="MediaKind.Movie"/> or <see cref="MediaKind.Series"/>.</summary>
    public MediaKind Kind { get; set; }

    public int TmdbId { get; set; }
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When it arrived (and the profile was told); null while it's awaited.</summary>
    public DateTime? FulfilledAt { get; set; }
}

/// <summary>How far one kind of news has been told, so a restart doesn't tell it again.</summary>
public sealed class NotificationCursor
{
    public required string Kind { get; set; }
    public DateTime Through { get; set; }
}
