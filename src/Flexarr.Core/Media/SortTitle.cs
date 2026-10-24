namespace Flexarr.Core.Media;

public static class SortTitle
{
    private static readonly string[] Articles = ["the ", "a ", "an "];

    /// <summary>"The Nice Guys" → "nice guys", so lists sort the way people expect.</summary>
    public static string For(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        var lower = title.Trim().ToLowerInvariant();
        foreach (var article in Articles)
        {
            if (lower.StartsWith(article, StringComparison.Ordinal) && lower.Length > article.Length)
            {
                return lower[article.Length..];
            }
        }

        return lower;
    }

    /// <summary>Episodes and seasons sort numerically.</summary>
    public static string ForNumber(int number) => number.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
}
