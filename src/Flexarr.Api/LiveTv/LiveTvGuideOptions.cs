using System.Security.Cryptography;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.LiveTv;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Flexarr.Api.LiveTv;

/// <summary>
/// The guide's optional narrowing (audit perf-20/27/56): which channels to send. Absent, every channel the profile may
/// watch, as before.
/// </summary>
internal sealed record GuideChannels(HashSet<Guid>? Ids, string? Group, bool FavouritesOnly)
{
    public bool Narrows => Ids is not null || Group is not null || FavouritesOnly;

    /// <summary>Reads <c>channels</c> (ids, comma-separated), <c>group</c> and <c>favourites</c>; false when an id isn't one.</summary>
    public static bool TryParse(string? channels, string? group, bool? favourites, out GuideChannels parsed)
    {
        HashSet<Guid>? ids = null;
        if (!string.IsNullOrWhiteSpace(channels))
        {
            ids = [];
            foreach (var part in channels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Guid.TryParse(part, out var id))
                {
                    parsed = new GuideChannels(null, null, false);
                    return false;
                }

                ids.Add(id);
            }
        }

        parsed = new GuideChannels(ids, string.IsNullOrWhiteSpace(group) ? null : group, favourites == true);
        return true;
    }

    public bool Keeps(LiveChannel channel, HashSet<Guid> favourites) =>
        (Ids is null || Ids.Contains(channel.Id))
        && (Group is null || string.Equals(channel.Group, Group, StringComparison.Ordinal))
        && (!FavouritesOnly || favourites.Contains(channel.Id));
}

public static partial class LiveTvEndpoints
{
    /// <summary>
    /// The answer as JSON with a weak ETag of its bytes, or 304 when the asker already has those bytes. Only the ETag
    /// and Cache-Control headers are new: the body is what returning the object would have written.
    /// </summary>
    internal static IResult JsonWithETag<T>(T value, HttpContext http, JsonSerializerOptions options)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, options);
        var etag = new EntityTagHeaderValue($"\"{Convert.ToHexStringLower(SHA256.HashData(bytes))[..32]}\"", isWeak: true);
        http.Response.Headers.ETag = etag.ToString();
        http.Response.Headers.CacheControl = "private, no-cache";
        if (http.Request.GetTypedHeaders().IfNoneMatch.Any(t => t.Compare(etag, useStrongComparison: false)))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return Results.Bytes(bytes, "application/json; charset=utf-8");
    }

    /// <summary>
    /// GET /livetv/programmes/{id}: one programme as the guide gives it, with its description (for a guide fetched with
    /// <c>descriptions=false</c>). 404 when there's no such programme, or its channel isn't one the profile may watch.
    /// New in the release after 0.62.1.
    /// </summary>
    private static void MapProgrammeEndpoint(RouteGroupBuilder group) =>
        group.MapGet("/programmes/{id:long}", async (long id, FlexarrDbContext db, CancellationToken ct) =>
        {
            if (await db.LiveProgrammes.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) is not { } programme
                || !await db.LiveChannels.AnyAsync(c => c.Id == programme.ChannelId, ct))
            {
                return Results.NotFound();
            }

            var marks = await DvrEndpoints.Marks.LoadAsync(db, programme.Start, programme.End, ct);
            return Results.Ok(Marked(programme, marks));
        });
}
