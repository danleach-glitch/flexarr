using System.Text.Json.Nodes;
using Flexarr.Api.Configuration;
using Flexarr.Api.Profiles;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Notifications;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Books;

public enum BookAvailability
{
    Available,
    Requested,
    InLibrary,
}

/// <summary>A book a search found, and whether it can be requested.</summary>
/// <param name="ForeignBookId">Its id with the app's metadata source: what a request names.</param>
/// <param name="ItemId">The book in the library, when it's there.</param>
/// <param name="Image">The cover through the server's image proxy (web pages can't load other sites' images); null
/// when its host isn't one the proxy fetches from. Absent from servers before 0.58.</param>
public sealed record BookRequestResultDto(string ForeignBookId, string Title, string? Author, int? Year, string? Cover, string? Overview,
    BookAvailability Availability, Guid? ItemId, string? Image = null);

/// <param name="Query">The search the book was found with, to find it again.</param>
public sealed record BookRequestRequest(string ForeignBookId, string Query);

public sealed record ArrChoiceDto(int Id, string Name);

/// <param name="LibraryId">The audiobooks library the root folder is, when it's one Flexarr has.</param>
public sealed record ArrRootFolderDto(string Path, Guid? LibraryId);

/// <summary>Where requested books go: the app, its profiles and folders, and what's chosen.</summary>
public sealed record BookRequestSetupDto(bool Configured, string? Instance, IReadOnlyList<ArrChoiceDto> QualityProfiles, IReadOnlyList<ArrChoiceDto> MetadataProfiles,
    IReadOnlyList<ArrRootFolderDto> RootFolders, int? QualityProfileId, int? MetadataProfileId, string? RootFolderPath);

public sealed record BookRequestSetupRequest(int? QualityProfileId, int? MetadataProfileId, string? RootFolderPath, string? Instance = null);

public sealed record BookRequestLibraryRequest(Guid? LibraryId);

/// <summary>
/// Requesting books. Seerr doesn't do books, so Flexarr asks Readarr, Bookshelf or Chaptarr itself: it adds the book
/// (and its author, if new, following nothing else of theirs), monitors only that book and searches for it.
/// </summary>
public static class BookRequestEndpoints
{
    public static RouteGroupBuilder MapBookRequestEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/requests/books").WithTags("Requests").NotForAgeLimited();

