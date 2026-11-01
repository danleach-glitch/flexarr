using System.Globalization;
using System.Text.RegularExpressions;

namespace Flexarr.Core.Books;

/// <summary>What a book's place on disk says about it, before its tags or sidecars are read.</summary>
/// <param name="Key">
/// The book's path, which identifies it across scans: its folder, or for a book that's a single file beside others
/// (Author/Title.m4b) the file itself.
/// </param>
/// <param name="Title">The book's title from its folder or file name, with numbers, years and ids taken off.</param>
/// <param name="Authors">Authors from the author folder ("Lee Child, Andrew Child" is two).</param>
/// <param name="Series">The series folder's name, for Author/Series/Title.</param>
/// <param name="Sequence">The book's number in its series, as written ("1", "1.5", "1-3").</param>
/// <param name="Year">A year in the folder name ("2004 - Title", "Title (2004)").</param>
/// <param name="Narrator">A narrator in braces ("Title {Ray Porter}").</param>
/// <param name="Asin">An Audible id in square brackets ("[B0DPXK6Y7B]").</param>
/// <param name="Disc">The file's disc, from a CD/Disc/Part folder or a "1-01" file name.</param>
public sealed record BookPlace(
    string Key, string Title, IReadOnlyList<string> Authors, string? Series, string? Sequence, int? Year, string? Narrator, string? Asin, int? Disc);

