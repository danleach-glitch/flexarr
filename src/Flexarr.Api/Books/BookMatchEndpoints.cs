using Flexarr.Api.Security;
using Flexarr.Core.Books;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Metadata;
using Flexarr.Metadata.Books;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Books;

/// <summary>A result of a book search for Fix match.</summary>
public sealed record BookMatchDto(string Provider, string Id, string Title, string? Subtitle, IReadOnlyList<string> Authors, IReadOnlyList<string> Narrators,
    string? Series, int? Year, string? Cover, double? DurationSeconds);

/// <summary>The result chosen: its provider and id, and the search it came from.</summary>
public sealed record BookMatchRequest(string Provider, string Id, string Query, string? Author = null);

/// <summary>A book's chapters as edited by hand: titles and where each starts. Each runs to the next one's start.</summary>
public sealed record ChapterEditRequest(IReadOnlyList<ChapterEdit> Chapters);

public sealed record ChapterEdit(string Title, double StartSeconds);

public sealed record ChapterShiftRequest(double Seconds);

/// <summary>Audible's chapters, moved by some seconds (its times often run ahead by the length of the opening credits).</summary>
public sealed record AudibleChaptersRequest(double ShiftSeconds = 0);

/// <summary>Fix match and the chapter tools for books. Admins only: a book's details and chapters are the household's.</summary>
public static class BookMatchEndpoints
{
    public static RouteGroupBuilder MapBookMatchEndpoints(this RouteGroupBuilder api)
    {
        var item = api.MapGroup("/items/{id:guid}").WithTags("Audiobooks").RequireAuthorization(FlexarrClaims.AdminPolicy);

        // Searching is a read: the main profile may look with settings locked, and unlocks to choose.
        api.MapGroup("/items/{id:guid}").WithTags("Audiobooks").RequireAuthorization(FlexarrClaims.MainProfilePolicy).MapGet("/book-match", async (Guid id, string? q, string? author, FlexarrDbContext db, BookProviders providers, CancellationToken ct) =>
        {
            if (await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id && i.Kind == MediaKind.Audiobook, ct) is not { } book)
            {
                return Results.NotFound();
            }

            author ??= await db.Credits.Where(c => c.MediaItemId == id && c.Role == CreditRole.Author).OrderBy(c => c.Order).Select(c => c.Person!.Name).FirstOrDefaultAsync(ct);
            var results = await providers.SearchAsync(q is { Length: > 0 } ? q : book.Title, author, ct);
            return Results.Ok(results.Select(ToDto).ToList());
        });

