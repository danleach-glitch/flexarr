using System.Text.Json;

namespace Flexarr.Integrations.Arr;

/// <summary>
/// What Seerr knows of a title's scores: Rotten Tomatoes (critics, whether it's Certified Fresh, audience) and, for
/// films, IMDb. Seerr answers <c>movie/{id}/ratingscombined</c> with <c>{rt, imdb}</c> and <c>tv/{id}/ratings</c> with
/// the Rotten Tomatoes object itself; any part may be missing.
/// </summary>
public sealed record SeerrRatings(int? CriticScore, bool Certified, int? AudienceScore, double? Imdb)
{
    public static string PathFor(bool movie, int tmdbId) => movie ? $"movie/{tmdbId}/ratingscombined" : $"tv/{tmdbId}/ratings";

    /// <returns>Null when the answer holds no score at all.</returns>
    public static SeerrRatings? Parse(JsonElement? ratings, bool movie)
    {
        if (ratings is not { ValueKind: JsonValueKind.Object } r)
        {
            return null;
        }

        var rt = movie ? (r.TryGetProperty("rt", out var x) && x.ValueKind == JsonValueKind.Object ? x : default) : r;
        var imdb = movie && r.TryGetProperty("imdb", out var i) && i.ValueKind == JsonValueKind.Object ? Number(i, "criticsScore") : null;
        var critics = Number(rt, "criticsScore") is >= 0 and <= 100 and var c ? (int?)(int)Math.Round(c) : null;
        var audience = Number(rt, "audienceScore") is >= 0 and <= 100 and var a ? (int?)(int)Math.Round(a) : null;
        var certified = critics is not null && rt.ValueKind == JsonValueKind.Object && rt.TryGetProperty("criticsRating", out var state)
            && state.ValueKind == JsonValueKind.String && string.Equals(state.GetString(), "Certified Fresh", StringComparison.OrdinalIgnoreCase);
        var imdbScore = imdb is > 0 and <= 10 ? Math.Round(imdb.Value, 1) : (double?)null;
        return critics is null && audience is null && imdbScore is null ? null : new SeerrRatings(critics, certified, audience, imdbScore);
    }

    private static double? Number(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
