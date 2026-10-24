using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Flexarr.Api.Endpoints;

public static class ImageEndpoints
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "image.tmdb.org", "artworks.thetvdb.com", "thetvdb.com", "static.tvmaze.com", "assets.fanart.tv",
    };

    /// <summary>
    /// Cached, resized artwork. Anonymous so <c>&lt;img&gt;</c> tags and TV clients can load it directly;
    /// item ids are unguessable and artwork isn't sensitive.
    /// </summary>
    public static RouteGroupBuilder MapImageEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/images").WithTags("Images").AllowAnonymous();

        group.MapGet("/items/{id:guid}/{kind}", async (Guid id, string kind, int? w, FlexarrDbContext db, ImageCache cache, HttpContext http, CancellationToken ct) =>
        {
            var url = await ResolveAsync(db, id, kind, ct);
            return url is null ? Results.NotFound() : await ServeAsync(cache, url, w, http, ct);
        });

        // Artwork for things not in the library yet (Coming soon, downloads). Only known artwork hosts, so this
        // can't be used to make the server fetch arbitrary URLs.
        group.MapGet("/remote", async (string url, int? w, ImageCache cache, HttpContext http, CancellationToken ct) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && AllowedHosts.Contains(uri.Host)
                ? await ServeAsync(cache, url, w, http, ct)
                : Results.BadRequest());

        group.MapGet("/people/{id:guid}", async (Guid id, int? w, FlexarrDbContext db, ImageCache cache, HttpContext http, CancellationToken ct) =>
        {
            var url = await db.People.Where(p => p.Id == id).Select(p => p.ImageUrl).FirstOrDefaultAsync(ct);
            return url is null ? Results.NotFound() : await ServeAsync(cache, url, w ?? 185, http, ct);
        });

        return api;
    }

    /// <summary>Walks up to the series for seasons and episodes that lack their own artwork.</summary>
    private static async Task<string?> ResolveAsync(FlexarrDbContext db, Guid id, string kind, CancellationToken ct)
    {
        for (Guid? current = id; current is { } itemId;)
        {
            var item = await db.MediaItems.AsNoTracking().Where(i => i.Id == itemId)
                .Select(i => new { i.ParentId, i.PosterUrl, i.BackdropUrl, i.LogoUrl, i.ThumbUrl, i.Kind })
                .FirstOrDefaultAsync(ct);
            if (item is null)
            {
                return null;
            }

            var url = kind switch
            {
                "poster" => item.PosterUrl,
                "backdrop" => item.BackdropUrl,
                "logo" => item.LogoUrl,
                "thumb" => item.ThumbUrl,
                _ => null,
            };
            if (url is not null || kind == "thumb")
            {
                return url;
            }

            current = item.ParentId;
        }

        return null;
    }

    private static async Task<IResult> ServeAsync(ImageCache cache, string url, int? width, HttpContext http, CancellationToken ct)
    {
        var path = await cache.GetAsync(url, width, ct);
        if (path is null)
        {
            return Results.NotFound();
        }

        http.Response.Headers[HeaderNames.CacheControl] = "public, max-age=2592000, immutable";
        return Results.File(path, path.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "image/jpeg", enableRangeProcessing: false);
    }
}
