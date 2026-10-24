using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Net.Http.Headers;

namespace Flexarr.Api.Endpoints;

public static class ImageEndpoints
{
    /// <summary>How long an item's artwork source is remembered; a changed one also changes the URL's ?v=.</summary>
    private static readonly TimeSpan SourceTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Cached, resized artwork. Anonymous so <c>&lt;img&gt;</c> tags and TV clients can load it directly;
    /// item ids are unguessable and artwork isn't sensitive.
    /// </summary>
    public static RouteGroupBuilder MapImageEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/images").WithTags("Images").AllowAnonymous();

        group.MapGet("/items/{id:guid}/{kind}", async (Guid id, string kind, int? w, string? v, FlexarrDbContext db, IMemoryCache memory, ImageCache cache, HttpContext http, CancellationToken ct) =>
        {
            if (kind is not ("poster" or "backdrop" or "logo" or "thumb"))
            {
                return Results.NotFound();
            }

            // A page of posters asks for each one; remember where they come from rather than walking up to the series
            // every time. A ?v= newer than what's remembered means the artwork changed, so it's looked up again.
            var key = $"image-source:{id}:{kind}";
            if (!memory.TryGetValue(key, out ImageSource? source) || source is null || (v is not null && v != source.Version))
            {
                source = await ResolveAsync(db, id, kind, ct);
                if (source is not null)
                {
                    memory.Set(key, source, SourceTtl); // only for items that exist, so made-up ids can't fill memory
                }
            }

            return source?.Url is { } url ? await ServeAsync(cache, url, w, http, ct) : Results.NotFound();
        });

        // Artwork for things not in the library yet (Coming soon, downloads). Only images on known artwork hosts, so
        // this can't be used to make the server fetch arbitrary URLs, and without query strings, so it can't be used
        // to fill the cache with copies of one image.
        group.MapGet("/remote", async (string url, int? w, ImageCache cache, HttpContext http, CancellationToken ct) =>
            ArtworkUrl.TrustedRemote(url) is { } trusted
                ? await ServeAsync(cache, trusted, w, http, ct)
                : Results.BadRequest());

        group.MapGet("/people/{id:guid}", async (Guid id, int? w, FlexarrDbContext db, IMemoryCache memory, ImageCache cache, HttpContext http, CancellationToken ct) =>
        {
            var key = $"image-source:person:{id}";
            if (!memory.TryGetValue(key, out ImageSource? source) || source is null)
            {
                source = await db.People.Where(p => p.Id == id).Select(p => new ImageSource(p.ImageUrl, null)).FirstOrDefaultAsync(ct);
                if (source is not null)
                {
                    memory.Set(key, source, SourceTtl);
                }
            }

            return ArtworkUrl.IsHttps(source?.Url) ? await ServeAsync(cache, source!.Url!, w ?? 185, http, ct) : Results.NotFound();
        });

        return api;
    }

    /// <summary>
    /// Walks up to the series for seasons and episodes that lack their own artwork. Metadata sources only supply
    /// https artwork; files on disk are only used for music, where the scanner found them.
    /// </summary>
    /// <returns>Null if there's no such item; otherwise its artwork (null if it has none) and its ?v= version.</returns>
    private static async Task<ImageSource?> ResolveAsync(FlexarrDbContext db, Guid id, string kind, CancellationToken ct)
    {
        string? version = null;
        for (Guid? current = id; current is { } itemId;)
        {
            var item = await db.MediaItems.AsNoTracking().Where(i => i.Id == itemId)
                .Select(i => new { i.ParentId, i.PosterUrl, i.BackdropUrl, i.LogoUrl, i.ThumbUrl, i.Kind, i.MetadataUpdatedAt })
                .FirstOrDefaultAsync(ct);
            if (item is null)
            {
                return version is null ? null : new ImageSource(null, version);
            }

            // As ArtworkUrls.For writes it, from the item asked for (not the series it borrows from).
            version ??= ((item.MetadataUpdatedAt?.Ticks ?? 0) / 10_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);

            var url = kind switch
            {
                "poster" => item.PosterUrl,
                "backdrop" => item.BackdropUrl,
                "logo" => item.LogoUrl,
                "thumb" => item.ThumbUrl,
                _ => null,
            };
            if (url is not null && !ArtworkUrl.IsHttps(url) && !(ArtworkUrl.IsLocal(url) && item.Kind is MediaKind.Artist or MediaKind.Album or MediaKind.Track))
            {
                return new ImageSource(null, version);
            }

            if (url is not null || kind == "thumb")
            {
                return new ImageSource(url, version);
            }

            current = item.ParentId;
        }

        return new ImageSource(null, version);
    }

    private sealed record ImageSource(string? Url, string? Version);

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
