using System.Globalization;
using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Core.Scanning;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Endpoints;

/// <summary>
/// An album or artist a library one could be, for Fix match. Provider is <c>lidarr</c> (one Lidarr has, named by
/// <see cref="Instance"/>) or <c>deezer</c>. <see cref="Cover"/> is the picture to show; <see cref="CoverValue"/> is
/// what to send back to choose it as the cover. <see cref="InLibrary"/>: another album or artist already matched to
/// it. <see cref="Followed"/>: an artist Lidarr follows. Added in 0.59.
/// </summary>
public sealed record MusicMatchDto(
    string Provider,
    string Id,
    string? Instance,
    string Title,
    string? Artist,
    int? Year,
    int? Tracks,
    string? Type,
    string? Cover,
    string? CoverValue,
    bool Current,
    Guid? InLibrary,
    bool Followed = false);

/// <summary>
/// The match chosen. Cover: null for the match's own, <c>current</c> to keep the one there, or a candidate's
/// <see cref="MusicMatchDto.CoverValue"/>; either of the last two is kept through refreshes.
/// </summary>
public sealed record MusicMatchRequest(string Provider, string Id, string? Instance = null, string? Cover = null);

/// <summary>
/// Fix match for music: which album or artist a library one really is, from Lidarr's (when connected) or Deezer's.
/// Nothing is added to, followed in or changed in Lidarr. The choice is locked, so refreshes keep it; Match
/// automatically again (DELETE) hands it back to its tags and Lidarr's own pairing. The main profile's, as films' is.
/// </summary>
public static class MusicMatchEndpoints
{
    public const string KeepCover = "current";
    private const int MaxResults = 20;

