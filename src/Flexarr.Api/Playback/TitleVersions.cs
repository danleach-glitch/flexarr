using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Playback;

/// <summary>
/// A title's versions as the profile the context serves can see them: its own files and those of its linked copies.
/// Copies are looked up through the context's filter on purpose, so a hidden library or an age limit holds.
/// See docs/multi-versions.md.
/// </summary>
public static class TitleVersions
{
    /// <summary>The title itself and the copies of it this profile can see.</summary>
    public static async Task<List<Guid>> CopiesAsync(FlexarrDbContext db, MediaItem item, CancellationToken ct) =>
        item.LinkGroupId is { } group
            ? await db.MediaItems.Where(i => i.LinkGroupId == group).Select(i => i.Id).ToListAsync(ct)
            : [item.Id];

    /// <summary>The readable files of the title and its copies, with what playing them needs.</summary>
    public static async Task<List<MediaFile>> FilesAsync(FlexarrDbContext db, MediaItem item, CancellationToken ct)
    {
        var copies = await CopiesAsync(db, item, ct);
        return await db.MediaFiles.AsNoTracking().Include(f => f.Streams).Include(f => f.Markers)
            .Where(f => copies.Contains(f.MediaItemId) && f.ProbeError == null).ToListAsync(ct);
    }

    /// <summary>What the profile chose for the title, on whichever copy.</summary>
    public static async Task<VersionChoice?> ChoiceAsync(FlexarrDbContext db, MediaItem item, string profileId, CancellationToken ct)
    {
        var copies = await CopiesAsync(db, item, ct);
        return await db.VersionChoices.AsNoTracking().Where(c => c.ProfileId == profileId && copies.Contains(c.MediaItemId))
            .OrderByDescending(c => c.ChosenAt).FirstOrDefaultAsync(ct);
    }
}
