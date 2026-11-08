using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Flexarr.Api.LiveTv;

/// <summary>Why asking Dispatcharr to download its guides didn't work out.</summary>
public enum GuideDownloadProblem
{
    None,
    KeyRefused,
    NotSupported,
    Unreachable,
    SourceFailed,
    TimedOut,
}

/// <summary>
/// Asks Dispatcharr to download its guide sources again, with an optional admin API key. Flexarr's usual sign-in can only
/// list and play channels, and Dispatcharr only keeps what its last download held, so this is what fills a guide that
/// ran out before Dispatcharr's next scheduled download.
/// </summary>
/// <remarks>
/// The client this uses doesn't follow redirects (see Program.cs), so the key only ever goes to the address saved with
/// it. Dispatcharr's source list holds each source's address, which can carry a login: only the fields read below are
/// looked at, and none of it is logged or passed on, nor is Dispatcharr's own message about a source, which can quote one.
/// </remarks>
public sealed class DispatcharrGuides(HttpClient http)
{
    /// <summary>The level Dispatcharr's admins have, which starting a download takes.</summary>
    private const int AdminLevel = 10;

    private sealed record Source(int Id, string? Status, string? UpdatedAt)
    {
        public bool Busy => Status is "fetching" or "parsing";
    }

    /// <summary>How often Dispatcharr is asked whether it has finished.</summary>
    internal TimeSpan CheckEvery { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long a download is waited for before the guide is read anyway.</summary>
    internal TimeSpan WaitAtMost { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Whether the key is an admin's on that server.</summary>
    public async Task<GuideDownloadProblem> CheckKeyAsync(Uri root, string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(root);
        try
        {
            using var response = await SendAsync(HttpMethod.Get, root, "api/accounts/users/me/", key, null, ct);
            if (Problem(response) is var problem and not GuideDownloadProblem.None)
            {
                return problem;
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("user_level", out var level)
                && level.TryGetInt32(out var value) && value >= AdminLevel ? GuideDownloadProblem.None : GuideDownloadProblem.KeyRefused;
        }
        catch (Exception ex) when (Unreachable(ex, ct))
        {
            return GuideDownloadProblem.Unreachable;
        }
    }

    /// <summary>Starts a download of each active source, and waits for them to finish.</summary>
    public async Task<GuideDownloadProblem> DownloadAsync(Uri root, string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(root);
        try
        {
            var (before, listing) = await SourcesAsync(root, key, ct);
            if (listing != GuideDownloadProblem.None)
            {
                return listing;
            }

            foreach (var source in before)
            {
                using var response = await SendAsync(HttpMethod.Post, root, "api/epg/import/", key, new { id = source.Id }, ct);
                if (Problem(response) is var problem and not GuideDownloadProblem.None)
                {
                    return problem;
                }
            }

            return await WaitAsync(root, key, before, ct);
        }
        catch (Exception ex) when (Unreachable(ex, ct))
        {
            return GuideDownloadProblem.Unreachable;
        }
    }

    private async Task<GuideDownloadProblem> WaitAsync(Uri root, string key, IReadOnlyList<Source> before, CancellationToken ct)
    {
        var last = before.ToDictionary(s => s.Id, s => s.UpdatedAt);
        var waiting = last.Keys.ToHashSet();
        var failed = false;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(WaitAtMost);
        try
        {
            while (waiting.Count > 0)
            {
                await Task.Delay(CheckEvery, limit.Token);
                var (sources, problem) = await SourcesAsync(root, key, limit.Token);
                if (problem != GuideDownloadProblem.None)
                {
                    return problem;
                }

                foreach (var source in sources)
                {
                    // Finished once it's been stamped since it was asked and isn't busy. A source that went away is
                    // removed below, so it isn't waited for.
                    if (waiting.Contains(source.Id) && !source.Busy && source.UpdatedAt != last[source.Id])
                    {
                        waiting.Remove(source.Id);
                        failed |= source.Status == "error";
                    }
                }

                waiting.IntersectWith(sources.Select(s => s.Id));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return GuideDownloadProblem.TimedOut;
        }

        return failed ? GuideDownloadProblem.SourceFailed : GuideDownloadProblem.None;
    }

    private async Task<(IReadOnlyList<Source> Sources, GuideDownloadProblem Problem)> SourcesAsync(Uri root, string key, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, root, "api/epg/sources/", key, null, ct);
        if (Problem(response) is var problem and not GuideDownloadProblem.None)
        {
            return ([], problem);
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        // A plain list, or a page of one should a version of Dispatcharr page it.
        var list = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("results", out var results) ? results : doc.RootElement;
        if (list.ValueKind != JsonValueKind.Array)
        {
            return ([], GuideDownloadProblem.NotSupported);
        }

        var sources = new List<Source>();
        foreach (var s in list.EnumerateArray())
        {
            // A dummy source has nothing to download, and Dispatcharr refuses to refresh one.
            if (s.TryGetProperty("id", out var id) && id.TryGetInt32(out var number)
                && !(s.TryGetProperty("is_active", out var active) && active.ValueKind == JsonValueKind.False)
                && Text(s, "source_type") != "dummy")
            {
                sources.Add(new Source(number, Text(s, "status"), Text(s, "updated_at")));
            }
        }

        return (sources, GuideDownloadProblem.None);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri root, string path, string key, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(root.ToString().TrimEnd('/') + "/" + path));
        request.Headers.Add("X-API-Key", key);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await http.SendAsync(request, ct);
    }

    private static GuideDownloadProblem Problem(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => GuideDownloadProblem.KeyRefused,
        // A version of Dispatcharr without these calls, or something else at that address.
        HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed => GuideDownloadProblem.NotSupported,
        _ => response.IsSuccessStatusCode ? GuideDownloadProblem.None : GuideDownloadProblem.Unreachable,
    };

    private static bool Unreachable(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or JsonException or FormatException or InvalidOperationException || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