/// <summary>
/// Works out which book an audio file belongs to from its path. A folder of audio files is one book, and folders named
/// like discs (CD1, Disc 2, Part 3) are parts of the book above them. Accepted layouts:
/// <code>
/// Title/…                         Author/Title/…               Author/Series/1 - Title/…
/// Author/Series/Vol. 1.5 - 2004 - Title {Narrator}/…           Author/Title/CD1/…
/// Author/Title.m4b (beside other books)                        Author - Title [ASIN].m4b
/// </code>
/// </summary>
public static partial class BookLayout
{
    /// <summary>Formats that hold a whole book in one file, so several side by side are several books.</summary>
    public static readonly IReadOnlySet<string> SingleFileBookExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".m4b", ".aax", ".aaxc" };

    /// <param name="root">The library folder.</param>
    /// <param name="filePath">An audio file under <paramref name="root"/>.</param>
    /// <param name="audioFilesIn">
    /// The audio files directly in a folder (names only), to tell a folder of one book's files from a folder of
    /// single-file books.
    /// </param>
    public static BookPlace Locate(string root, string filePath, Func<string, IReadOnlyCollection<string>> audioFilesIn)
    {
        ArgumentNullException.ThrowIfNull(audioFilesIn);
        root = root.TrimEnd('/');
        var directory = Path.GetDirectoryName(filePath)!;
        int? disc = null;
        if (directory.Length > root.Length && DiscFolder().Match(Path.GetFileName(directory)) is { Success: true } d)
        {
            disc = int.Parse(d.Groups["n"].Value, CultureInfo.InvariantCulture);
            directory = Path.GetDirectoryName(directory)!;
        }

        disc ??= DiscFromFileName(Path.GetFileNameWithoutExtension(filePath));

        // The file is a book of its own when it's straight in the library folder, or one of several whole-book files
        // side by side (Author/Title.m4b, Author/Other Title.m4b).
        var alone = directory.Length <= root.Length;
        if (!alone && SingleFileBookExtensions.Contains(Path.GetExtension(filePath)))
        {
            var files = audioFilesIn(directory);
            alone = files.Count > 1 && files.All(f => SingleFileBookExtensions.Contains(Path.GetExtension(f)));
        }

        var key = alone ? filePath : directory;
        return Describe(root, key, alone) with { Disc = disc };
    }

    /// <summary>What a book's path says about it.</summary>
    /// <param name="root">The library folder.</param>
    /// <param name="key">The book's path (see <see cref="BookPlace.Key"/>).</param>
    /// <param name="isFile">The book is a single file rather than a folder.</param>
    public static BookPlace Describe(string root, string key, bool isFile)
    {
        root = root.TrimEnd('/');
        var relative = Path.GetRelativePath(root, key).Split(Path.DirectorySeparatorChar);
        var name = isFile ? Path.GetFileNameWithoutExtension(key) : relative[^1];
        var folders = relative[..^1];

        var authors = folders.Length > 0 ? SplitAuthors(folders[0]) : [];
        var series = folders.Length > 1 ? Clean(folders[^1]) : null;
        var parsed = ParseName(name, authors, inSeries: series is not null);
        return new BookPlace(key, parsed.Title, parsed.Authors ?? authors, series, parsed.Sequence, parsed.Year, parsed.Narrator, parsed.Asin, null);
    }

    /// <summary>The disc <paramref name="filePath"/> of the book at <paramref name="key"/> is on, if its folder or name says.</summary>
    public static int? DiscOf(string key, string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        var directory = Path.GetDirectoryName(filePath)!;
        if (directory != key && DiscFolder().Match(Path.GetFileName(directory)) is { Success: true } d)
        {
            return int.Parse(d.Groups["n"].Value, CultureInfo.InvariantCulture);
        }

        return DiscFromFileName(Path.GetFileNameWithoutExtension(filePath));
    }

    private sealed record Parsed(string Title, string? Sequence, int? Year, string? Narrator, string? Asin, IReadOnlyList<string>? Authors);

    /// <summary>Takes a book folder or file name apart: "Vol. 1.5 - 2004 - Title {Narrator} [ASIN]".</summary>
    /// <param name="name">The folder or file name.</param>
    /// <param name="folderAuthors">The authors the author folder names.</param>
    /// <param name="inSeries">The book is in a series folder, so a number before its title ("3 The Title") can be its place.</param>
    private static Parsed ParseName(string name, IReadOnlyList<string> folderAuthors, bool inSeries)
    {
        string? asin = null, narrator = null, sequence = null;
        int? year = null;
        IReadOnlyList<string>? authors = null;

        if (AsinPart().Match(name) is { Success: true } a)
        {
            asin = a.Groups["asin"].Value.ToUpperInvariant();
            name = name.Remove(a.Index, a.Length);
        }

        if (NarratorPart().Match(name) is { Success: true } n)
        {
            narrator = n.Groups["n"].Value.Trim();
            name = name.Remove(n.Index, n.Length);
        }

        if (YearInParens().Match(name) is { Success: true } yp)
        {
            year = int.Parse(yp.Groups["y"].Value, CultureInfo.InvariantCulture);
            name = name.Remove(yp.Index, yp.Length);
        }

        // "Author - Title": the leading part names the folder's author, or is clearly a name and the rest a title.
        var parts = Dash().Split(name.Trim()).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count > 1 && folderAuthors.Count > 0 && folderAuthors.Any(f => SameName(f, parts[0])))
        {
            parts.RemoveAt(0);
        }
        else if (parts.Count > 1 && folderAuthors.Count == 0 && !Numbered().IsMatch(parts[0]) && !YearOnly().IsMatch(parts[0]))
        {
            authors = SplitAuthors(parts[0]);
            parts.RemoveAt(0);
        }

        // Leading "Vol. 1.5", "Book 3", "1" and "2004" parts, in any order: a bare year is a year, not a number.
        while (parts.Count > 1)
        {
            if (year is null && YearOnly().IsMatch(parts[0]))
            {
                year = int.Parse(parts[0], CultureInfo.InvariantCulture);
            }
            else if (sequence is null && Numbered().Match(parts[0]) is { Success: true } s)
            {
                sequence = s.Groups["n"].Value.Replace(',', '.');
            }
            else
            {
                break;
            }

            parts.RemoveAt(0);
        }

        // "3 The Title" in a series folder: its place in the series. Not outside one, and never a year: "2001 A Space
        // Odyssey" keeps its number.
        var title = string.Join(" - ", parts);
        if (sequence is null && inSeries && LeadingNumber().Match(title) is { Success: true } ln && !LooksLikeAYear(ln.Groups["n"].Value))
        {
            sequence = ln.Groups["n"].Value;
            title = title[ln.Length..];
        }

        title = Clean(title);
        return new Parsed(title.Length > 0 ? title : Clean(name), sequence, year, narrator, asin, authors);
    }

    /// <summary>"Lee Child, Andrew Child" or "Terry Pratchett &amp; Neil Gaiman" as separate authors; "Smith, John" stays one.</summary>
    public static IReadOnlyList<string> SplitAuthors(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var parts = AuthorSeparator().Split(value).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count == 2 && value.Contains(',', StringComparison.Ordinal) && !parts[1].Contains(' ', StringComparison.Ordinal) && !parts[0].Contains(' ', StringComparison.Ordinal))
        {
            return [value.Trim()]; // "Smith, John": surname first, not two people.
        }

        return parts.Count > 0 ? parts : [value.Trim()];
    }

    /// <summary>"1984", "2001": four digits that could be a year rather than a place in a series.</summary>
    private static bool LooksLikeAYear(string number) =>
        int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1000 and <= 2099;

    /// <summary>A disc number from a "1-01 Title" or "CD2 - 03" file name, as music rippers name them.</summary>
    private static int? DiscFromFileName(string name) =>
        DiscTrackName().Match(name) is { Success: true } m ? int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture) : null;

    private static bool SameName(string a, string b) =>
        string.Equals(Fold(a), Fold(b), StringComparison.Ordinal);

    private static string Fold(string value) => value.Normalize(System.Text.NormalizationForm.FormC).Trim().ToLowerInvariant();

    private static string Clean(string value) => Spaces().Replace(value.Replace('_', ' '), " ").Trim(' ', '-', '.', ',');

    [GeneratedRegex(@"^(?:cd|dis[ck]|part|pt)[\s._-]*(?<n>\d{1,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex DiscFolder();

    [GeneratedRegex(@"^(?:cd\s*)?(?<d>\d{1,2})-\d{1,3}\b", RegexOptions.IgnoreCase)]
    private static partial Regex DiscTrackName();

    [GeneratedRegex(@"\s*\[(?<asin>B0[0-9A-Z]{8}|\d{9}[\dX])\]", RegexOptions.IgnoreCase)]
    private static partial Regex AsinPart();

    [GeneratedRegex(@"\s*\{(?<n>[^}]+)\}")]
    private static partial Regex NarratorPart();

    [GeneratedRegex(@"\s*\((?<y>(?:19|20)\d{2})\)")]
    private static partial Regex YearInParens();

    [GeneratedRegex(@"\s+-\s+")]
    private static partial Regex Dash();

    [GeneratedRegex(@"^(?:(?:vol(?:ume)?|book|bk|no|#)\.?\s*)?(?<n>\d+(?:[.,]\d+)?(?:-\d+)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex Numbered();

    [GeneratedRegex(@"^(?:19|20)\d{2}$")]
    private static partial Regex YearOnly();

    [GeneratedRegex(@"^(?:(?:vol(?:ume)?|book|bk)\.?\s*)?(?<n>\d+(?:\.\d+)?)[.)]?\s+(?=\D)", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"\s*(?:;|&|\band\b|,)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorSeparator();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}