    public static RouteGroupBuilder MapMusicMatchEndpoints(this RouteGroupBuilder api)
    {
        var item = api.MapGroup("/items/{id:guid}").WithTags("Match").RequireAuthorization(FlexarrClaims.MainProfilePolicy);

        item.MapGet("/music-match", async (Guid id, string? q, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, ArrClient arr, DeezerClient deezer,
            CancellationToken ct) =>
        {
            var entity = await db.MediaItems.AsNoTracking().Include(i => i.Parent).FirstOrDefaultAsync(i => i.Id == id && (i.Kind == MediaKind.Album || i.Kind == MediaKind.Artist), ct);
            if (entity is null)
            {
                return Results.NotFound();
            }

            var term = q is { Length: > 0 } ? q.Trim() : entity.Kind == MediaKind.Album && entity.Parent is { } artist ? $"{artist.Title} {entity.Title}" : entity.Title;
            var lidarrs = options.CurrentValue.Arr.Where(a => a.Kind == ArrKind.Lidarr).ToList();
            var results = new List<MusicMatchDto>();
            var failed = new List<string>();

            foreach (var lidarr in lidarrs)
            {
                try
                {
                    results.AddRange(entity.Kind == MediaKind.Album
                        ? await LidarrAlbumsAsync(arr, lidarr, entity, term, q is { Length: > 0 }, ct)
                        : await LidarrArtistsAsync(arr, lidarr, entity, term, ct));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    failed.Add(lidarr.Name);
                }
            }

            try
            {
                results.AddRange(entity.Kind == MediaKind.Album
                    ? (await deezer.SearchAlbumsAsync(term, 15, ct)).Select(a => new MusicMatchDto("deezer", Id(a.Id), null, a.Title, a.Artist?.Name, a.Year,
                        a.NbTracks > 0 ? a.NbTracks : null, a.RecordType, ImageEndpoints.RemoteUrl(a.Cover), ArtworkUrl.TrustedRemote(a.Cover), entity.DeezerId == Id(a.Id), null))
                    : (await deezer.SearchArtistsAsync(term, 15, ct)).Select(a => new MusicMatchDto("deezer", Id(a.Id), null, a.Name, null, null,
                        a.NbAlbum > 0 ? a.NbAlbum : null, null, ImageEndpoints.RemoteUrl(a.Picture), ArtworkUrl.TrustedRemote(a.Picture), entity.DeezerId == Id(a.Id), null)));
            }
            catch (Exception ex) when (ex is HttpRequestException or DeezerException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                failed.Add("Deezer");
            }

            if (results.Count == 0 && failed.Count > 0)
            {
                return Results.Problem($"{string.Join(" and ", failed)} didn't answer the search.", statusCode: 502);
            }

            return Results.Ok(await WithLibraryAsync(db, entity, results, ct));
        });

        item.MapPost("/music-match", async (Guid id, MusicMatchRequest request, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, ArrClient arr,
            DeezerClient deezer, MetadataRefresher refresher, CancellationToken ct) =>
        {
            var entity = await db.MediaItems.Include(i => i.Genres).FirstOrDefaultAsync(i => i.Id == id && (i.Kind == MediaKind.Album || i.Kind == MediaKind.Artist), ct);
            if (entity is null)
            {
                return Results.NotFound();
            }

            var lidarr = request.Provider == "lidarr"
                ? options.CurrentValue.Arr.FirstOrDefault(a => a.Kind == ArrKind.Lidarr && (request.Instance is null || a.Name == request.Instance))
                : null;
            if (request.Provider is not ("lidarr" or "deezer") || request.Provider == "lidarr" && lidarr is null
                || !int.TryParse(request.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var lidarrId) && request.Provider == "lidarr"
                || !long.TryParse(request.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var deezerId) && request.Provider == "deezer")
            {
                return Results.Problem("That match isn't one Flexarr can use. Search again.", statusCode: 400);
            }

            if (request.Cover is { } wanted && wanted != KeepCover && ArtworkUrl.TrustedRemote(wanted) is null
                && !(ArtworkUrl.TryParseArr(wanted, out var coverInstance, out _) && options.CurrentValue.Arr.Any(a => a.Kind == ArrKind.Lidarr && a.Name == coverInstance)))
            {
                return Results.Problem("That cover can't be used. Choose another.", statusCode: 400);
            }

            var current = entity.MatchLocked && (request.Provider == "deezer" ? entity.DeezerId == request.Id
                : entity.MetadataSource == lidarr!.Name && entity.ArrId == lidarrId);
            var (poster, accent) = (entity.PosterUrl, entity.AccentColor);
            if (!current)
            {
                entity.DetailsLocked = false; // Edited by hand for the old match: the new one's details take over.
            }

            entity.ArtworkLocked = false;
            try
            {
                switch (entity.Kind, request.Provider)
                {
                    case (MediaKind.Album, "lidarr"):
                        await refresher.ApplyChosenAlbumAsync(entity, await arr.GetAlbumAsync(lidarr!, lidarrId, ct), lidarr!, ct);
                        break;
                    case (MediaKind.Album, _):
                        await refresher.ApplyChosenAlbumAsync(entity, await deezer.GetAlbumAsync(deezerId, ct), ct);
                        break;
                    case (_, "lidarr"):
                        var albums = await db.MediaItems.Where(i => i.ParentId == entity.Id && i.Kind == MediaKind.Album).Include(i => i.Genres).ToListAsync(ct);
                        await refresher.ApplyChosenArtistAsync(entity, albums, await arr.GetArtistAsync(lidarr!, lidarrId, ct), lidarr!, ct);
                        break;
                    default:
                        await refresher.ApplyChosenArtistAsync(entity, await deezer.GetArtistAsync(deezerId, ct), ct);
                        break;
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound)
            {
                return Results.Problem("That match can't be found any more. Search again.", statusCode: 404);
            }
            catch (DeezerException)
            {
                return Results.Problem("Deezer doesn't have that any more. Search again.", statusCode: 404);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return Results.Problem($"{lidarr?.Name ?? "Deezer"} didn't answer, so the match wasn't changed.", statusCode: 502);
            }

            if (request.Cover == KeepCover)
            {
                (entity.PosterUrl, entity.AccentColor) = (poster, accent);
                entity.ArtworkLocked = poster is not null;
            }
            else if (request.Cover is { } cover)
            {
                await refresher.SetChosenCoverAsync(entity, ArtworkUrl.TrustedRemote(cover) ?? cover, ct);
                entity.ArtworkLocked = true;
            }

            entity.MatchLocked = true;
            entity.MetadataUpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Match automatically again: back to the tags, then the folder is read again and Lidarr pairs it as it would.
        item.MapDelete("/music-match", async (Guid id, FlexarrDbContext db, ILibraryScanQueue queue, CancellationToken ct) =>
        {
            var entity = await db.MediaItems.FirstOrDefaultAsync(i => i.Id == id && (i.Kind == MediaKind.Album || i.Kind == MediaKind.Artist), ct);
            if (entity is null)
            {
                return Results.NotFound();
            }

            var folder = entity.Kind == MediaKind.Album ? entity.Path
                : await db.MediaItems.Where(i => i.ParentId == entity.Id && i.Path != null).Select(i => i.Path).FirstOrDefaultAsync(ct);
            MetadataRefresher.ForgetMusicMatch(entity, entity.Kind == MediaKind.Album ? LocalCover(entity.Path) : null);
            if (entity.Kind == MediaKind.Album)
            {
                await db.MediaItems.Where(i => i.Id == entity.ParentId).ExecuteUpdateAsync(u => u.SetProperty(i => i.MetadataUpdatedAt, (DateTime?)null), ct);
            }

            await db.SaveChangesAsync(ct);
            if (folder is not null)
            {
                await queue.EnqueueAsync(new LibraryScanRequest(folder, "Match automatically again"), ct);
            }

            return Results.NoContent();
        });

        return api;
    }

    private static readonly string[] CoverFiles = ["cover.jpg", "folder.jpg", "front.jpg", "album.jpg", "cover.png", "folder.png"];

    /// <summary>The cover file beside an album's tracks, as the scanner would find it.</summary>
    private static string? LocalCover(string? folder) =>
        folder is null ? null : CoverFiles.Select(f => Path.Combine(folder, f)).FirstOrDefault(File.Exists) is { } file ? $"file://{file}" : null;

    /// <summary>
    /// Lidarr's albums for a search: its own artist's (when Lidarr has the artist, and nothing was typed: the likely
    /// fixes are there), then those its search finds that it has. Albums it doesn't have can't be chosen.
    /// </summary>
    private static async Task<List<MusicMatchDto>> LidarrAlbumsAsync(ArrClient arr, ArrInstance lidarr, MediaItem album, string term, bool typed, CancellationToken ct)
    {
        var found = new List<LidarrAlbum>();
        if (!typed && album.Parent is { ArrId: { } artistId } artist && artist.MetadataSource == lidarr.Name)
        {
            var wanted = Loose(album.Title);
            found.AddRange((await arr.GetArtistAlbumsAsync(lidarr, artistId, ct))
                .OrderByDescending(a => Loose(a.Title) == wanted).ThenByDescending(a => a.Statistics?.TrackFileCount > 0).ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase).Take(12));
        }

        found.AddRange((await arr.LookupAlbumsAsync(lidarr, term, ct)).Where(a => a.Id > 0 && found.All(f => f.Id != a.Id)));
        return found.Take(MaxResults).Select(a =>
        {
            var cover = MetadataRefresher.ArrImage(lidarr, a.Images, "cover", $"mediacover/album/{a.Id}/");
            return new MusicMatchDto("lidarr", Id(a.Id), lidarr.Name, a.Title, a.Artist?.ArtistName, a.ReleaseDate?.Year, a.Statistics?.TotalTrackCount is > 0 and var n ? n : null,
                AlbumKind.From(a.AlbumType, a.SecondaryTypes), ImageEndpoints.ArrUrl(cover), cover, album.MetadataSource == lidarr.Name && album.ArrId == a.Id, null);
        }).ToList();
    }

    /// <summary>Lidarr's artists whose name is like the search, then those its search finds that it has.</summary>
    private static async Task<List<MusicMatchDto>> LidarrArtistsAsync(ArrClient arr, ArrInstance lidarr, MediaItem artist, string term, CancellationToken ct)
    {
        var wanted = Loose(term);
        var found = (await arr.GetArtistsAsync(lidarr, ct))
            .Where(a => wanted.Length > 0 && (Loose(a.ArtistName).Contains(wanted, StringComparison.Ordinal) || wanted.Contains(Loose(a.ArtistName), StringComparison.Ordinal)))
            .OrderByDescending(a => Loose(a.ArtistName) == wanted).ThenBy(a => a.ArtistName, StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        found.AddRange((await arr.LookupArtistsAsync(lidarr, term, ct)).Where(a => a.Id > 0 && found.All(f => f.Id != a.Id)));
        return found.Take(MaxResults).Select(a =>
        {
            var photo = MetadataRefresher.ArrImage(lidarr, a.Images, "poster", $"mediacover/artist/{a.Id}/");
            return new MusicMatchDto("lidarr", Id(a.Id), lidarr.Name, a.ArtistName, a.Disambiguation is { Length: > 0 } d ? d : null, null, null, null,
                ImageEndpoints.ArrUrl(photo), photo, artist.MetadataSource == lidarr.Name && artist.ArrId == a.Id, null, a.Monitored);
        }).ToList();
    }

    /// <summary>Marks the results another album or artist in the library is already matched to.</summary>
    private static async Task<List<MusicMatchDto>> WithLibraryAsync(FlexarrDbContext db, MediaItem entity, List<MusicMatchDto> results, CancellationToken ct)
    {
        var deezerIds = results.Where(r => r.Provider == "deezer").Select(r => r.Id).ToList();
        var arrIds = results.Where(r => r.Provider == "lidarr").Select(r => int.Parse(r.Id, CultureInfo.InvariantCulture)).ToList();
        var others = await db.MediaItems.AsNoTracking()
            .Where(i => i.Kind == entity.Kind && i.Id != entity.Id && ((i.DeezerId != null && deezerIds.Contains(i.DeezerId)) || (i.ArrId != null && arrIds.Contains(i.ArrId.Value))))
            .Select(i => new { i.Id, i.DeezerId, i.ArrId, i.MetadataSource }).ToListAsync(ct);
        return results.Select(r => r with
        {
            InLibrary = others.FirstOrDefault(o => r.Provider == "deezer" ? o.DeezerId == r.Id
                : o.MetadataSource == r.Instance && o.ArrId?.ToString(CultureInfo.InvariantCulture) == r.Id)?.Id,
        }).ToList();
    }

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static string Loose(string s) => new(s.Normalize(System.Text.NormalizationForm.FormD).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
