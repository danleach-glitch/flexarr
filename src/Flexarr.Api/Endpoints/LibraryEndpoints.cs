using Flexarr.Api.Configuration;
using Flexarr.Api.Profiles;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Api.Scanning;
using Flexarr.Core.Libraries;
using Flexarr.Core.Paths;
using Flexarr.Core.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Flexarr.Api.Localization;
using Flexarr.Core.Localization;

namespace Flexarr.Api.Endpoints;

public static class LibraryEndpoints
{
    public static RouteGroupBuilder MapLibraryEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/libraries").WithTags("Libraries");

        // all=true (admins, for Settings) lists libraries hidden from the current profile too. Neither lists the DVR's
        // recordings while there's Live TV to find them in.
        group.MapGet("/", async (bool? all, FlexarrDbContext db, CurrentProfile profile, ScanProgress progress, IOptionsMonitor<FlexarrOptions> options,
            FolderWatcher watcher, CancellationToken ct) =>
        {
            if (all == true && profile.IsAdmin)
            {
                db.HiddenLibraryIds = [];
            }

            var counts = await db.MediaItems.Where(i => i.ParentId == null)
                .GroupBy(i => i.LibraryId).Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
            var libraries = await db.Libraries.Include(l => l.Folders).OrderBy(l => l.Name).ToListAsync(ct);
            var order = await db.Profiles.AsNoTracking().Where(p => p.Id == profile.Id).Select(p => p.LibraryOrder).FirstOrDefaultAsync(ct) ?? [];
            var liveTv = options.CurrentValue.LiveTv?.Account is not null;
            // A profile with an age limit isn't shown libraries with nothing in them for it: a child's menu is what they can watch.
            var limited = db.IsLimited();
            return InOrder(libraries, order).Where(l => !(liveTv && RecordingsLibrary.Is(l)) && (!limited || counts.GetValueOrDefault(l.Id) > 0))
                .Select(l => ToDto(l, counts.GetValueOrDefault(l.Id), progress, watcher, profile.IsMainProfile)).ToList();
        });

        group.MapGet("/{id:guid}", async (Guid id, FlexarrDbContext db, ScanProgress progress, CurrentProfile profile, FolderWatcher watcher, CancellationToken ct) =>
            await db.Libraries.Include(l => l.Folders).FirstOrDefaultAsync(l => l.Id == id, ct) is { } library
                ? Results.Ok(ToDto(library, await db.MediaItems.CountAsync(i => i.LibraryId == id && i.ParentId == null, ct), progress, watcher, profile.IsMainProfile))
                : Results.NotFound());

        group.MapPost("/", async (LibraryRequest request, FlexarrDbContext db, ILibraryScanQueue queue, ScanProgress progress, FolderWatcher watcher, CancellationToken ct) =>
        {
            if (request.Type == LibraryType.Podcasts)
            {
                return Problems.Field("Podcasts are built in: their folder is in Settings › Libraries.", field: "type");
            }

            if (await ValidateAsync(request, null, db, ct) is { } problem)
            {
                return problem;
            }

            var library = new Library
            {
                Name = request.Name.Trim(),
                Type = request.Type,
                Folders = Normalize(request.Folders).Select(p => new LibraryFolder { Path = p }).ToList(),
                FineForKids = request.FineForKids ?? false,
            };
            db.Libraries.Add(library);
            await db.SaveChangesAsync(ct);
            await EnqueueAsync(library, queue, "Library added", ct);

            return Results.Created($"/api/v1/libraries/{library.Id}", ToDto(library, 0, progress, watcher));
        }).RequireAuthorization(FlexarrClaims.AdminPolicy);

        group.MapPut("/{id:guid}", async (Guid id, LibraryRequest request, FlexarrDbContext db, ILibraryScanQueue queue, ScanProgress progress, FolderWatcher watcher,
            CancellationToken ct) =>
        {
            db.HiddenLibraryIds = []; // Settings manages every library
            var library = await db.Libraries.Include(l => l.Folders).FirstOrDefaultAsync(l => l.Id == id, ct);
            if (library is null)
            {
                return Results.NotFound();
            }

            if (request.Type != library.Type)
            {
                return Problems.Field("A library’s type can’t be changed.", field: "type");
            }

            if (await ValidateAsync(request, id, db, ct) is { } problem)
            {
                return problem;
            }

            var folders = Normalize(request.Folders);
            var removed = library.Folders.Where(f => !folders.Contains(f.Path)).ToList();
            var added = folders.Where(p => library.Folders.All(f => f.Path != p)).Select(p => new LibraryFolder { Path = p }).ToList();
            library.Name = request.Name.Trim();
            if (request.FineForKids is { } fine && fine != library.FineForKids)
            {
                library.FineForKids = fine;
                LibraryVisibility.KidsSettingsChanged();
            }
            library.Folders.RemoveAll(removed.Contains);
            library.Folders.AddRange(added);

            // Items from a folder that's no longer part of the library go with it: its files (missing ones too), then
            // whatever that leaves empty, artists and videos straight in the folder included.
            foreach (var folder in removed)
            {
                var prefix = folder.Path.TrimEnd('/') + "/";
                await db.MediaFiles.IgnoreQueryFilters()
                    .Where(f => f.MediaItem!.LibraryId == id && (f.Path == folder.Path || f.Path.StartsWith(prefix)))
                    .ExecuteDeleteAsync(ct);
            }

            if (removed.Count > 0)
            {
                await LibraryScanner.PruneEmptyItemsAsync(db, id, ct);
            }

            await db.SaveChangesAsync(ct);

            // Only new folders need a scan: a rename, or dropping a folder, leaves the rest as they were.
            foreach (var folder in added)
            {
                await queue.EnqueueAsync(new LibraryScanRequest(folder.Path, "Library updated"), ct);
            }

            return Results.Ok(ToDto(library, await db.MediaItems.CountAsync(i => i.LibraryId == id && i.ParentId == null, ct), progress, watcher));
        }).RequireAuthorization(FlexarrClaims.AdminPolicy);

