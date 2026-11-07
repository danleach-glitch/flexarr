using Flexarr.Core.Media;
using Flexarr.Streaming;

namespace Flexarr.Api.Playback;

public static partial class PlaybackEndpoints
{
    /// <summary>
    /// Of <paramref name="fonts"/>, those the styled track <paramref name="key"/> asks for by name (audit perf-66). Kept
    /// whenever it can't tell: every font when the track or its script can't be read or names none, and any font whose
    /// own names can't be read (a WOFF, say).
    /// </summary>
    private static async Task<IReadOnlyList<AttachedFont>> FontsNamedByAsync(MediaFile file, PlaybackSession session, string key,
        IReadOnlyList<AttachedFont> fonts, SubtitleCache subtitles, TranscodeSettings settings, CancellationToken ct)
    {
        if (fonts.Count == 0 || session.Subtitles.FirstOrDefault(s => s.Key == key) is not { IsStyled: true } track)
        {
            return fonts;
        }

        var script = await Subtitles.StyledAsync(file, track, settings, ct);
        if (script is null)
        {
            // As the .ass endpoint does: converting again keeps a styled copy.
            await subtitles.GetAsync(file, session.Subtitles, track, ct, styled: true);
            script = await Subtitles.StyledAsync(file, track, settings, ct);
        }

        return script is null ? fonts : await FontNames.UsedByAsync(file, fonts, script, settings, ct);
    }
}