        group.MapGet("/search", async (string q, IOptionsMonitor<FlexarrOptions> options, ArrClient arr, FlexarrDbContext db, CancellationToken ct) =>
        {
            if (await RefusedAsync(db, options.CurrentValue, ct) is { } refused)
            {
                return refused;
            }

            var instance = Instance(options.CurrentValue)!;
            JsonArray found;
            try
            {
                found = await arr.LookupBooksRawAsync(instance, q.Trim(), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
            {
                return Results.Problem($"{instance.Name} didn't answer the search.", statusCode: 502);
            }

            var books = found.OfType<JsonObject>().Where(b => Text(b, "foreignBookId") is not null).Take(20).ToList();
            var arrIds = books.Select(b => Text(b, "id")).Where(id => id is not null and not "0").ToList();
            var owned = await db.BookDetails.Where(d => d.ArrBookId != null && arrIds.Contains(d.ArrBookId) && d.ArrInstance == instance.Name && d.MediaItem!.MissingSince == null)
                .Select(d => new { d.ArrBookId, d.MediaItemId }).ToListAsync(ct);
            var foreign = books.Select(b => Text(b, "foreignBookId")!).ToList();
            var requested = (await db.MediaRequests.Where(r => r.Kind == MediaKind.Audiobook && r.FulfilledAt == null && r.BookForeignId != null && foreign.Contains(r.BookForeignId))
                .Select(r => r.BookForeignId!).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

            return Results.Ok(books.Select(b =>
            {
                var id = Text(b, "id");
                var mine = owned.FirstOrDefault(o => o.ArrBookId == id)?.MediaItemId;
                var fid = Text(b, "foreignBookId")!;
                var availability = mine is not null ? BookAvailability.InLibrary
                    : requested.Contains(fid) || (id is not null and not "0" && b["monitored"]?.GetValue<bool>() == true) ? BookAvailability.Requested
                    : BookAvailability.Available;
                var released = Text(b, "releaseDate");
                return new BookRequestResultDto(fid, Text(b, "title") ?? "Untitled", (b["author"] as JsonObject) is { } a ? Text(a, "authorName") : Text(b, "authorTitle"),
                    released is { Length: >= 4 } && int.TryParse(released[..4], System.Globalization.CultureInfo.InvariantCulture, out var y) ? y : null,
                    Cover(b), Text(b, "overview") is { } o ? Flexarr.Core.Books.BookSidecars.PlainText(o) : null, availability, mine,
                    Endpoints.ImageEndpoints.RemoteUrl(Cover(b)));
            }).ToList());
        });

        group.MapPost("/", async (BookRequestRequest request, IOptionsMonitor<FlexarrOptions> options, ArrClient arr, FlexarrDbContext db, CurrentProfile current, CancellationToken ct) =>
        {
            if (await RefusedAsync(db, options.CurrentValue, ct) is { } refused)
            {
                return refused;
            }

            var settings = options.CurrentValue;
            var instance = Instance(settings)!;
            try
            {
                var book = (await arr.LookupBooksRawAsync(instance, request.Query.Trim(), ct)).OfType<JsonObject>()
                    .FirstOrDefault(b => Text(b, "foreignBookId") == request.ForeignBookId);
                if (book is null)
                {
                    return Results.Problem("That book can't be found any more. Search again.", statusCode: 409);
                }

                var title = Text(book, "title");
                var author = (book["author"] as JsonObject) is { } a ? Text(a, "authorName") : null;
                string arrId;
                if (Text(book, "id") is { } known and not "0")
                {
                    // The app knows it already (an author it follows): wanted again, and looked for now.
                    await arr.SetBookMonitoredAsync(instance, known, monitored: true, ct);
                    arrId = known;
                }
                else
                {
                    var added = await arr.AddBookAsync(instance, await PrepareAsync(book, arr, instance, settings, db, current.Id, ct), ct);
                    arrId = Text(added, "id") ?? throw new InvalidOperationException($"{instance.Name} didn't say what it added.");
                }

                await arr.SearchBooksAsync(instance, [arrId], ct);
                var open = await db.MediaRequests.FirstOrDefaultAsync(r => r.ProfileId == current.Id && r.Kind == MediaKind.Audiobook
                    && r.BookForeignId == request.ForeignBookId && r.FulfilledAt == null, ct);
                if (open is null)
                {
                    db.MediaRequests.Add(new MediaRequest
                    {
                        ProfileId = current.Id, Kind = MediaKind.Audiobook, BookForeignId = request.ForeignBookId, BookArrId = arrId,
                        BookArrInstance = instance.Name,
                        Title = author is null ? title : $"{title} by {author}",
                    });
                }
                else
                {
                    // Asked for again, perhaps of another app since requests moved: it's that app's book now.
                    open.BookArrId = arrId;
                    open.BookArrInstance = instance.Name;
                }

                await db.SaveChangesAsync(ct);

                return Results.Accepted();
            }
            catch (HttpRequestException ex)
            {
                return Results.Problem($"{instance.Name} didn't accept the request ({(int?)ex.StatusCode ?? 0}): {ex.Message}", statusCode: 502);
            }
            catch (Exception ex) when (ex is TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException or FormatException
                && !ct.IsCancellationRequested)
            {
                // A timeout, or an answer that isn't the one expected (a proxy's page, an add that names no book).
                return Results.Problem($"{instance.Name} didn't answer the request as expected.", statusCode: 502);
            }
        });

        // Where requested books go, chosen by an admin.
        var setup = api.MapGroup("/requests/books/setup").WithTags("Requests").RequireAuthorization(FlexarrClaims.AdminPolicy);
        setup.MapGet("/", async (IOptionsMonitor<FlexarrOptions> options, ArrClient arr, FlexarrDbContext db, CancellationToken ct) =>
            Results.Ok(await SetupAsync(options.CurrentValue, arr, db, ct)));
        setup.MapPut("/", async (BookRequestSetupRequest request, IOptionsMonitor<FlexarrOptions> options, ArrClient arr, FlexarrDbContext db, CancellationToken ct) =>
        {
            await ServerConfigStore.UpdateAndWaitAsync(options, options.CurrentValue.DataDirectory, config => config["bookRequests"] = new JsonObject
            {
                ["instance"] = request.Instance ?? options.CurrentValue.BookRequests.Instance,
                ["qualityProfileId"] = request.QualityProfileId,
                ["metadataProfileId"] = request.MetadataProfileId,
                ["rootFolderPath"] = request.RootFolderPath,
            }, ct: ct);
            return Results.Ok(await SetupAsync(options.CurrentValue, arr, db, ct));
        });

        // Which audiobooks library a profile's requests go to (the profile itself, or an admin).
        api.MapPut("/profiles/{id}/book-requests", async (string id, BookRequestLibraryRequest request, FlexarrDbContext db, CurrentProfile current, CancellationToken ct) =>
        {
            if (id != current.Id && !current.IsAdmin)
            {
                return Results.Forbid();
            }

            if (await db.Profiles.FirstOrDefaultAsync(p => p.Id == id, ct) is not { } profile)
            {
                return Results.NotFound();
            }

            if (request.LibraryId is { } chosen && !await db.Libraries.IgnoreQueryFilters().AnyAsync(l => l.Id == chosen && l.Type == LibraryType.Audiobooks, ct))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["libraryId"] = ["That isn’t an audiobooks library."] });
            }

            profile.RequestAudiobooksLibraryId = request.LibraryId;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ProfileEndpoints.ToDto(profile, current));
        }).WithTags("Profiles");

        return api;
    }

    /// <summary>Whether this profile may request books here: an app to ask, and an audiobooks library it can see.</summary>
    private static async Task<IResult?> RefusedAsync(FlexarrDbContext db, FlexarrOptions options, CancellationToken ct)
    {
        if ((await LibraryVisibility.HiddenTypesAsync(db, ct)).Hides(MediaKind.Audiobook) || !await db.Libraries.AnyAsync(l => l.Type == LibraryType.Audiobooks, ct))
        {
            return Results.Problem("This profile can't request audiobooks.", statusCode: 403);
        }

        return Instance(options) is null
            ? Results.Problem("Requests for books need Readarr, Bookshelf or Chaptarr. Add one under Settings › Connections.", statusCode: 409)
            : null;
    }

    public static ArrInstance? Instance(FlexarrOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var readarrs = options.Arr.Where(a => a.Kind == ArrKind.Readarr).ToList();
        return readarrs.FirstOrDefault(a => a.Name == options.BookRequests.Instance) ?? readarrs.FirstOrDefault();
    }

    /// <summary>
    /// The lookup's book, ready to add: monitored and searched for, with its author (if new) following only it, in the
    /// profile's library or the chosen root folder, with the chosen profiles.
    /// </summary>
    private static async Task<JsonObject> PrepareAsync(JsonObject book, ArrClient arr, ArrInstance instance, FlexarrOptions options, FlexarrDbContext db, string profileId, CancellationToken ct)
    {
        var qualities = await arr.GetListAsync(instance, "qualityprofile", ct);
        var metadata = await arr.GetListAsync(instance, "metadataprofile", ct);
        var roots = await arr.GetListAsync(instance, "rootfolder", ct);
        var quality = options.BookRequests.QualityProfileId
            ?? qualities.OfType<JsonObject>().FirstOrDefault(q => Text(q, "name") is "Spoken" or "Audiobook" or "Audio")?["id"]?.GetValue<int>()
            ?? qualities.OfType<JsonObject>().FirstOrDefault()?["id"]?.GetValue<int>();
        var meta = options.BookRequests.MetadataProfileId ?? metadata.OfType<JsonObject>().FirstOrDefault(m => Text(m, "name") != "None")?["id"]?.GetValue<int>();
        var root = await RootFolderAsync(db, profileId, roots, options, ct);

        var copy = (JsonObject)book.DeepClone();
        copy["monitored"] = true;
        copy["anyEditionOk"] = true;
        copy["addOptions"] = new JsonObject { ["searchForNewBook"] = false }; // searched right after, once it's in
        var author = copy["author"] as JsonObject ?? [];
        author["qualityProfileId"] = quality;
        author["metadataProfileId"] = meta;
        author["rootFolderPath"] = root;
        author["monitored"] = true;
        author["monitorNewItems"] = "none";
        author["addOptions"] = new JsonObject { ["monitor"] = "none", ["searchForMissingBooks"] = false };
        copy["author"] = author;
        return copy;
    }

    /// <summary>The app's root folder for the profile's chosen library (through the path mappings), else the admin's choice, else its first.</summary>
    private static async Task<string?> RootFolderAsync(FlexarrDbContext db, string profileId, JsonArray roots, FlexarrOptions options, CancellationToken ct)
    {
        var paths = roots.OfType<JsonObject>().Select(r => Text(r, "path")).OfType<string>().ToList();
        var chosen = await db.Profiles.Where(p => p.Id == profileId).Select(p => p.RequestAudiobooksLibraryId).FirstOrDefaultAsync(ct);
        if (chosen is { } libraryId)
        {
            var folders = await db.LibraryFolders.Where(f => f.LibraryId == libraryId).Select(f => f.Path).ToListAsync(ct);
            if (paths.FirstOrDefault(p => folders.Any(f => PathComparison.Key(PathMapper.Map(p, options.PathMappings)) == PathComparison.Key(f))) is { } inLibrary)
            {
                return inLibrary;
            }
        }

        return options.BookRequests.RootFolderPath is { } root && paths.Contains(root) ? root : paths.FirstOrDefault();
    }

    private static async Task<BookRequestSetupDto> SetupAsync(FlexarrOptions options, ArrClient arr, FlexarrDbContext db, CancellationToken ct)
    {
        var chosen = options.BookRequests;
        if (Instance(options) is not { } instance)
        {
            return new BookRequestSetupDto(false, null, [], [], [], null, null, null);
        }

        try
        {
            static List<ArrChoiceDto> Choices(JsonArray list) => [.. list.OfType<JsonObject>().Where(o => o["id"] is not null)
                .Select(o => new ArrChoiceDto(o["id"]!.GetValue<int>(), Text(o, "name") ?? ""))];
            var bookLibraries = db.Libraries.IgnoreQueryFilters().Where(l => l.Type == LibraryType.Audiobooks).Select(l => l.Id);
            var folders = await db.LibraryFolders.Where(f => bookLibraries.Contains(f.LibraryId)).Select(f => new { f.Path, f.LibraryId }).ToListAsync(ct);
            var roots = (await arr.GetListAsync(instance, "rootfolder", ct)).OfType<JsonObject>().Select(r => Text(r, "path")).OfType<string>()
                .Select(p => new ArrRootFolderDto(p, folders.FirstOrDefault(f => PathComparison.Key(f.Path) == PathComparison.Key(PathMapper.Map(p, options.PathMappings)))?.LibraryId))
                .ToList();
            return new BookRequestSetupDto(true, instance.Name, Choices(await arr.GetListAsync(instance, "qualityprofile", ct)),
                Choices(await arr.GetListAsync(instance, "metadataprofile", ct)), roots, chosen.QualityProfileId, chosen.MetadataProfileId, chosen.RootFolderPath);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            return new BookRequestSetupDto(true, instance.Name, [], [], [], chosen.QualityProfileId, chosen.MetadataProfileId, chosen.RootFolderPath);
        }
    }

    private static string? Cover(JsonObject book) =>
        (book["images"] as JsonArray ?? []).OfType<JsonObject>().Where(i => Text(i, "coverType") is "cover" or "poster")
            .Select(i => Text(i, "remoteUrl") ?? Text(i, "url")).FirstOrDefault(u => u is not null && u.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        ?? Text(book, "remoteCover");

    private static string? Text(JsonObject? node, string name) =>
        node?[name] is JsonValue v && v.GetValueKind() is System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Number
            ? v.ToString() is { Length: > 0 } s ? s : null
            : null;
}