        item.MapPost("/book-match", async (Guid id, BookMatchRequest request, FlexarrDbContext db, BookProviders providers, MetadataRefresher refresher, CancellationToken ct) =>
        {
            var book = await db.MediaItems.Include(i => i.Book).Include(i => i.Genres).Include(i => i.Credits).ThenInclude(c => c.Person)
                .FirstOrDefaultAsync(i => i.Id == id && i.Kind == MediaKind.Audiobook, ct);
            if (book is null)
            {
                return Results.NotFound();
            }

            // Audible's full record by ASIN; anything else as the search found it (kept, so it's the same answer).
            var chosen = request.Provider == "audible" ? await providers.LookupAsync(request.Id, null, ct) : null;
            chosen ??= (await providers.SearchAsync(request.Query, request.Author, ct)).FirstOrDefault(m => m.Provider == request.Provider && m.Id == request.Id);
            if (chosen is null)
            {
                return Results.Problem("That result can't be found any more. Search again.", statusCode: 409);
            }

            var details = book.Book ??= new BookDetails { MediaItemId = book.Id };
            await refresher.ApplyOnlineAsync(book, details, chosen, overwrite: true, ct);
            book.MatchLocked = true;
            book.MetadataSource = chosen.Provider;
            book.MetadataUpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        item.MapPut("/chapters", async (Guid id, ChapterEditRequest request, FlexarrDbContext db, CancellationToken ct) =>
        {
            if (await db.BookDetails.FirstOrDefaultAsync(d => d.MediaItemId == id, ct) is not { } details)
            {
                return Results.NotFound();
            }

            var edits = request.Chapters.OrderBy(c => c.StartSeconds).ToList();
            if (edits.Count == 0 || edits.Any(c => string.IsNullOrWhiteSpace(c.Title) || c.Title.Length > 500 || !double.IsFinite(c.StartSeconds) || c.StartSeconds < 0)
                || edits.Select(c => c.StartSeconds).Distinct().Count() != edits.Count)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["chapters"] = ["Each chapter needs a title and its own start in the book."] });
            }

            await SaveAsync(db, id, details, [.. edits.Select(e => new BookChapter(e.Title.Trim(), e.StartSeconds, 0))], ChapterSource.Edited, ct);
            return Results.Ok(await BookQueries.Chapters(db, id).ToListAsync(ct));
        });

        item.MapPost("/chapters/shift", async (Guid id, ChapterShiftRequest request, FlexarrDbContext db, CancellationToken ct) =>
        {
            if (await db.BookDetails.FirstOrDefaultAsync(d => d.MediaItemId == id, ct) is not { } details)
            {
                return Results.NotFound();
            }

            if (!double.IsFinite(request.Seconds) || Math.Abs(request.Seconds) > 600)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["seconds"] = ["Shift by up to ten minutes either way."] });
            }

            var chapters = await db.MediaChapters.Where(c => c.MediaItemId == id).OrderBy(c => c.Index).ToListAsync(ct);
            // The first chapter still starts the book; the rest move.
            var moved = chapters.Select((c, i) => new BookChapter(c.Title, i == 0 ? 0 : Math.Max(0, c.StartSeconds + request.Seconds), 0)).ToList();
            await SaveAsync(db, id, details, moved, ChapterSource.Edited, ct);
            return Results.Ok(await BookQueries.Chapters(db, id).ToListAsync(ct));
        });

        // Audible's chapters, not saved: the chapter editor puts them in its form, and Save sends them.
        item.MapGet("/chapters/audible", async (Guid id, double? shiftSeconds, FlexarrDbContext db, MetadataRefresher refresher, CancellationToken ct) =>
        {
            if (await db.BookDetails.AsNoTracking().FirstOrDefaultAsync(d => d.MediaItemId == id, ct) is not { } details)
            {
                return Results.NotFound();
            }

            if (details.Asin is not { } asin)
            {
                return Results.Problem("This book has no Audible id. Fix match to find it on Audible first.", statusCode: 409);
            }

            if (BadShift(shiftSeconds ?? 0) is { } bad)
            {
                return bad;
            }

            return await refresher.AudibleChaptersAsync(details, asin, shiftSeconds ?? 0, ct, force: true) is { } chapters
                ? Results.Ok(chapters.Select((c, i) => new BookChapterDto(i, c.Title, c.StartSeconds, c.EndSeconds)).ToList())
                : Results.Problem("Audible has no chapters for this book.", statusCode: 404);
        });

        item.MapPost("/chapters/audible", async (Guid id, AudibleChaptersRequest request, FlexarrDbContext db, MetadataRefresher refresher, CancellationToken ct) =>
        {
            if (await db.BookDetails.FirstOrDefaultAsync(d => d.MediaItemId == id, ct) is not { } details)
            {
                return Results.NotFound();
            }

            if (details.Asin is not { } asin)
            {
                return Results.Problem("This book has no Audible id. Fix match to find it on Audible first.", statusCode: 409);
            }

            if (BadShift(request.ShiftSeconds) is { } bad)
            {
                return bad;
            }

            if (!await refresher.UseAudibleChaptersAsync(id, details, asin, request.ShiftSeconds, ct, force: true))
            {
                return Results.Problem("Audible has no chapters for this book.", statusCode: 404);
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(await BookQueries.Chapters(db, id).ToListAsync(ct));
        });

        return api;
    }

    /// <summary>A shift that isn't a number, or is more than ten minutes either way: refused, as <c>/chapters/shift</c> does.</summary>
    private static IResult? BadShift(double seconds) => !double.IsFinite(seconds) || Math.Abs(seconds) > 600
        ? Results.ValidationProblem(new Dictionary<string, string[]> { ["shiftSeconds"] = ["Shift by up to ten minutes either way."] })
        : null;

    private static BookMatchDto ToDto(BookMatch m) => new(m.Provider, m.Id, m.Title, m.Subtitle, m.Authors, m.Narrators,
        m.Series.Count > 0 ? m.Series[0].Sequence is null ? m.Series[0].Name : $"{m.Series[0].Name} #{m.Series[0].Sequence}" : null, m.ReleaseDate?.Year, m.Cover, m.DurationSeconds);

    /// <summary>Puts chapters in place of the book's, each running to the next one's start, the last to the end.</summary>
    private static async Task SaveAsync(FlexarrDbContext db, Guid bookId, BookDetails details, List<BookChapter> chapters, ChapterSource source, CancellationToken ct)
    {
        var length = details.DurationSeconds;
        var sorted = chapters.Where(c => length <= 0 || c.StartSeconds < length).OrderBy(c => c.StartSeconds).ToList();
        await db.MediaChapters.Where(c => c.MediaItemId == bookId).ExecuteDeleteAsync(ct);
        db.MediaChapters.AddRange(sorted.Select((c, i) => new MediaChapter
        {
            MediaItemId = bookId, Index = i, Title = c.Title, StartSeconds = i == 0 ? 0 : c.StartSeconds,
            EndSeconds = i + 1 < sorted.Count ? sorted[i + 1].StartSeconds : Math.Max(length, c.StartSeconds),
        }));
        details.ChapterSource = source;
        await db.SaveChangesAsync(ct);
    }
}
