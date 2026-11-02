using System.Globalization;
using System.Text;
using Flexarr.Api.Endpoints;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Metadata.Books;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Books;

/// <summary>A book of a series that the library doesn't have, as Audible lists it.</summary>
/// <param name="Sequence">Its number in the series as written ("3", "1.5"); null when Audible gives none.</param>
public sealed record MissingBookDto(string Asin, string Title, string? Sequence, IReadOnlyList<string> Authors, int? Year, string? Cover);

/// <param name="Answered">Audible was asked and answered; false when the series has no Audible id or Audible couldn't be asked.</param>
public sealed record BookSeriesMissingDto(IReadOnlyList<MissingBookDto> Books, bool Answered);

/// <summary>The books Audible lists in a series that the library lacks (no requests: just knowing).</summary>
public static class BookSeriesMissing
{
    /// <summary>A book the library has: its ASIN, its number in this series, and its title.</summary>
    internal sealed record Owned(string? Asin, string? Sequence, string Title);

    public static RouteGroupBuilder MapBookSeriesMissingEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/book-series/{id:guid}/missing", async (Guid id, FlexarrDbContext db, BookProviders providers, CancellationToken ct) =>
        {
            // The context's filters hide the series' library from a profile that can't see it.
            var series = await db.BookSeries.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (series is null || !await db.Libraries.AnyAsync(l => l.Id == series.LibraryId && l.Type == Core.Libraries.LibraryType.Audiobooks, ct))
            {
                return Results.NotFound();
            }

            // Every book of the library counts as had, those hidden from this profile too: they aren't missing.
            var inSeries = await db.BookSeriesEntries.AsNoTracking().IgnoreQueryFilters().Where(e => e.SeriesId == id)
                .Select(e => new Owned(e.Book!.Book!.Asin, e.Sequence, e.Book.Title)).ToListAsync(ct);
            if (series.ExternalId is null)
            {
                await FillAsinAsync(db, series, providers, ct);
            }

            if (series.ExternalId is not { } seriesAsin
                || await providers.SeriesBooksAsync(seriesAsin, DateOnly.FromDateTime(DateTime.UtcNow), ct) is not { } listed)
            {
                return Results.Ok(new BookSeriesMissingDto([], false));
            }

            var asins = listed.Select(b => b.Asin).ToList();
            var elsewhere = await db.MediaItems.AsNoTracking().IgnoreQueryFilters()
                .Where(i => i.LibraryId == series.LibraryId && i.Kind == MediaKind.Audiobook && i.Book!.Asin != null && asins.Contains(i.Book.Asin))
                .Select(i => new Owned(i.Book!.Asin, null, i.Title)).ToListAsync(ct);
            return Results.Ok(new BookSeriesMissingDto(Missing(listed, [.. inSeries, .. elsewhere]), true));
        }).WithTags("Audiobooks");

        return api;
    }

    /// <summary>
    /// Fills in a series' Audible id from a book of it that has an ASIN, and saves it. Leaves it empty when none has
    /// one, or Audible doesn't put it in a series by that name.
    /// </summary>
    private static async Task FillAsinAsync(FlexarrDbContext db, Core.Books.BookSeries series, BookProviders providers, CancellationToken ct)
    {
        var bookAsins = await db.BookSeriesEntries.AsNoTracking().IgnoreQueryFilters().Where(e => e.SeriesId == series.Id && e.Book!.Book!.Asin != null)
            .OrderBy(e => e.SortOrder).Select(e => e.Book!.Book!.Asin!).Take(3).ToListAsync(ct);
        foreach (var asin in bookAsins)
        {
            if (await providers.SeriesAsinAsync(asin, series.Name, ct) is { } found)
            {
                series.ExternalId = found;
                await db.SaveChangesAsync(ct);
                return;
            }
        }
    }

    /// <summary>
    /// The listed books the library doesn't have. A book is had when the library has its ASIN, a book at its number in
    /// the series, or one of the same title (another edition, narrator or store).
    /// </summary>
    internal static List<MissingBookDto> Missing(IReadOnlyList<SeriesBook> listed, IReadOnlyList<Owned> owned)
    {
        var asins = owned.Select(o => o.Asin).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var numbers = owned.Select(o => Number(o.Sequence)).OfType<decimal>().ToHashSet();
        var titles = owned.Select(o => Fold(o.Title)).Where(t => t.Length > 0).ToHashSet(StringComparer.Ordinal);
        return [.. listed.Where(b => !asins.Contains(b.Asin) && !(Number(b.Sequence) is { } n && numbers.Contains(n)) && !titles.Contains(Fold(b.Title)))
            .Select(b => new MissingBookDto(b.Asin, b.Title, b.Sequence, b.Authors, b.Year, b.Cover is null ? null : ImageEndpoints.RemoteUrl(b.Cover)))];
    }

    private static decimal? Number(string? sequence) =>
        decimal.TryParse(sequence?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>"The Thursday Murder Club: A Novel" → "thursdaymurderclub".</summary>
    private static string Fold(string title)
    {
        var main = title.Split([':', '('], 2)[0].Trim();
        if (main.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
        {
            main = main[4..];
        }

        return new string([.. main.Normalize(NormalizationForm.FormC).ToLowerInvariant().Where(char.IsLetterOrDigit)]);
    }
}
