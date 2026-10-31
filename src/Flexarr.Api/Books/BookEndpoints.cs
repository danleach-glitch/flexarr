using Flexarr.Api.Endpoints;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Books;

/// <summary>Audiobook libraries: their shelves, lists, series, authors and narrators.</summary>
public static class BookEndpoints
{
    private const int MaxPage = 200;

    public static RouteGroupBuilder MapBookEndpoints(this RouteGroupBuilder api)
    {
        var library = api.MapGroup("/libraries/{id:guid}").WithTags("Audiobooks");

        library.MapGet("/book-shelves", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
            await IsBooksAsync(db, id, ct) ? Results.Ok(await BookQueries.ShelvesAsync(db, id, profile.Id, ct)) : Results.NotFound());

        library.MapGet("/books", async (Guid id, [AsParameters] BookFilter filter, int? skip, int? take, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (!await IsBooksAsync(db, id, ct))
            {
                return Results.NotFound();
            }

            var books = BookQueries.Filter(BookQueries.Books(db, id), filter, profile.Id);
            var total = await books.CountAsync(ct);
            var items = await books.Skip(Math.Max(0, skip ?? 0)).Take(Math.Clamp(take ?? 60, 1, MaxPage)).Summaries(profile.Id).ToListAsync(ct);
            return Results.Ok(new PagedDto<ItemSummaryDto>(total, items));
        });

        library.MapGet("/book-series", async (Guid id, string? search, int? skip, int? take, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
            await IsBooksAsync(db, id, ct)
                ? Results.Ok(await BookQueries.SeriesListAsync(db, id, search, Math.Max(0, skip ?? 0), Math.Clamp(take ?? 60, 1, MaxPage), profile.Id, ct))
                : Results.NotFound());

        // role: "author" (the default) or "narrator".
        library.MapGet("/book-people", async (Guid id, string? role, string? search, int? skip, int? take, FlexarrDbContext db, CancellationToken ct) =>
            await IsBooksAsync(db, id, ct)
                ? Results.Ok(await BookQueries.PeopleAsync(db, id, role == "narrator" ? CreditRole.Narrator : CreditRole.Author, search,
                    Math.Max(0, skip ?? 0), Math.Clamp(take ?? 60, 1, MaxPage), ct))
                : Results.NotFound());

        var item = api.MapGroup("/items/{id:guid}").WithTags("Audiobooks");

        // The book's files on its timeline, for a download or a player that plays it without a session. codecs: the
        // sounds the player decodes ("aac,mp3,flac"); files in any other are converted.
        item.MapGet("/book", async (Guid id, string? codecs, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (await BookAsync(db, id, ct) is not { } book)
            {
                return Results.NotFound();
            }

            var parts = await BookPlayback.PartsAsync(db, id, Codecs(codecs), ct);
            return Results.Ok(await BookPlayback.ManifestAsync(db, book, profile.Id, parts,
                p => $"/api/v1/items/{id}/book/files/{p.Index}{(p.Convert ? "?convert=aac" : "")}", ct));
        });

        item.MapGet("/book/files/{index:int}", async (Guid id, int index, string? convert, FlexarrDbContext db, BookFileConverter converter, CancellationToken ct) =>
        {
            if (await BookAsync(db, id, ct) is null)
            {
                return Results.NotFound();
            }

            var parts = await BookPlayback.PartsAsync(db, id, BookPlayback.BasicCodecs, ct);
            return index < 0 || index >= parts.Count ? Results.NotFound() : await ServeAsync(parts[index], convert == "aac", converter, ct);
        });

        item.MapPut("/progress", async (Guid id, BookProgressRequest request, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
            !double.IsFinite(request.PositionSeconds) ? Results.BadRequest()
            : await BookPlayback.RecordAsync(db, id, profile.Id, request, ct) is { } progress ? Results.Ok(progress) : Results.NotFound());

        item.MapPost("/finished", async (Guid id, BookFinishedRequest request, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
            await BookPlayback.SetFinishedAsync(db, id, profile.Id, request.Finished, ct) ? Results.NoContent() : Results.NotFound());

        item.MapPost("/listen-again", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
            await BookPlayback.ListenAgainAsync(db, id, profile.Id, ct) ? Results.NoContent() : Results.NotFound());

        item.MapGet("/bookmarks", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
            await BookAsync(db, id, ct) is null ? Results.NotFound() : Results.Ok(await BookQueries.Bookmarks(db, id, profile.Id).ToListAsync(ct)));

        item.MapPost("/bookmarks", async (Guid id, BookmarkRequest request, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (await BookAsync(db, id, ct) is null)
            {
                return Results.NotFound();
            }

            if (!double.IsFinite(request.PositionSeconds) || request.PositionSeconds < 0 || request.Note?.Length > 2000)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["bookmark"] = ["A place in the book, and a note of up to 2,000 characters."] });
            }

            var bookmark = new Core.Books.Bookmark { ProfileId = profile.Id, MediaItemId = id, PositionSeconds = request.PositionSeconds, Note = Note(request.Note) };
            db.Bookmarks.Add(bookmark);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new BookmarkDto(bookmark.Id, bookmark.PositionSeconds, bookmark.Note, bookmark.CreatedAt));
        });