        // Podcasts are part of Flexarr: their library stays (Settings › Libraries moves its folder).
        group.MapDelete("/{id:guid}", async (Guid id, FlexarrDbContext db, CancellationToken ct) =>
            await db.Libraries.IgnoreQueryFilters().Where(l => l.Id == id).Select(l => (LibraryType?)l.Type).FirstOrDefaultAsync(ct) switch
            {
                null => Results.NotFound(),
                LibraryType.Podcasts => Problems.Field("Podcasts can’t be removed.", field: "type"),
                _ => await db.Libraries.IgnoreQueryFilters().Where(l => l.Id == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound(),
            })
            .RequireAuthorization(FlexarrClaims.AdminPolicy);

        group.MapPost("/{id:guid}/scan", async (Guid id, bool? refreshMetadata, FlexarrDbContext db, ILibraryScanQueue queue, CancellationToken ct) =>
        {
            db.HiddenLibraryIds = []; // Settings manages every library
            var library = await db.Libraries.Include(l => l.Folders).FirstOrDefaultAsync(l => l.Id == id, ct);
            if (library is null)
            {
                return Results.NotFound();
            }

            await EnqueueAsync(library, queue, refreshMetadata == true ? "Metadata refresh" : "Manual scan", ct, refreshMetadata == true);
            return Results.Accepted();
        }).RequireAuthorization(FlexarrClaims.MainProfilePolicy);

        return api;
    }

    /// <summary>
    /// The libraries in the profile's chosen order; any it doesn't mention, such as one added since, after them in the
    /// order they came (by name).
    /// </summary>
    public static List<Library> InOrder(IReadOnlyList<Library> libraries, IReadOnlyList<string> order)
    {
        var rank = order.Select((id, i) => (id, i)).GroupBy(r => r.id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
        return libraries.Select((l, i) => (l, i))
            .OrderBy(r => rank.TryGetValue(r.l.Id.ToString(), out var at) ? at : order.Count + r.i)
            .Select(r => r.l).ToList();
    }

    /// <param name="paths">False for any profile but the main one: where the library's folders are on the server's disk, and the
    /// file a scan has reached, are left empty (the fields stay, as apps read them).</param>
    private static LibraryDto ToDto(Library library, int itemCount, ScanProgress progress, FolderWatcher watcher, bool paths = true)
    {
        var scan = progress.Current is { } p && p.LibraryId == library.Id ? new ScanStatusDto(paths ? p.Path : "", p.Total, p.Processed) : null;
        var folders = library.Folders.Select(f => f.Path).Order().ToList();
        // A folder the watcher hasn't looked at yet (just added, or the server just started) is told as it will be.
        var watch = paths
            ? folders.Select(f => watcher.StatusOf(f) is { } status ? new FolderWatchDto(f, status.State, status.Problem) : null).OfType<FolderWatchDto>().ToList()
            : null;
        return new LibraryDto(library.Id, library.Name, library.Type, paths ? folders : [], itemCount, library.LastScannedAt, scan, library.FineForKids, watch);
    }

    private static List<string> Normalize(IEnumerable<string> folders) =>
        folders.Select(f => Path.GetFullPath(f.Trim()).TrimEnd('/')).Distinct().ToList();

    private static async Task EnqueueAsync(Library library, ILibraryScanQueue queue, string reason, CancellationToken ct, bool forceMetadata = false)
    {
        foreach (var folder in library.Folders)
        {
            await queue.EnqueueAsync(new LibraryScanRequest(folder.Path, reason, forceMetadata), ct);
        }
    }

    private static async Task<IResult?> ValidateAsync(LibraryRequest request, Guid? id, FlexarrDbContext db, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = [Loc.T("Name is required.")];
        }
        else if (request.Type == LibraryType.TvShows && request.Name.Trim() == RecordingsLibrary.Name)
        {
            // It'd be taken for the DVR's, and left out of the lists.
            errors["name"] = [Loc.T("Recordings is the name of Live TV’s recordings. Choose another.")];
        }

        if (request.Folders is not { Count: > 0 })
        {
            errors["folders"] = [Loc.T("Add at least one folder.")];
        }
        else
        {
            var folders = Normalize(request.Folders);
            var missing = folders.Where(f => !Directory.Exists(f)).ToList();
            var taken = await db.LibraryFolders.Where(f => f.LibraryId != id).Select(f => f.Path).ToListAsync(ct);
            var overlapping = folders.Where(f => taken.Any(t => PathComparison.IsUnder(f, t) || PathComparison.IsUnder(t, f))).ToList();

            var messages = missing.Select(f => Loc.T("{folder} doesn’t exist or isn’t readable.", new { folder = f }))
                .Concat(overlapping.Select(f => Loc.T("{folder} overlaps a folder in another library.", new { folder = f })))
                .ToArray();
            if (messages.Length > 0)
            {
                errors["folders"] = messages;
            }
        }

        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }
}
