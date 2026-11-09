using System.Globalization;

namespace Flexarr.Api.Podcasts;

public sealed record PodcastCountry(string Code, string Name);

/// <summary>The countries a server can be set to, which began as those Apple has podcast charts for.</summary>
public static class PodcastCountries
{
    private static readonly PodcastCountry[] WithCharts =
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

    /// <summary>Every country a server can be set to, by name.</summary>
    public static readonly IReadOnlyList<PodcastCountry> All = WithCharts.Concat(
    [
        new("bd", "Bangladesh"), new("bo", "Bolivia"), new("bg", "Bulgaria"), new("cr", "Costa Rica"), new("hr", "Croatia"),
        new("cy", "Cyprus"), new("do", "Dominican Republic"), new("ec", "Ecuador"), new("eg", "Egypt"), new("ee", "Estonia"),
        new("gh", "Ghana"), new("gt", "Guatemala"), new("is", "Iceland"), new("jm", "Jamaica"), new("jo", "Jordan"),
        new("kz", "Kazakhstan"), new("ke", "Kenya"), new("kw", "Kuwait"), new("lv", "Latvia"), new("lb", "Lebanon"),
        new("lt", "Lithuania"), new("lu", "Luxembourg"), new("mt", "Malta"), new("ma", "Morocco"), new("np", "Nepal"),
        new("ng", "Nigeria"), new("om", "Oman"), new("pk", "Pakistan"), new("pa", "Panama"), new("py", "Paraguay"),
        new("pe", "Peru"), new("pr", "Puerto Rico"), new("qa", "Qatar"), new("rs", "Serbia"), new("sk", "Slovakia"),
        new("si", "Slovenia"), new("lk", "Sri Lanka"), new("tt", "Trinidad and Tobago"), new("ua", "Ukraine"), new("uy", "Uruguay"),
        new("ve", "Venezuela"),
    ]).OrderBy(c => c.Name, StringComparer.InvariantCulture).ToList();

    /// <summary>The country whose podcast charts and directory to show: <paramref name="country"/>'s if Apple has them, else the United States'.</summary>
    public static string ChartsFor(string country) => WithCharts.Any(c => c.Code == country) ? country : "us";

    /// <summary>The chosen country, else the server's region's, else the United States.</summary>
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
    /// UK's, unlike <see cref="Resolve"/>, so a server that can't tell doesn't rename its limits. A server whose locale is
    /// en_US counts as in the US wherever it is, until a country is chosen.
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
