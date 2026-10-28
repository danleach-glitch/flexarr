using System.Reflection;

namespace Flexarr.Api.Updates;

/// <summary>
/// A Flexarr version: "0.51.0" for a release, "0.51.0-dev.1a2b3c4d" for a build of main between releases. Build metadata
/// after a "+" (the commit, which .NET adds to the informational version) is dropped.
/// </summary>
public sealed record ReleaseVersion(Version Number, string? Suffix) : IComparable<ReleaseVersion>
{
    /// <summary>The running server's version.</summary>
    public static ReleaseVersion Current { get; } = Parse(
        typeof(ReleaseVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")!;

    /// <summary>A build between releases, not a release: never offered an update, which could be older than it.</summary>
    public bool IsDevelopment => Suffix is not null;

    /// <summary>"0.51.0", "v0.51.0" or "0.51.0-dev.abc+commit"; null when it's none of those.</summary>
    public static ReleaseVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim().TrimStart('v', 'V');
        if (value.IndexOf('+', StringComparison.Ordinal) is var plus and >= 0)
        {
            value = value[..plus];
        }

        string? suffix = null;
        if (value.IndexOf('-', StringComparison.Ordinal) is var dash and >= 0)
        {
            suffix = value[(dash + 1)..];
            value = value[..dash];
        }

        return Version.TryParse(value, out var number) && number.Build >= 0 && number.Revision < 0 && suffix is not ""
            ? new ReleaseVersion(number, suffix)
            : null;
    }

    /// <summary>
    /// Releases by number. A development build of a number comes before that number's release (semver's rule), which
    /// is only used for ordering: a development build is never offered one.
    /// </summary>
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var byNumber = Number.CompareTo(other.Number);
        if (byNumber != 0)
        {
            return byNumber;
        }

        return (Suffix, other.Suffix) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => string.CompareOrdinal(Suffix, other.Suffix),
        };
    }

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;

    public override string ToString() => Suffix is null ? Number.ToString(3) : $"{Number.ToString(3)}-{Suffix}";
}
