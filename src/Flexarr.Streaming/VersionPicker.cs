using Flexarr.Core.Media;

namespace Flexarr.Streaming;

/// <summary>How hard a file is to play on a given device, as far as can be told without opening it.</summary>
public enum PlaybackTier
{
    /// <summary>The player opens the file itself.</summary>
    AsItIs,

    /// <summary>The picture goes across untouched in a new container: next to nothing for the server, and no loss.</summary>
    Repackaged,

    /// <summary>The picture is converted.</summary>
    Converted,
}

/// <summary>
/// Which of a title's versions plays when no particular one was asked for (see docs/multi-versions.md). Repackaging
/// counts as playing without converting: an Apple TV should get a 4K MKV repackaged, not a 1080p MP4 as it is.
/// </summary>
public static class VersionPicker
{
    public static MediaFile? Pick(IReadOnlyList<MediaFile> files, VersionChoice? choice, int heightCap, Func<MediaFile, PlaybackTier> tierOf)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(tierOf);

        if (files.Count <= 1)
        {
            return files.Count == 0 ? null : files[0];
        }

        var tiers = new Dictionary<Guid, PlaybackTier>();
        PlaybackTier Tier(MediaFile file)
        {
            if (!tiers.TryGetValue(file.Id, out var tier))
            {
                tiers[file.Id] = tier = tierOf(file);
            }

            return tier;
        }

        var best = VersionRules.BestFirst(files).ToList();
        var chosen = choice is null ? null : Chosen(best, choice);

        // The automatic pick stays within one cut: the choice's, or the best file's.
        var cutOf = chosen ?? best[0];
        var cut = best.Where(f => ReferenceEquals(f, cutOf) || VersionRules.SameCut(f, cutOf)).ToList();
        if (chosen is not null && (Tier(chosen) != PlaybackTier.Converted || cut.All(f => Tier(f) == PlaybackTier.Converted)))
        {
            return chosen;
        }

        if (cut.Count == 1)
        {
            return cut[0];
        }

        var playable = cut.Where(f => Tier(f) != PlaybackTier.Converted).ToList();
        return playable.Count > 0
            ? BestAmong(playable, Tier)
            : Nearest(cut, heightCap);
    }

    /// <summary>
    /// The version a download is made from. At full quality that's the one chosen or the best. Going smaller, it's the
    /// smallest of that cut that still fills the size, so less is converted and nothing's made from a poorer picture.
    /// </summary>
    public static MediaFile? ForDownload(IReadOnlyList<MediaFile> files, VersionChoice? choice, int? heightWanted)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (files.Count <= 1)
        {
            return files.Count == 0 ? null : files[0];
        }

        var best = VersionRules.BestFirst(files).ToList();
        var chosen = choice is null ? null : Chosen(best, choice);
        var cutOf = chosen ?? best[0];
        if (heightWanted is not { } height)
        {
            return cutOf;
        }

        var cut = best.Where(f => ReferenceEquals(f, cutOf) || VersionRules.SameCut(f, cutOf)).ToList();
        var filling = cut.Where(f => Equivalent(f) >= height).ToList();
        return filling.Count > 0 ? filling.MinBy(Equivalent)! : cut[0];
    }

    /// <summary>
    /// The file a choice means: the one chosen or, if an upgrade replaced it, one with the same label and edition. A
    /// better copy arriving makes the chosen file a spare, and the choice moves to that copy, the one Version shows.
    /// </summary>
    public static MediaFile? Chosen(IReadOnlyList<MediaFile> files, VersionChoice choice)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(choice);

        var best = VersionRules.BestFirst(files).ToList();
        if (files.FirstOrDefault(f => f.Id == choice.MediaFileId) is { } file)
        {
            return VersionRules.Sort(best).Kept.FirstOrDefault(k => !VersionRules.Distinct(k, file)) ?? file;
        }

        return best.FirstOrDefault(f => VersionRules.Label(f) == choice.Label
            && string.Equals(VersionRules.Edition(f), choice.Edition, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The best picture; between two alike, the one that plays as it is.</summary>
    private static MediaFile BestAmong(List<MediaFile> files, Func<MediaFile, PlaybackTier> tier)
    {
        var top = Equivalent(files[0]);
        return files.Where(f => Equivalent(f) == top).OrderBy(tier).First();
    }

    /// <summary>Everything needs converting: the smallest source that still fills the cap makes the same picture for less work.</summary>
    private static MediaFile Nearest(List<MediaFile> files, int heightCap)
    {
        var filling = files.Where(f => Equivalent(f) >= heightCap).ToList();
        return filling.Count > 0 ? filling.MinBy(Equivalent)! : files[0];
    }

    private static int Equivalent(MediaFile file) => Math.Max(file.Height ?? 0, (file.Width ?? 0) * 9 / 16);
}
