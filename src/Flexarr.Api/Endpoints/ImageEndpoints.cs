using System.Security.Cryptography;
using System.Text;
using Flexarr.Api.Configuration;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Flexarr.Api.Endpoints;

public static class ImageEndpoints
{
    /// <summary>How long an item's artwork source is remembered; a changed one also changes the URL's ?v=.</summary>
    private static readonly TimeSpan SourceTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Signs /images/remote links, from <see cref="FlexarrOptions.ImageSecret"/>. Static so the endpoints that write
    /// those links into their DTOs don't each need it passed in. Set once, by the first server in the process (only
    /// tests start more than one), so signing and checking always use the same key.
    /// </summary>
    private static byte[]? _remoteKey;

    /// <summary>
    /// A link to outside artwork (Coming soon, downloads, requests, collections) through /images/remote, signed so
    /// that only images the server handed out can be fetched through it. Null if it isn't artwork the server fetches.
    /// </summary>
    public static string? RemoteUrl(string? url) =>
        ArtworkUrl.TrustedRemote(url) is { } trusted ? $"/api/v1/images/remote?url={Uri.EscapeDataString(trusted)}&s={Signature(trusted)}" : null;

    private static string Signature(string trustedUrl)
    {
        var key = Volatile.Read(ref _remoteKey) ?? throw new InvalidOperationException("The image endpoints haven't been mapped.");
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(trustedUrl)).AsSpan(0, 16));
    }

    /// <summary>
    /// Cached, resized artwork. Anonymous so <c>&lt;img&gt;</c> tags and TV clients can load it directly;
    /// item ids are unguessable and artwork isn't sensitive.
    /// </summary>
    public static RouteGroupBuilder MapImageEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var secret = ((IEndpointRouteBuilder)api).ServiceProvider.GetRequiredService<IOptions<FlexarrOptions>>().Value.ImageSecret;
        Interlocked.CompareExchange(ref _remoteKey, Encoding.UTF8.GetBytes(secret ?? throw new InvalidOperationException("No image secret.")), null);

        var group = api.MapGroup("/images").WithTags("Images").AllowAnonymous();

        group.MapGet("/items/{id:guid}/{kind}", async (Guid id, string kind, int? w, string? v, FlexarrDbContext db, IMemoryCache memory, ImageCache cache, HttpContext http, CancellationToken ct) =>
        {
            // "wide": the backdrop with the title's logo on it (the Apple TV's Top Shelf), at the backdrop's ?v=.
            if (kind == "wide")
            {
                if (await SourceAsync(db, memory, id, "backdrop", v, ct) is not { Url: { } backdrop })
                {
                    return Results.NotFound();
                }

                var logo = (await SourceAsync(db, memory, id, "logo", v, ct))?.Url;
                return await ServeAsync(() => cache.GetWideAsync(backdrop, logo, w, ct), http);
            }

            if (kind is not ("poster" or "backdrop" or "logo" or "thumb"))
            {
                return Results.NotFound();
            }

            return await SourceAsync(db, memory, id, kind, v, ct) is { Url: { } url } ? await ServeAsync(cache, url, w, http, ct) : Results.NotFound();
        });

        // Artwork for things not in the library yet (Coming soon, downloads). Only images on known artwork hosts, so
        // this can't be used to make the server fetch arbitrary URLs, and without query strings, so it can't be used
        // to fill the cache with copies of one image. And only links the server signed (RemoteUrl), so nobody can
        // page through TMDB with it, using the server's bandwidth and pushing real artwork out of the cache.
        group.MapGet("/remote", async (string url, string? s, int? w, ImageCache cache, HttpContext http, CancellationToken ct) =>
            ArtworkUrl.TrustedRemote(url) is not { } trusted ? Results.BadRequest()
            : s is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(s), Encoding.ASCII.GetBytes(Signature(trusted))) ? Results.NotFound()
            : await ServeAsync(cache, trusted, w, http, ct));

        group.MapGet("/people/{id:guid}", async (Guid id, int? w, string? v, FlexarrDbContext db, IMemoryCache memory, ImageCache cache, PersonImages uploads,
            HttpContext http, CancellationToken ct) =>
        {
            // A ?v= newer than the one remembered means the photo changed since (on another server process, a refresh);
            // an older one (a page cached before the change) is answered from memory, not the database.
            var key = PersonSourceKey(id);
            if (!memory.TryGetValue(key, out ImageSource? source) || source is null || IsNewer(v, source.Version))
            {
                source = await ResolvePersonAsync(db, uploads, id, ct);
                if (source is not null)
                {
                    memory.Set(key, source, SourceTtl);
                }
            }

            return source?.Url is { } url ? await ServeAsync(cache, url, w ?? 185, http, ct) : Results.NotFound();
        });

        // A photo an admin could choose for a person: only one the server itself found for them, and only while it
        // remembers what it found (ten minutes after they were looked for), never a URL from the request. For admins
        // only, unlike other images: a stale candidate can be a local file the library check no longer covers. (The
        // web's <img> is on the same site, so it sends the session.)
        api.MapGet("/images/people/{id:guid}/candidates/{source}", async (Guid id, string source, int? w, IMemoryCache memory, ImageCache cache, HttpContext http,
            CancellationToken ct) =>
            PeopleEndpoints.CandidatePhoto(memory, id, source) is { } url
                ? await ServeAsync(() => cache.GetAsync(url, w ?? 342, ct), http, "private, max-age=600")
                : Results.NotFound())
            .WithTags("Images").RequireAuthorization(Security.FlexarrClaims.AdminPolicy);

        return api;
    }

    /// <summary>
    /// Walks up to the series for seasons and episodes that lack their own artwork. Metadata sources only supply
    /// https artwork, or artwork held by a configured *arr (Lidarr's); files on disk are only ever ones Flexarr found
    /// beside the media itself (a cover.jpg, a poster.jpg, an episode's -thumb.jpg, a frame of a home video). However a
    /// file:// URL got there, one outside the item's own library folders is never read, and art inside a media file
    /// (embedded://) is only taken from music and audiobooks.
    /// </summary>
    /// <returns>Null if there's no such item; otherwise its artwork (null if it has none) and its ?v= version.</returns>
    private static async Task<ImageSource?> ResolveAsync(FlexarrDbContext db, Guid id, string kind, CancellationToken ct)
    {
        string? version = null;
        for (Guid? current = id; current is { } itemId;)
        {
            var item = await db.MediaItems.AsNoTracking().Where(i => i.Id == itemId)
                .Select(i => new { i.ParentId, i.LibraryId, i.PosterUrl, i.BackdropUrl, i.LogoUrl, i.ThumbUrl, i.Kind, i.MetadataUpdatedAt, i.ArtworkChangedAt })
                .FirstOrDefaultAsync(ct);
            if (item is null)
            {
                return version is null ? null : new ImageSource(null, version);
            }

            // As ArtworkUrls.For writes it, from the item asked for (not the series it borrows from).
            version ??= ArtworkUrls.Version(item.MetadataUpdatedAt, item.ArtworkChangedAt).ToString(System.Globalization.CultureInfo.InvariantCulture);

            var url = kind switch
            {
                "poster" => item.PosterUrl,
                "backdrop" => item.BackdropUrl,
                "logo" => item.LogoUrl,
                "thumb" => item.ThumbUrl,
                _ => null,
            };
            if (url is not null && !ArtworkUrl.IsHttps(url) && !ArtworkUrl.IsArr(url) && !ArtworkUrl.IsLocal(url))
            {
                return new ImageSource(null, version);
            }

            if (ArtworkUrl.IsLocal(url) && !await InLibraryAsync(db, item.LibraryId, item.Kind, url!, ct))
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

    /// <summary>Whether a photo's ?v= (seconds) is newer than the one remembered; one that isn't a number never is.</summary>
    internal static bool IsNewer(string? v, string? remembered) =>
        long.TryParse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var asked)
        && (!long.TryParse(remembered, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var known) || asked > known);

    /// <summary>Where the person image endpoint remembers a person's photo comes from.</summary>
    public static string PersonSourceKey(Guid id) => $"image-source:person:{id}";

    /// <summary>
    /// A person's photo: from the web over https, one an admin uploaded (always the person's own file in the data
    /// folder, whatever the URL says), or a photo in their author folder, which is only read when it's in one of the
    /// folders of a library they have a credit in.
    /// </summary>
    /// <returns>Null if there's no such person; otherwise their photo (null if there's none to serve) and its ?v=.</returns>
    private static async Task<ImageSource?> ResolvePersonAsync(FlexarrDbContext db, PersonImages uploads, Guid id, CancellationToken ct)
    {
        var person = await db.People.AsNoTracking().Where(p => p.Id == id).Select(p => new { p.ImageUrl, p.PhotoSource, p.ImageChangedAt }).FirstOrDefaultAsync(ct);
        if (person is null)
        {
            return null;
        }

        var version = person.ImageChangedAt is { } changed ? ArtworkUrls.PersonVersion(changed) : null;
        var url = person.ImageUrl;
        if (url is null)
        {
            return new ImageSource(null, version);
        }

        if (person.PhotoSource == PersonSources.Upload)
        {
            return new ImageSource(File.Exists(uploads.PathFor(id)) ? uploads.UrlFor(id) : null, version);
        }

        if (ArtworkUrl.IsHttps(url))
        {
            return new ImageSource(url, version);
        }

        return url.StartsWith("file://", StringComparison.Ordinal) && await PersonInLibraryAsync(db, id, url, ct)
            ? new ImageSource(url, version)
            : new ImageSource(null, version);
    }

    /// <summary>Whether a local photo of a person is a file in a folder of one of the libraries they're credited in.</summary>
    private static async Task<bool> PersonInLibraryAsync(FlexarrDbContext db, Guid personId, string url, CancellationToken ct)
    {
        // Not a link, which could lead anywhere from inside the library.
        if (ArtworkUrl.LocalPath(url) is not { Length: > 0 } path || (File.Exists(path) && new FileInfo(path).LinkTarget is not null))
        {
            return false;
        }

        var libraries = db.Credits.Where(c => c.PersonId == personId).Select(c => c.MediaItem!.LibraryId);
        var folders = await db.LibraryFolders.AsNoTracking().Where(f => libraries.Contains(f.LibraryId)).Select(f => f.Path).ToListAsync(ct);
        return folders.Any(folder => PathComparison.IsUnder(path, folder));
    }

    /// <summary>
    /// Whether local artwork is a file in one of the library's folders (and, for art inside a media file, music's or a
    /// book's: the scanner takes a book's cover from its first file when the folder has none).
    /// </summary>
    private static async Task<bool> InLibraryAsync(FlexarrDbContext db, Guid libraryId, MediaKind kind, string url, CancellationToken ct)
    {
        if (url.StartsWith("embedded://", StringComparison.Ordinal) && kind is not (MediaKind.Artist or MediaKind.Album or MediaKind.Track or MediaKind.Audiobook))
        {
            return false;
        }

        // Not a link, which could lead anywhere from inside the library.
        if (ArtworkUrl.LocalPath(url) is not { Length: > 0 } path || (File.Exists(path) && new FileInfo(path).LinkTarget is not null))
        {
            return false;
        }

        var folders = await db.LibraryFolders.AsNoTracking().Where(f => f.LibraryId == libraryId).Select(f => f.Path).ToListAsync(ct);
        return folders.Any(folder => PathComparison.IsUnder(path, folder));
    }

    /// <summary>
    /// Where an item's artwork comes from. A page of posters asks for each one; remember where they come from rather
    /// than walking up to the series every time. A ?v= newer than what's remembered means the artwork changed, so it's
    /// looked up again.
    /// </summary>
    private static async Task<ImageSource?> SourceAsync(FlexarrDbContext db, IMemoryCache memory, Guid id, string kind, string? v, CancellationToken ct)
    {
        var key = $"image-source:{id}:{kind}";
        if (!memory.TryGetValue(key, out ImageSource? source) || source is null || (v is not null && v != source.Version))
        {
            source = await ResolveAsync(db, id, kind, ct);
            if (source is not null)
            {
                memory.Set(key, source, SourceTtl); // only for items that exist, so made-up ids can't fill memory
            }
        }

        return source;
    }

    private static Task<IResult> ServeAsync(ImageCache cache, string url, int? width, HttpContext http, CancellationToken ct) =>
        ServeAsync(() => cache.GetAsync(url, width, ct), http);

    /// <param name="fetch">The cached copy's path; asked for again (once) if a trim deletes it before it's opened.</param>
    private static async Task<IResult> ServeAsync(Func<Task<string?>> fetch, HttpContext http, string cacheControl = "public, max-age=2592000, immutable")
    {
        // Opened here rather than when the response is written: a trim of the cache can delete the file in between,
        // and then it's fetched again, once. Once open, a trim can't take it away.
        for (var attempt = 0; ; attempt++)
        {
            string? path;
            try
            {
                path = await fetch();
            }
            catch (ImageCacheBusyException)
            {
                // Not a missing image, so not a 404: one asked for again in a moment is likely to come.
                http.Response.Headers[HeaderNames.RetryAfter] = "5";
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (path is null)
            {
                return Results.NotFound();
            }

            FileStream file;
            try
            {
                file = File.Open(path, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read | FileShare.Delete, Options = FileOptions.Asynchronous });
            }
            catch (Exception ex) when (attempt == 0 && ex is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }

            http.Response.Headers[HeaderNames.CacheControl] = cacheControl;
            return Results.File(file, path.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "image/jpeg", enableRangeProcessing: false);
        }
    }
}
