using System.Collections.Concurrent;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Flexarr.Api.Localization;

/// <summary>
/// Each profile's chosen language, kept for a minute so a request doesn't read the database for it (a change through
/// PUT /me/language is seen at once; one made another way, such as a restore, within the minute).
/// </summary>
public sealed class ProfileLanguages(IServiceScopeFactory scopes, TimeProvider clock)
{
    private static readonly TimeSpan KeptFor = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, (string? Language, DateTimeOffset At)> _kept = new(StringComparer.Ordinal);

    public async ValueTask<string?> OfAsync(string profileId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (_kept.TryGetValue(profileId, out var kept) && now - kept.At < KeptFor)
        {
            return kept.Language;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var language = await db.Profiles.AsNoTracking().Where(p => p.Id == profileId).Select(p => p.Language).FirstOrDefaultAsync(ct);
        _kept[profileId] = (language, now);
        return language;
    }

    public void Changed(string profileId, string? language) => _kept[profileId] = (language, clock.GetUtcNow());
}

/// <summary>
/// Puts each request in a language (docs/languages.md, *Choosing a language*): the signed-in profile's, else the best
/// of the app's Accept-Language that this server has a pack for, else English.
/// </summary>
public static class RequestLanguage
{
    public static IApplicationBuilder UseFlexarrRequestLanguage(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            string? language = null;
            if (http.User.FindFirst(FlexarrClaims.Profile)?.Value is { } profileId)
            {
                language = await http.RequestServices.GetRequiredService<ProfileLanguages>().OfAsync(profileId, http.RequestAborted);
            }

            language ??= FromHeader(http.Request.Headers.AcceptLanguage, Loc.Catalog);
            Loc.Language = language;
            await next(http);
        });

    /// <summary>The first language in an Accept-Language header, best first, that's English or has a pack here.</summary>
    public static string? FromHeader(string? header, LanguageCatalog catalog)
    {
        if (string.IsNullOrWhiteSpace(header) || !StringWithQualityHeaderValue.TryParseList([header], out var values))
        {
            return null;
        }

        foreach (var value in values.Where(v => v.Quality is null or > 0).OrderByDescending(v => v.Quality ?? 1))
        {
            var tag = value.Value.Value;
            if (tag is null || tag == "*")
            {
                continue;
            }

            var code = Normalise(tag);
            if (code.StartsWith("en", StringComparison.Ordinal) && catalog.Find(code) is null)
            {
                return null;
            }

            if (catalog.Find(code) is { } pack)
            {
                return pack.Code;
            }
        }

        return null;
    }

    /// <summary>pt-br → pt-BR, zh-hans-cn → zh-Hans-CN: the casing packs are named with.</summary>
    public static string Normalise(string tag)
    {
        var parts = tag.Split('-', '_');
        for (var i = 1; i < parts.Length; i++)
        {
            parts[i] = parts[i].Length == 4 ? char.ToUpperInvariant(parts[i][0]) + parts[i][1..].ToLowerInvariant() : parts[i].ToUpperInvariant();
        }

        parts[0] = parts[0].ToLowerInvariant();
        return string.Join('-', parts);
    }
}
