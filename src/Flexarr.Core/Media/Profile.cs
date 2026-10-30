using System.Globalization;
using System.Security.Cryptography;

namespace Flexarr.Core.Media;

/// <summary>
/// A person in the household with their own watch history, Continue Watching and preferences.
/// The main profile ("owner") always exists; existing history belongs to it.
/// </summary>
public sealed class Profile
{
    public required string Id { get; set; }
    public required string Name { get; set; }

    /// <summary>Avatar colour, #rrggbb.</summary>
    public string Color { get; set; } = "#a9c2ec";

    /// <summary>PBKDF2 hash of an optional PIN, as "iterations.salt.hash" (base64).</summary>
    public string? PinHash { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The Tailscale account (login name) that signs in as this profile over Tailscale.</summary>
    public string? TailscaleLogin { get; set; }

    /// <summary>Libraries this profile doesn't see anywhere: navigation, home, search, Discover or playback.</summary>
    public List<Guid> HiddenLibraryIds { get; set; } = [];

    /// <summary>When the profile picture last changed (null: no picture, show the initial).</summary>
    public DateTime? ImageUpdatedAt { get; set; }

    /// <summary>When an episode finishes, count down and play the next one.</summary>
    public bool AutoPlayNext { get; set; } = true;

    /// <summary>Jump past intros, recaps and credits without being asked. Skipping credits starts the countdown to the next episode as they begin.</summary>
    public bool SkipIntros { get; set; }

    public bool SkipRecaps { get; set; }

    public bool SkipCredits { get; set; }

    /// <summary>How big subtitles are drawn, on every device.</summary>
    public SubtitleSize SubtitleSize { get; set; } = SubtitleSize.Standard;

    /// <summary>How high subtitles sit over the picture, on every device.</summary>
    public SubtitlePosition SubtitlePosition { get; set; } = SubtitlePosition.Bottom;

    /// <summary>In films wider than the screen, subtitles go in the black bar below the picture rather than over it.</summary>
    public bool SubtitlesInBlackBars { get; set; }

    /// <summary>Phones keep the newest episodes of the podcasts this profile follows without being asked (on Wi-Fi).</summary>
    public bool PodcastAutoDownload { get; set; } = true;

    /// <summary>How many of each followed podcast's newest unplayed episodes phones keep.</summary>
    public int PodcastKeepEpisodes { get; set; } = 3;

    /// <summary>The speed podcasts play at.</summary>
    public double PodcastSpeed { get; set; } = 1;

    /// <summary>Podcasts has a place in the web app's sidebar, among the libraries.</summary>
    public bool PodcastsInSidebar { get; set; } = true;

    /// <summary>The TV library shows this profile requests are added to; null, wherever Seerr adds them by default.</summary>
    public Guid? RequestShowsLibraryId { get; set; }

    /// <summary>The film library films this profile requests are added to; null, wherever Seerr adds them by default.</summary>
    public Guid? RequestFilmsLibraryId { get; set; }

    /// <summary>Titles this profile adds to its watchlist that aren't in the library are requested through Seerr.</summary>
    public bool AutoRequestWatchlist { get; set; }

    /// <summary>
    /// The order of the libraries in every app's menu: library ids, and "live-tv" for Live TV where an app lists it
    /// among them. Libraries it doesn't mention (a new one) follow, by name. Empty: all by name.
    /// </summary>
    public List<string> LibraryOrder { get; set; } = [];

    /// <summary>
    /// The oldest age a title may be rated for this profile to see it (a step of <see cref="RatingAges"/>); null, no
    /// limit. Unrated titles are hidden from it unless a parent allows them or their library.
    /// </summary>
    public int? MaxRatingAge { get; set; }

    /// <summary>
    /// A child's profile: the simpler app, no requests, and leaving it (choosing another profile) takes the main
    /// profile's PIN. Always has an age limit.
    /// </summary>
    public bool IsKids { get; set; }

    public bool IsMain => Id == PlaybackState.DefaultUser;

    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    public void SetPin(string? pin)
    {
        if (string.IsNullOrEmpty(pin))
        {
            PinHash = null;
            return;
        }

        const int iterations = 100_000;
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, 32);
        PinHash = string.Create(CultureInfo.InvariantCulture, $"{iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}");
    }

    public bool CheckPin(string? pin)
    {
        if (PinHash is null)
        {
            return true;
        }

        if (string.IsNullOrEmpty(pin))
        {
            return false;
        }

        var parts = PinHash.Split('.');
        var iterations = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(pin, Convert.FromBase64String(parts[1]), iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public enum SubtitleSize
{
    Small,
    Standard,
    Large,
    ExtraLarge,
}

/// <summary>Where subtitles sit: at the foot of the picture, raised a little or a lot, or at the top.</summary>
public enum SubtitlePosition
{
    Bottom,
    Raised,
    Higher,
    Top,
}