        var bookmarks = api.MapGroup("/bookmarks/{id:guid}").WithTags("Audiobooks");
        bookmarks.MapPut("/", async (Guid id, BookmarkNoteRequest request, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (request.Note?.Length > 2000)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["note"] = ["Up to 2,000 characters."] });
            }

            if (await db.Bookmarks.FirstOrDefaultAsync(b => b.Id == id && b.ProfileId == profile.Id, ct) is not { } bookmark)
            {
                return Results.NotFound();
            }

            bookmark.Note = Note(request.Note);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new BookmarkDto(bookmark.Id, bookmark.PositionSeconds, bookmark.Note, bookmark.CreatedAt));
        });

        bookmarks.MapDelete("/", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
            await db.Bookmarks.Where(b => b.Id == id && b.ProfileId == profile.Id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound());

        // How this profile listens to books: default speed, skip steps, smart rewind.
        api.MapGet("/books/listening", async (FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
            Results.Ok(await BookPlayback.ListeningAsync(db, profile.Id, ct))).WithTags("Audiobooks");

        api.MapPut("/books/listening", async (BookListeningRequest request, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            int[] steps = [10, 15, 30, 45, 60];
            if (request.Speed is { } s && (!double.IsFinite(s) || s is < 0.5 or > 3.5)
                || request.SkipBackSeconds is { } b && !steps.Contains(b) || request.SkipForwardSeconds is { } f && !steps.Contains(f))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["listening"] = ["Speeds go from 0.5× to 3.5×; skips are 10, 15, 30, 45 or 60 seconds."] });
            }

            if (await db.Profiles.FirstOrDefaultAsync(p => p.Id == profile.Id, ct) is not { } me)
            {
                return Results.NotFound();
            }

            me.AudiobookSpeed = request.Speed is { } speed ? Math.Round(speed * 20) / 20 : me.AudiobookSpeed;
            me.AudiobookSkipBackSeconds = request.SkipBackSeconds ?? me.AudiobookSkipBackSeconds;
            me.AudiobookSkipForwardSeconds = request.SkipForwardSeconds ?? me.AudiobookSkipForwardSeconds;
            me.AudiobookSmartRewind = request.SmartRewind ?? me.AudiobookSmartRewind;
            await db.SaveChangesAsync(ct);
            return Results.Ok(await BookPlayback.ListeningAsync(db, profile.Id, ct));
        }).WithTags("Audiobooks");

        var admin = api.MapGroup("/settings/book-metadata").WithTags("Settings").RequireAuthorization(Security.FlexarrClaims.AdminPolicy);
        admin.MapGet("/", (Microsoft.Extensions.Options.IOptionsMonitor<Configuration.FlexarrOptions> options) => Results.Ok(MetadataSettings(options.CurrentValue)));
        admin.MapPut("/", async (BookMetadataSettingsRequest request, Microsoft.Extensions.Options.IOptionsMonitor<Configuration.FlexarrOptions> options) =>
        {
            if (request.Region is { } region && !Flexarr.Metadata.Books.BookMetadataSettings.Regions.Contains(region)
                || request.Providers is { } providers && (providers.Count == 0 || providers.Any(p => !Flexarr.Metadata.Books.BookMetadataSettings.AllProviders.Contains(p))))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["bookMetadata"] = ["Pick a known Audible store and at least one provider."] });
            }

            var current = options.CurrentValue.BookMetadata;
            var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watch = options.OnChange((_, _) => reloaded.TrySetResult());
            Configuration.ServerConfigStore.Update(options.CurrentValue.DataDirectory, config => config["bookMetadata"] = new System.Text.Json.Nodes.JsonObject
            {
                ["region"] = request.Region ?? current.Region,
                ["providers"] = request.Providers is { } chosen ? new System.Text.Json.Nodes.JsonArray([.. chosen.Distinct().Select(p => (System.Text.Json.Nodes.JsonNode?)p)]) : current.Providers is { } kept ? new System.Text.Json.Nodes.JsonArray([.. kept.Select(p => (System.Text.Json.Nodes.JsonNode?)p)]) : null,
                ["googleBooksKey"] = request.GoogleBooksKey is { } g ? (g.Trim() is { Length: > 0 } gk ? gk : null) : current.GoogleBooksKey,
                ["hardcoverToken"] = request.HardcoverToken is { } h ? (h.Trim() is { Length: > 0 } hk ? hk : null) : current.HardcoverToken,
            });
            await Task.WhenAny(reloaded.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            return Results.Ok(MetadataSettings(options.CurrentValue));
        });

        api.MapGet("/book-series/{id:guid}", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
            await BookQueries.SeriesAsync(db, id, profile.Id, ct) is { } series && await IsBooksAsync(db, series.LibraryId, ct)
                ? Results.Ok(series)
                : Results.NotFound())
            .WithTags("Audiobooks");

        return api;
    }

    private static BookMetadataSettingsDto MetadataSettings(Configuration.FlexarrOptions options)
    {
        var resolved = options.BookMetadata.Resolved(options.Podcasts.ResolvedCountry);
        return new BookMetadataSettingsDto(resolved.Region, resolved.Providers, resolved.GoogleBooksKey is not null, resolved.HardcoverToken is not null,
            Flexarr.Metadata.Books.BookMetadataSettings.Regions, Flexarr.Metadata.Books.BookMetadataSettings.AllProviders);
    }

    /// <summary>A book this profile can see.</summary>
    private static Task<MediaItem?> BookAsync(FlexarrDbContext db, Guid id, CancellationToken ct) =>
        db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id && i.Kind == MediaKind.Audiobook, ct);

    private static IReadOnlyList<string> Codecs(string? codecs) =>
        codecs is { Length: > 0 } ? codecs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : BookPlayback.BasicCodecs;

    private static string? Note(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    /// <summary>A book file, as it is or converted to AAC, with byte ranges so players can seek.</summary>
    public static async Task<IResult> ServeAsync(BookPart part, bool convert, BookFileConverter converter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(converter);
        if (!convert)
        {
            return File.Exists(part.Path) ? Results.File(part.Path, ContentType(part.Path), enableRangeProcessing: true) : Results.NotFound();
        }

        return await converter.ConvertAsync(part.FileId, part.Path, ct) is { } converted
            ? Results.File(converted, "audio/mp4", enableRangeProcessing: true)
            : Results.Problem("This book file couldn't be converted.", statusCode: 500);
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".m4b" or ".m4a" or ".mp4" or ".aac" => "audio/mp4",
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".ogg" or ".opus" => "audio/ogg",
        ".wav" => "audio/wav",
        _ => "application/octet-stream",
    };

    /// <summary>The library is an audiobooks library this profile can see (the context's filter hides the rest).</summary>
    private static Task<bool> IsBooksAsync(FlexarrDbContext db, Guid libraryId, CancellationToken ct) =>
        db.Libraries.AnyAsync(l => l.Id == libraryId && l.Type == LibraryType.Audiobooks, ct);
}
