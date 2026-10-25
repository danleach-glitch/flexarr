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
    public static string Resolve(string? country)
    {
        if (Known(country) is { } chosen)
        {
            return chosen;
        }

        try
        {
            return Known(RegionInfo.CurrentRegion.TwoLetterISORegionName) ?? "us";
        }
        catch (ArgumentException)
        {
            return "us";
        }
    }

    /// <summary>The code in lower case when it's one of <see cref="All"/>; "uk" is taken for "gb".</summary>
    public static string? Known(string? code)
    {
        var lower = code?.Trim().ToLowerInvariant();
        lower = lower == "uk" ? "gb" : lower;
        return All.Any(c => c.Code == lower) ? lower : null;
    }
}
