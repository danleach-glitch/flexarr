using System.Text.Json.Serialization;
using Flexarr.Core.Localization;
using Microsoft.Net.Http.Headers;

namespace Flexarr.Api.Localization;

/// <summary>A language the apps can offer: built in, kept or published. Absent from servers before 0.61.</summary>
public sealed record LanguageDto(string Code, string Name, string Direction, long Version, IReadOnlyDictionary<string, double> Coverage);

public sealed record LanguagesDto(IReadOnlyList<LanguageDto> Languages);

/// <summary>A language pack for one codebase, as the apps load it.</summary>
public sealed record LanguagePackDto(
    string Code,
    string Name,
    string Direction,
    long Version,
    [property: JsonPropertyName("messages")] IReadOnlyDictionary<string, string> Messages);

public static class LanguageEndpoints
{
    private static readonly string[] Scopes = ["web", "apple", "android", "server"];

    /// <summary>
    /// The languages on offer and their packs (docs/languages.md). Both need no sign-in: they're public text, and the
    /// screens before sign-in need them.
    /// </summary>
    public static RouteGroupBuilder MapLanguageEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/languages").WithTags("Languages").AllowAnonymous();

        group.MapGet("/", async (LanguageIndex index, CancellationToken ct) =>
        {
            var published = await index.RefreshAsync(ct);
            var byCode = new Dictionary<string, LanguageDto>(StringComparer.Ordinal)
            {
                ["en"] = new("en", "English", "ltr", 0, Scopes.ToDictionary(s => s, _ => 1.0)),
            };
            foreach (var pack in index.Catalog.Packs.Values)
            {
                byCode[pack.Code] = new(pack.Code, pack.Name, pack.Direction, pack.Version, pack.Coverage);
            }

            foreach (var e in published)
            {
                if (!byCode.TryGetValue(e.Code, out var have) || have.Version < e.Version)
                {
                    byCode[e.Code] = new(e.Code, e.Name, e.Direction, e.Version, e.Coverage);
                }
            }

            var list = byCode.Values.OrderBy(l => l.Code == "en" ? 0 : 1).ThenBy(l => l.Name, StringComparer.CurrentCulture).ToList();
            return Results.Ok(new LanguagesDto(list));
        });

        group.MapGet("/{code}", async (string code, string? scope, HttpContext http, LanguageIndex index, CancellationToken ct) =>
        {
            if (!LanguageCode.IsValid(code))
            {
                return Results.BadRequest();
            }

            if (scope is not null && !Scopes.Contains(scope))
            {
                return Results.BadRequest();
            }

            var pack = await index.EnsureAsync(code, ct);
            if (pack is null)
            {
                return Results.NotFound();
            }

            var etag = new EntityTagHeaderValue($"\"{pack.Code}-{pack.Version}-{scope ?? "all"}\"");
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers.ETag = etag.ToString();
            if (http.Request.GetTypedHeaders().IfNoneMatch.Any(t => t.Compare(etag, useStrongComparison: false)))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            var scoped = pack.Scoped(scope);
            return Results.Ok(new LanguagePackDto(pack.Code, pack.Name, pack.Direction, pack.Version, scoped.Messages));
        });

        return api;
    }
}
