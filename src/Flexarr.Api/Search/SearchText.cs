using System.Globalization;
using System.Text;

namespace Flexarr.Api.Search;

/// <summary>
/// Forgiving text matching for search: ignores case, accents and punctuation ("amelie" finds Amélie, "spiderman" finds
/// Spider-Man) and allows a typo or two in longer words ("breking bad" finds Breaking Bad).
/// </summary>
public static class SearchText
{
    /// <summary>Lower case without accents, "&amp;" as "and", apostrophes dropped and other punctuation as spaces.</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        var space = true;
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || c is '\'' or '’' or '‘')
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else if (c == '&')
            {
                builder.Append(space ? "and " : " and ");
                space = true;
            }
            else if (!space)
            {
                builder.Append(' ');
                space = true;
            }
        }

        return builder.ToString().TrimEnd().Normalize(NormalizationForm.FormC);
    }

    /// <summary>A search as it's matched: its words, and the whole thing run together.</summary>
    public sealed record Query(string[] Words, string Compact)
    {
        public static Query Parse(string text)
        {
            var words = Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new Query(words, string.Concat(words));
        }
    }

    /// <summary>Text to search, normalized once: its words, and the whole thing run together.</summary>
    public sealed record Candidate(string[] Words, string Compact)
    {
        public static Candidate Of(string text)
        {
            var words = Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new Candidate(words, string.Concat(words));
        }
    }

    /// <summary>
    /// How far <paramref name="candidate"/> is from what was searched: 0 when it starts with it (ignoring spaces and
    /// punctuation), 1 when it contains it, more for each typo forgiven; null when it doesn't match. The last word
    /// searched may be unfinished, so it only has to start a word.
    /// </summary>
    public static int? Cost(Query query, Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidate);
        if (query.Compact.Length == 0)
        {
            return null;
        }

        if (candidate.Compact.StartsWith(query.Compact, StringComparison.Ordinal))
        {
            return 0;
        }

        if (candidate.Compact.Contains(query.Compact, StringComparison.Ordinal))
        {
            return 1;
        }

        // Too short to guess at: "bd" could be anything.
        if (query.Compact.Length < 4)
        {
            return null;
        }

        int? byWords = 0;
        for (var i = 0; i < query.Words.Length && byWords is not null; i++)
        {
            var best = BestWord(query.Words[i], candidate.Words, partial: i == query.Words.Length - 1);
            byWords = best is null ? null : byWords + best;
        }

        // Run together, for typos across where the spaces go ("spidermn").
        var whole = PrefixDistance(query.Compact, candidate.Compact, Allowed(query.Compact.Length));
        var cost = (byWords, whole) switch
        {
            (null, null) => (int?)null,
            (null, var w) => w,
            (var b, null) => b,
            var (b, w) => Math.Min(b!.Value, w!.Value),
        };
        return cost is null ? null : 2 + cost;
    }

    /// <summary>Typos forgiven in a word this long: none up to three letters, one up to seven, then two.</summary>
    private static int Allowed(int length) => length <= 3 ? 0 : length <= 7 ? 1 : 2;

    private static int? BestWord(string word, string[] candidates, bool partial)
    {
        var allowed = Allowed(word.Length);
        int? best = null;
        foreach (var candidate in candidates)
        {
            if (candidate.StartsWith(word, StringComparison.Ordinal) && (partial || candidate.Length == word.Length))
            {
                return 0;
            }

            if (allowed == 0)
            {
                continue;
            }

            var distance = partial ? PrefixDistance(word, candidate, allowed) : Distance(word, candidate, allowed);
            if (distance is not null && (best is null || distance < best))
            {
                best = distance;
            }
        }

        return best;
    }

    /// <summary>The fewest typos turning <paramref name="word"/> into the start of <paramref name="text"/>, if within <paramref name="allowed"/>.</summary>
    private static int? PrefixDistance(string word, string text, int allowed)
    {
        if (allowed == 0)
        {
            return null;
        }

        int? best = null;
        for (var length = Math.Max(1, word.Length - allowed); length <= Math.Min(text.Length, word.Length + allowed); length++)
        {
            var distance = Distance(word, text[..length], allowed);
            if (distance is not null && (best is null || distance < best))
            {
                best = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// Edits (insert, delete, substitute, or swap two neighbouring letters) between two words, or null when more than
    /// <paramref name="allowed"/>.
    /// </summary>
    private static int? Distance(string a, string b, int allowed)
    {
        if (Math.Abs(a.Length - b.Length) > allowed)
        {
            return null;
        }

        var rows = new int[3][];
        for (var r = 0; r < 3; r++)
        {
            rows[r] = new int[b.Length + 1];
        }

        for (var j = 0; j <= b.Length; j++)
        {
            rows[1][j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            // Row i lives at (i + 1) % 3, so row 0 is rows[1].
            var (twoBack, previous, current) = (rows[(i + 2) % 3], rows[i % 3], rows[(i + 1) % 3]);
            current[0] = i;
            var rowMin = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitute = a[i - 1] == b[j - 1] ? 0 : 1;
                var value = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + substitute);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    value = Math.Min(value, twoBack[j - 2] + 1);
                }

                current[j] = value;
                rowMin = Math.Min(rowMin, value);
            }

            if (rowMin > allowed)
            {
                return null;
            }
        }

        var result = rows[(a.Length + 1) % 3][b.Length];
        return result <= allowed ? result : null;
    }
}
