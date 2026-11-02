using Flexarr.Core.Media;

namespace Flexarr.Core.Notifications;

/// <summary>
/// A phone that wants notifications, for one profile (the one it was on when they were turned on), and what about.
/// Its pushes go through a relay to Apple or Google, sealed with <see cref="Key"/> so only the phone can read them.
/// </summary>
public sealed class NotificationDevice
{
    /// <summary>Its sign-in's id; for a phone signed in with the server's API key, <c>token:</c> and its push token.</summary>
    public required string Id { get; set; }

    /// <summary>The sign-in it belongs to: when that's revoked, its notifications stop. Null with the API key.</summary>
    public string? SignInId { get; set; }

    public required string ProfileId { get; set; }

    /// <summary>Apple's push token for the phone, in hex, or an Android phone's Firebase registration token.</summary>
    public required string Token { get; set; }

    /// <summary><see cref="NotificationPlatforms.Apple"/> or <see cref="NotificationPlatforms.Android"/>: which push service the relay hands it to.</summary>
    public string Platform { get; set; } = NotificationPlatforms.Apple;

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

/// <summary>Who delivers a phone's notifications: Apple's push service, or Google's (Firebase Cloud Messaging).</summary>
public static class NotificationPlatforms
{
    public const string Apple = "apple";
    public const string Android = "android";
}

/// <summary>A film, show, album or artist a profile asked for from Flexarr, so it can be told when it's in.</summary>
public sealed class MediaRequest
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string ProfileId { get; set; }

    /// <summary>
    /// <see cref="MediaKind.Movie"/> or <see cref="MediaKind.Series"/> (through Seerr, by <see cref="TmdbId"/>), or
    /// <see cref="MediaKind.Album"/> or <see cref="MediaKind.Artist"/> (a follow; through Lidarr, by <see cref="MusicBrainzId"/>).
    /// </summary>
    public MediaKind Kind { get; set; }

    /// <summary>The film's or show's TMDB id; 0 for music.</summary>
    public int TmdbId { get; set; }

    /// <summary>For music: the album's MusicBrainz release group id, or the followed artist's MusicBrainz id.</summary>
    public string? MusicBrainzId { get; set; }

    /// <summary>For an album: its artist's MusicBrainz id, to find it by title when the scan hasn't learned its release group.</summary>
    public string? ArtistMusicBrainzId { get; set; }

    /// <summary>For music: the album's title or the artist's name, as asked for.</summary>
    public string? Title { get; set; }

    /// <summary>For an album: its artist's name, as asked for (so a list can say it's requested without asking Lidarr).</summary>
    public string? ArtistName { get; set; }

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When it arrived (and the profile was told); null while it's awaited. A followed artist's never is.</summary>
    public DateTime? FulfilledAt { get; set; }

    /// <summary>A followed artist's: when the profile was last told of a new album, so the next notice is about a later one.</summary>
    public DateTime? LastAnsweredAt { get; set; }
}

/// <summary>How far one kind of news has been told, so a restart doesn't tell it again.</summary>
public sealed class NotificationCursor
{
    public required string Kind { get; set; }
    public DateTime Through { get; set; }
}
