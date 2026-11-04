using System.Globalization;

namespace Flexarr.Core.Localization;

/// <summary>A plural form, as CLDR names them.</summary>
public enum PluralCategory
{
    Zero,
    One,
    Two,
    Few,
    Many,
    Other,
}

/// <summary>
/// CLDR's operands of a number, computed exactly as scripts/i18n/plurals.py does, so every platform picks the same
/// form: from the number's shortest decimal form, a whole number having no fraction digits.
/// </summary>
internal readonly record struct PluralOperands(double N, double I, double V, double W, double F, double T, double E)
{
    public static PluralOperands Of(double x)
    {
        var n = Math.Abs(x);
        var s = n.ToString("R", CultureInfo.InvariantCulture);
        if (n % 1 == 0 || s.Contains('E', StringComparison.Ordinal) || !s.Contains('.', StringComparison.Ordinal))
        {
            return new PluralOperands(n, Math.Truncate(n), 0, 0, 0, 0, 0);
        }

        var whole = s[..s.IndexOf('.', StringComparison.Ordinal)];
        var frac = s[(s.IndexOf('.', StringComparison.Ordinal) + 1)..];
        var stripped = frac.TrimEnd('0');
        return new PluralOperands(
            n,
            double.Parse(whole, CultureInfo.InvariantCulture),
            frac.Length,
            stripped.Length,
            double.Parse(frac, CultureInfo.InvariantCulture),
            double.Parse(stripped.Length == 0 ? "0" : stripped, CultureInfo.InvariantCulture),
            0);
    }
}

public static class Plurals
{
    /// <summary>The CLDR locale a BCP 47 code uses: pt-PT, then pt, then und (everything is Other).</summary>
    public static string Locale(string code)
    {
        var c = code.Replace('_', '-');
        while (true)
        {
            if (PluralRules.ByLocale.ContainsKey(c))
            {
                return c;
            }

            var cut = c.LastIndexOf('-');
            if (cut < 0)
            {
                return "und";
            }

            c = c[..cut];
        }
    }

    public static PluralCategory Category(string code, double x) =>
        PluralRules.ByLocale[Locale(code)](PluralOperands.Of(x));
}
