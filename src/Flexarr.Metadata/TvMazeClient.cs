using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Flexarr.Metadata;

public sealed record TvMazeImage(string? Medium, string? Original);

public sealed record TvMazePerson(int Id, string Name, TvMazeImage? Image);

public sealed record TvMazeCharacter(int Id, string Name, TvMazeImage? Image);

public sealed record TvMazeCastMember(TvMazePerson Person, TvMazeCharacter Character);

public sealed record TvMazeCrewMember(string Type, TvMazePerson Person);

public sealed record TvMazeShow(int Id, string Name, TvMazeImage? Image, string? Summary);

/// <summary>Keyless TV metadata (cast and crew) from api.tvmaze.com, politely rate limited.</summary>
public sealed class TvMazeClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Uri Base = new("https://api.tvmaze.com/");
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _next = DateTime.MinValue;

    public Task<IReadOnlyList<TvMazeCastMember>?> GetCastAsync(int showId, CancellationToken ct) =>
        GetAsync<IReadOnlyList<TvMazeCastMember>>($"shows/{showId}/cast", ct);

    public Task<IReadOnlyList<TvMazeCrewMember>?> GetCrewAsync(int showId, CancellationToken ct) =>
        GetAsync<IReadOnlyList<TvMazeCrewMember>>($"shows/{showId}/crew", ct);

    public Task<TvMazeShow?> LookupByTvdbAsync(int tvdbId, CancellationToken ct) =>
        GetAsync<TvMazeShow>($"lookup/shows?thetvdb={tvdbId}", ct);

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // TVmaze allows ~20 calls per 10 seconds.
                var wait = _next - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }

                _next = DateTime.UtcNow.AddMilliseconds(550);
            }
            finally
            {
                Gate.Release();
            }

            using var response = await http.GetAsync(new Uri(Base, path), ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
        }

        return null;
    }
}
