using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Streaming;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Downloads;

/// <summary>
/// The file a download is made from, the same each time it's asked: sizing it, preparing it, and telling the phone its
/// marks and length. It's worked out for the profile whose download it is, also from the background (whose context sees
/// every library), so another library's copy is only used if that profile can see it.
/// </summary>
internal static class DownloadSource
{
    public static async Task<MediaFile?> ForAsync(FlexarrDbContext db, Guid itemId, DownloadQuality quality, string profileId, CancellationToken ct)
    {
        var files = await FilesAsync(db, itemId, profileId, ct);
        return For(files, files.Count > 1 ? await ChoiceAsync(db, files, profileId, ct) : null, quality);
    }

    public static MediaFile? For(List<MediaFile> files, VersionChoice? choice, DownloadQuality quality) =>
        VersionPicker.ForDownload(files, choice, quality == DownloadQuality.Original ? null : DownloadPlanner.Size(quality).MaxHeight);

    public static async Task<List<MediaFile>> FilesAsync(FlexarrDbContext db, Guid itemId, string profileId, CancellationToken ct)
    {
        var item = await db.MediaItems.IgnoreQueryFilters().AsNoTracking().Where(i => i.Id == itemId).Select(i => new { i.LinkGroupId }).FirstOrDefaultAsync(ct);
        if (item is null)
        {
            return [];
        }

        List<Guid> copies = [itemId];
        if (item.LinkGroupId is { } group)
        {
            var hidden = await db.Profiles.AsNoTracking().Where(p => p.Id == profileId).Select(p => p.HiddenLibraryIds).FirstOrDefaultAsync(ct) ?? [];
            copies = await db.MediaItems.IgnoreQueryFilters().Where(i => i.LinkGroupId == group && i.MissingSince == null && !hidden.Contains(i.LibraryId)).Select(i => i.Id).ToListAsync(ct);
            copies.Add(itemId);
        }

        return await db.MediaFiles.AsNoTracking().Include(f => f.Streams).Include(f => f.Markers)
            .Where(f => copies.Contains(f.MediaItemId) && f.ProbeError == null && f.MissingSince == null).ToListAsync(ct);
    }

    public static async Task<VersionChoice?> ChoiceAsync(FlexarrDbContext db, List<MediaFile> files, string profileId, CancellationToken ct)
    {
        var copies = files.Select(f => f.MediaItemId).Distinct().ToList();
        return await db.VersionChoices.AsNoTracking().Where(c => c.ProfileId == profileId && copies.Contains(c.MediaItemId))
            .OrderByDescending(c => c.ChosenAt).FirstOrDefaultAsync(ct);
    }
}
