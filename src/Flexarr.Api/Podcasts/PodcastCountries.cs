using System.Globalization;

namespace Flexarr.Api.Podcasts;

public sealed record PodcastCountry(string Code, string Name);

/// <summary>Countries Apple has podcast charts for, for choosing which Browse shows.</summary>
public static class PodcastCountries
{
    public static readonly IReadOnlyList<PodcastCountry> All =
    [
        new("ar", "Argentina"), new("au", "Australia"), new("at", "Austria"), new("be", "Belgium"), new("br", "Brazil"),
        new("ca", "Canada"), new("cl", "Chile"), new("co", "Colombia"), new("cz", "Czechia"), new("dk", "Denmark"),
        new("fi", "Finland"), new("fr", "France"), new("de", "Germany"), new("gr", "Greece"), new("hk", "Hong Kong"),
        new("hu", "Hungary"), new("in", "India"), new("id", "Indonesia"), new("ie", "Ireland"), new("il", "Israel"),
        new("it", "Italy"), new("jp", "Japan"), new("my", "Malaysia"), new("mx", "Mexico"), new("nl", "Netherlands"),
        new("nz", "New Zealand"), new("no", "Norway"), new("ph", "Philippines"), new("pl", "Poland"), new("pt", "Portugal"),
        new("ro", "Romania"), new("sa", "Saudi Arabia"), new("sg", "Singapore"), new("za", "South Africa"), new("kr", "South Korea"),
        new("es", "Spain"), new("se", "Sweden"), new("ch", "Switzerland"), new("tw", "Taiwan"), new("th", "Thailand"),
        new("tr", "Türkiye"), new("ae", "United Arab Emirates"), new("gb", "United Kingdom"), new("us", "United States"), new("vn", "Vietnam"),
    ];

    /// <summary>The chosen country if Apple has charts for it, else the server's region's, else the United States.</summary>
    public static string Resolve(string? country) => Known(country) ?? Detected() ?? "us";

    /// <summary>The server's region if it's one of <see cref="All"/>; null when it can't tell (a container with no locale).</summary>
    public static string? Detected()
    {
        try
        {
            return Known(RegionInfo.CurrentRegion.TwoLetterISORegionName);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whose names age limits go by: the chosen country's, else the server's region's. Not knowing the country means the
    /// UK's, unlike <see cref="Resolve"/>'s charts, so a server nobody has set doesn't rename its limits.
    /// </summary>
    public static Flexarr.Core.Media.RatingSystem RatingSystem(string? country) =>
        Flexarr.Core.Media.RatingAges.SystemFor(Known(country) ?? Detected());

    /// <summary>The code in lower case when it's one of <see cref="All"/>; "uk" is taken for "gb".</summary>
    public static string? Known(string? code)
    {
        var lower = code?.Trim().ToLowerInvariant();
        lower = lower == "uk" ? "gb" : lower;
        return All.Any(c => c.Code == lower) ? lower : null;
    }
}
