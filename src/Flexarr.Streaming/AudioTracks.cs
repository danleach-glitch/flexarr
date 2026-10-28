using Flexarr.Core.Media;

namespace Flexarr.Streaming;

/// <summary>Which of a file's soundtracks plays when the viewer hasn't picked one.</summary>
public static class AudioTracks
{
    // Titles that mark a track as something other than the film's own soundtrack.
    private static readonly string[] Extras = ["commentary", "description", "descriptive", "narration", "narrated", "karaoke", "isolated score"];

    /// <summary>
    /// The track in <paramref name="language"/> to play: the film's soundtrack rather than a commentary or audio
    /// description, the file's default among those, else the first. Null when there's no language to match or no track in it.
    /// </summary>
    public static MediaStream? InLanguage(IReadOnlyList<MediaStream> streams, string? language)
    {
        if (language is null)
        {
            return null;
        }

        var matching = streams.Where(s => s.Type == StreamType.Audio && s.Language == language).OrderBy(s => s.Index).ToList();
        var main = matching.Where(s => !IsExtra(s)).ToList();
        var candidates = main.Count > 0 ? main : matching;
        return candidates.FirstOrDefault(s => s.IsDefault) ?? candidates.FirstOrDefault();
    }

    /// <summary>A commentary, audio description, narration or karaoke track, by its title.</summary>
    public static bool IsExtra(MediaStream stream) =>
        stream.Title is { } title && Extras.Any(word => title.Contains(word, StringComparison.OrdinalIgnoreCase));
}
