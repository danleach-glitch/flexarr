using Flexarr.Api.Configuration;
using Flexarr.Api.Profiles;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Paths;
using Flexarr.Core.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Endpoints;

public static class LibraryEndpoints
{
    public static RouteGroupBuilder MapLibraryEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/libraries").WithTags("Libraries");

        // all=true (admins, for Settings) lists libraries hidden from the current profile too. Neither lists the DVR's
        // recordings while there's Live TV to find them in.
        group.MapGet("/", async (bool? all, FlexarrDbContext db, CurrentProfile profile, ScanProgress progress, IOptionsMonitor<FlexarrOptions> options,
            CancellationToken ct) =>
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
                .Select(l => ToDto(l, counts.GetValueOrDefault(l.Id), progress)).ToList();
        });

        group.MapGet("/{id:guid}", async (Guid id, FlexarrDbContext db, ScanProgress progress, CancellationToken ct) =>
            await db.Libraries.Include(l => l.Folders).FirstOrDefaultAsync(l => l.Id == id, ct) is { } library
                ? Results.Ok(ToDto(library, await db.MediaItems.CountAsync(i => i.LibraryId == id && i.ParentId == null, ct), progress))
                : Results.NotFound());

        group.MapPost("/", async (LibraryRequest request, FlexarrDbContext db, ILibraryScanQueue queue, ScanProgress progress, CancellationToken ct) =>
        {
            if (request.Type == LibraryType.Podcasts)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["type"] = ["Podcasts are built in: their folder is in Settings › Libraries."] });
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

            return Results.Created($"/api/v1/libraries/{library.Id}", ToDto(library, 0, progress));
        }).RequireAuthorization(FlexarrClaims.AdminPolicy);

        group.MapPut("/{id:guid}", async (Guid id, LibraryRequest request, FlexarrDbContext db, ILibraryScanQueue queue, ScanProgress progress, CancellationToken ct) =>
        {
            db.HiddenLibraryIds = []; // Settings manages every library
            var library = await db.Libraries.Include(l => l.Folders).FirstOrDefaultAsync(l => l.Id == id, ct);
            if (library is null)
            {
                return Results.NotFound();
            }

            if (request.Type != library.Type)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["type"] = ["A library's type can't be changed."] });
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

            // Items from a folder that's no longer part of the library go with it.
            foreach (var folder in removed)
            {
                var prefix = folder.Path + "/";
                await db.MediaItems.Where(i => i.LibraryId == id && i.ParentId == null && i.Path != null && i.Path.StartsWith(prefix)).ExecuteDeleteAsync(ct);
            }

            await db.SaveChangesAsync(ct);

            // Only new folders need a scan: a rename, or dropping a folder, leaves the rest as they were.
            foreach (var folder in added)
            {
                await queue.EnqueueAsync(new LibraryScanRequest(folder.Path, "Library updated"), ct);
            }

            return Results.Ok(ToDto(library, await db.MediaItems.CountAsync(i => i.LibraryId == id && i.ParentId == null, ct), progress));
        }).RequireAuthorization(FlexarrClaims.AdminPolicy);

        // Podcasts are part of Flexarr: their library stays (Settings › Libraries moves its folder).
        group.MapDelete("/{id:guid}", async (Guid id, FlexarrDbContext db, CancellationToken ct) =>
            await db.Libraries.IgnoreQueryFilters().Where(l => l.Id == id).Select(l => (LibraryType?)l.Type).FirstOrDefaultAsync(ct) switch
            {
                null => Results.NotFound(),
                LibraryType.Podcasts => Results.ValidationProblem(new Dictionary<string, string[]> { ["type"] = ["Podcasts can't be removed."] }),
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
        }).RequireAuthorization(FlexarrClaims.AdminPolicy);

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

    private static LibraryDto ToDto(Library library, int itemCount, ScanProgress progress)
    {
        var scan = progress.Current is { } p && p.LibraryId == library.Id ? new ScanStatusDto(p.Path, p.Total, p.Processed) : null;
        return new LibraryDto(library.Id, library.Name, library.Type, library.Folders.Select(f => f.Path).Order().ToList(),
            itemCount, library.LastScannedAt, scan, library.FineForKids);
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
            errors["name"] = ["Name is required."];
        }
        else if (request.Type == LibraryType.TvShows && request.Name.Trim() == RecordingsLibrary.Name)
        {
            // It'd be taken for the DVR's, and left out of the lists.
            errors["name"] = ["Recordings is the name of Live TV's recordings. Choose another."];
        }

        if (request.Folders is not { Count: > 0 })
        {
            errors["folders"] = ["Add at least one folder."];
        }
        else
        {
            var folders = Normalize(request.Folders);
            var missing = folders.Where(f => !Directory.Exists(f)).ToList();
            var taken = await db.LibraryFolders.Where(f => f.LibraryId != id).Select(f => f.Path).ToListAsync(ct);
            var overlapping = folders.Where(f => taken.Any(t => PathComparison.IsUnder(f, t) || PathComparison.IsUnder(t, f))).ToList();

            var messages = missing.Select(f => $"{f} doesn't exist or isn't readable.")
                .Concat(overlapping.Select(f => $"{f} overlaps a folder in another library."))
                .ToArray();
            if (messages.Length > 0)
            {
                errors["folders"] = messages;
            }
        }

        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }
}
